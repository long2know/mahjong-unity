using System;
using System.Collections;
using System.Runtime.InteropServices;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using Mahjong.Renderer.Rendering;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Mahjong.Renderer.Bridge
{
    public sealed class MahjongBridge : MonoBehaviour
    {
        [SerializeField] private Shader surfaceShader;
        private SchemaValidator schema;
        private RendererState state;
        private SemanticActions actions;
        private RendererScene scene;
        private bool dirty;
        private float nextVisual;
        private long acknowledgedGeneration;
        private string acknowledgedSessionId;
        private long acknowledgedFrameId;
        private long tableGeneration;
        private long handGeneration;
        private string visibleFailure;
        private bool sendingError;
        private Coroutine renderReceipt;
        public RendererState State => state;
        public RendererScene Scene => scene;
        public Action<string> EditorEventSink { get; set; }

        [DllImport("__Internal")] private static extern int MahjongUnityEmit(string json);

        private void Awake()
        {
            gameObject.name = "MahjongBridge";
            try
            {
                schema = SchemaValidator.Load();
                string identity = EmbeddedBuildId();
                state = new RendererState(schema, identity);
                scene = new RendererScene(transform, surfaceShader);
                actions = new SemanticActions(state, action => Emit(action, "RendererAction"));
                actions.CuesChanged += () => scene.UpdateCues(actions);
                gameObject.AddComponent<PointerInput>().Initialize(state, actions, new CameraPicker(scene, state));
                Camera.onPostRender += CameraRendered;
                renderReceipt = StartCoroutine(RenderReceipts());
                Application.targetFrameRate = 60;
                QualitySettings.vSyncCount = 0;
            }
            catch (Exception e) { Fail(e); }
        }

        private static string EmbeddedBuildId()
        {
            TextAsset identity = Resources.Load<TextAsset>("renderer-build-identity");
            if (identity == null)
            {
#if UNITY_EDITOR
                return "editor-source";
#else
                throw new ContractException("renderer-failure", "Actual-editor build identity is missing.");
#endif
            }
            var value = StrictJson.Parse(identity.text) as JObject;
            if (value?["contractVersion"]?.Value<string>() != ContractVersion.Current ||
                value["unityVersion"]?.Value<string>() != ContractVersion.Editor ||
                value["unityRevision"]?.Value<string>() != ContractVersion.EditorRevision ||
                string.IsNullOrEmpty(value["buildId"]?.Value<string>()))
                throw new ContractException("renderer-failure", "Embedded build identity is incompatible.");
            return value["buildId"].Value<string>();
        }

        public void Configure(string json) => Receive<Configure>(json, "Configure", message =>
        {
            if (Application.unityVersion != ContractVersion.Editor)
                throw new ContractException("renderer-failure", "Renderer must run under the pinned Unity editor/player.");
            Emit(state.Configure(message), "Ready");
        });

        public void Clear(string json) => Receive<Clear>(json, "Clear", message =>
        {
            state.Clear(message);
            scene.Clear();
            dirty = false;
            visibleFailure = null;
            acknowledgedGeneration = scene.Generation;
            acknowledgedSessionId = null;
            acknowledgedFrameId = 0;
        });

        public void ApplyLayout(string json) => Receive<RendererLayout>(json, "RendererLayout", message =>
        {
            state.ApplyLayout(message);
            scene.Clear();
            dirty = true;
        });

        public void ApplyFrame(string json) => Receive<RendererFrame>(json, "RendererFrame", message =>
        {
            state.ApplyFrame(message);
            actions.Reconcile();
            dirty = true;
        });

        public void SetViewport(string json) => Receive<Viewport>(json, "Viewport", message =>
        {
            state.SetViewport(message);
            Screen.SetResolution((int)message.WidthPx, (int)message.HeightPx, false);
            dirty = true;
        });

        public void SetPlayback(string json) => Receive<Playback>(json, "Playback", message =>
        {
            state.SetPlayback(message);
            scene.Suspend(message.Suspended);
            if (!message.Suspended) dirty = true;
        });

        public void ReceiveActionResult(string json) => Receive<ActionResult>(json, "ActionResult", message => state.ReceiveActionResult(message));
        public void CancelInput(string json) => Receive<CancelInput>(json, "CancelInput", message => state.CancelInput(message));

        private void Receive<T>(string json, string definition, Action<T> apply)
        {
            try
            {
                if (state == null || scene == null)
                    throw new ContractException("renderer-failure", "Renderer initialization did not complete.");
                T message = schema.Read<T>(json, definition);
                apply(message);
            }
            catch (Exception e) { Fail(e); }
        }

        private void LateUpdate()
        {
            try { if (state != null && !state.Suspended) scene?.AdvanceVisual(); }
            catch (Exception e) { Fail(e); return; }
            if (!dirty || state == null || state.Suspended || state.Frame == null ||
                state.Layout == null || state.Viewport == null || Time.unscaledTime < nextVisual) return;
            try
            {
                LayoutPlan plan = BoardLayout.Build(state.Layout, state.Frame, state.Viewport);
                scene.Commit(plan, state.Frame, actions, state.Viewport.ResizeRevision);
                tableGeneration = 0;
                handGeneration = 0;
                dirty = false;
                nextVisual = Time.unscaledTime + 1f / 30;
                visibleFailure = null;
            }
            catch (Exception e) { dirty = false; Fail(e); }
        }

        private void CameraRendered(Camera camera)
        {
            if (scene == null) return;
            if (camera == scene.TableCamera) tableGeneration = scene.Generation;
            if (camera == scene.HandCamera) handGeneration = scene.Generation;
        }

        private IEnumerator RenderReceipts()
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                if (state?.Frame == null || state.Suspended || scene.ActiveFrame == null ||
                    state.Frame.FrameId != scene.ActiveFrame.FrameId || state.SessionId != scene.ActiveFrame.SessionId ||
                    scene.Generation == acknowledgedGeneration || !scene.TableCamera.enabled ||
                    tableGeneration != scene.Generation ||
                    scene.HandCamera.enabled && handGeneration != scene.Generation ||
                    state.Viewport == null || scene.ActiveResizeRevision != state.Viewport.ResizeRevision ||
                    Screen.width != state.Viewport.WidthPx || Screen.height != state.Viewport.HeightPx)
                    continue;
                acknowledgedGeneration = scene.Generation;
                // Viewport redraws complete separately, but the wire receipt identifies only the accepted frame.
                if (acknowledgedSessionId == scene.ActiveFrame.SessionId &&
                    acknowledgedFrameId == scene.ActiveFrame.FrameId)
                    continue;
                acknowledgedSessionId = scene.ActiveFrame.SessionId;
                acknowledgedFrameId = scene.ActiveFrame.FrameId;
                Emit(new FrameRendered
                {
                    Type = "frameRendered", InstanceId = state.InstanceId, SessionId = state.SessionId,
                    FrameId = scene.ActiveFrame.FrameId, LayoutRevision = scene.ActiveFrame.LayoutRevision
                }, "FrameRendered");
            }
        }

        private void Emit(object value, string definition)
        {
            string json = schema.Write(value, definition);
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (MahjongUnityEmit(json) != 1)
                    throw new ContractException("renderer-failure", "Browser MahjongUnityBridge.emit callback is unavailable.");
#else
                EditorEventSink?.Invoke(json);
#endif
            }
            catch (Exception e) { if (!sendingError) Fail(e); else visibleFailure = "Browser renderer callback failed."; }
        }

        private void Fail(Exception error)
        {
            string code = error is ContractException contract ? contract.Code : "renderer-failure";
            string message = error is ContractException ? error.Message : "Renderer operation failed (" + error.GetType().Name + ").";
            visibleFailure = message;
            state?.RevokeInput();
            Debug.LogError("[Mahjong renderer] " + code + ": " + message);
            if (schema == null || sendingError) return;
            sendingError = true;
            try
            {
                Emit(new RendererError
                {
                    Type = "error", InstanceId = state?.InstanceId ?? "unconfigured", SessionId = state?.SessionId,
                    Code = code, Message = message.Length > 1024 ? message.Substring(0, 1024) : message
                }, "RendererError");
            }
            finally { sendingError = false; }
        }

        private void OnGUI()
        {
            if (visibleFailure == null) return;
            GUI.Box(new Rect(12, 12, Mathf.Max(120, Screen.width - 24), 70), "Unity renderer unavailable");
            GUI.Label(new Rect(24, 40, Mathf.Max(96, Screen.width - 48), 35), visibleFailure);
        }

        private void OnDestroy()
        {
            Camera.onPostRender -= CameraRendered;
            if (renderReceipt != null) StopCoroutine(renderReceipt);
            state?.RevokeInput();
            scene?.Dispose();
        }
    }
}
