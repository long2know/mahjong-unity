using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using UnityEngine;

namespace Mahjong.Renderer.Rendering
{
    public sealed class RendererScene : IDisposable
    {
        private readonly ProceduralArt art;
        private readonly FrameBuffer[] buffers;
        private int active = -1;
        private readonly List<(EntityView View, RenderPose From, RenderPose To)> interpolation =
            new List<(EntityView, RenderPose, RenderPose)>();
        private float interpolationStarted;
        private bool snapNextFrame;
        public Camera TableCamera { get; }
        public Camera HandCamera { get; }
        public long Generation { get; private set; }
        public LayoutPlan ActivePlan { get; private set; }
        public RendererFrame ActiveFrame { get; private set; }
        public long ActiveResizeRevision { get; private set; }
        public IReadOnlyDictionary<string, EntityView> ActiveEntities =>
            active < 0 ? EmptyEntities : buffers[active].EntityLookup;
        private static readonly Dictionary<string, EntityView> EmptyEntities = new Dictionary<string, EntityView>();

        public RendererScene(Transform parent, Shader shader)
        {
            art = new ProceduralArt(shader);
            buffers = new[] { new FrameBuffer(parent, art, 0), new FrameBuffer(parent, art, 1) };
            TableCamera = MakeCamera(parent, "Table camera", RendererLayers.Table, 0);
            HandCamera = MakeCamera(parent, "Separate hand camera", RendererLayers.Hand, 1);
        }

        private static Camera MakeCamera(Transform parent, string name, int layer, int depth)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var camera = obj.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.055f, 0.07f);
            camera.cullingMask = 1 << layer;
            camera.fieldOfView = CameraFit.FieldOfView;
            camera.depth = depth;
            camera.nearClipPlane = 0.005f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.enabled = false;
            return camera;
        }

        public void Commit(LayoutPlan plan, RendererFrame frame, SemanticActions actions, long resizeRevision)
        {
            int next = active == 0 ? 1 : 0;
            FrameBuffer buffer = buffers[next];
            buffer.Stage(plan, frame);
            interpolation.Clear();
            bool interpolate = !snapNextFrame && active >= 0 && ActiveResizeRevision == resizeRevision &&
                ActiveFrame.FrameId != frame.FrameId &&
                ActiveFrame.SessionId == frame.SessionId &&
                ActiveFrame.Presentation.BoardLayout == frame.Presentation.BoardLayout &&
                ActiveFrame.Presentation.HandLayout == frame.Presentation.HandLayout &&
                ActiveFrame.Session.ViewSeat == frame.Session.ViewSeat;
            if (interpolate)
                foreach (var pair in buffer.EntityLookup)
                    if (buffers[active].EntityLookup.TryGetValue(pair.Key, out EntityView previous) &&
                        previous.Current.Kind == pair.Value.Current.Kind &&
                        previous.Root.layer == pair.Value.Root.layer)
                    {
                        RenderPose target = pair.Value.VisualPose;
                        interpolation.Add((pair.Value, previous.VisualPose, target));
                        pair.Value.SetVisualPose(previous.VisualPose);
                    }
            interpolationStarted = Time.unscaledTime;
            CameraPose fitted = CameraFit.Fit(plan.BoardBounds,
                plan.Viewport.BoardCss.width / plan.Viewport.BoardCss.height,
                frame.Presentation.ViewMode == "flat", frame.Presentation.LookDown, frame.Presentation.Zoom);
            TableCamera.rect = plan.Viewport.BoardNormalized;
            TableCamera.orthographic = frame.Presentation.ViewMode == "flat";
            TableCamera.orthographicSize = fitted.OrthographicSize;
            TableCamera.transform.SetPositionAndRotation(fitted.Position, fitted.Rotation);
            TableCamera.farClipPlane = Vector3.Distance(fitted.Position, plan.BoardBounds.center) + plan.BoardBounds.size.magnitude + 1;
            HandCamera.rect = plan.Viewport.HandNormalized;
            HandCamera.orthographic = true;
            HandCamera.orthographicSize = Mathf.Max(0.001f, plan.Viewport.HandCss.height * SourceCoordinates.Unit / 2);
            HandCamera.transform.SetPositionAndRotation(new Vector3(0, 10, 0), Quaternion.Euler(90, 0, 0));
            HandCamera.farClipPlane = 20;
            if (active >= 0) buffers[active].Clear();
            active = next;
            ActivePlan = plan;
            ActiveFrame = frame;
            ActiveResizeRevision = resizeRevision;
            Generation++;
            snapNextFrame = false;
            buffer.Root.SetActive(true);
            TableCamera.enabled = true;
            HandCamera.enabled = plan.Viewport.HandCss.height > 0;
            UpdateCues(actions);
            Physics.SyncTransforms();
        }

        public void AdvanceVisual()
        {
            if (interpolation.Count == 0) return;
            float progress = Mathf.Clamp01((Time.unscaledTime - interpolationStarted) * 30);
            foreach (var item in interpolation)
                item.View.SetVisualPose(new RenderPose(Vector3.Lerp(item.From.Position, item.To.Position, progress),
                    Quaternion.Slerp(item.From.Rotation, item.To.Rotation, progress), item.To.Size,
                    Mathf.Lerp(item.From.Scale, item.To.Scale, progress)));
            Physics.SyncTransforms();
            if (progress >= 1) interpolation.Clear();
        }

        public void UpdateCues(SemanticActions actions)
        {
            if (active < 0) return;
            buffers[active].Cues(actions, ActiveFrame);
        }

        public void Suspend(bool suspended)
        {
            if (suspended)
            {
                interpolation.Clear();
                snapNextFrame = true;
            }
            TableCamera.enabled = false;
            HandCamera.enabled = false;
            if (active >= 0) buffers[active].Root.SetActive(false);
        }

        public void Clear()
        {
            TableCamera.enabled = false;
            HandCamera.enabled = false;
            foreach (FrameBuffer buffer in buffers) buffer.Clear();
            interpolation.Clear();
            snapNextFrame = true;
            active = -1;
            ActiveFrame = null;
            ActivePlan = null;
            Generation++;
        }

        public void Dispose()
        {
            Clear();
            foreach (FrameBuffer buffer in buffers) buffer.Dispose();
            EngineObjects.Release(TableCamera.gameObject);
            EngineObjects.Release(HandCamera.gameObject);
            art.Dispose();
        }

        private sealed class FrameBuffer
        {
            public readonly GameObject Root;
            public readonly Dictionary<string, EntityView> EntityLookup = new Dictionary<string, EntityView>(StringComparer.Ordinal);
            private readonly List<EntityView> entities = new List<EntityView>();
            private readonly List<SlotView> slots = new List<SlotView>();
            private readonly Dictionary<string, SlotView> slotLookup = new Dictionary<string, SlotView>(StringComparer.Ordinal);
            private readonly LabelView[] labels;
            private readonly SurfaceView board;
            private readonly SurfaceView rail;
            private readonly SurfaceView handBackground;
            private readonly SurfaceView[] diceBody = new SurfaceView[2];
            private readonly SurfaceView[] diceFace = new SurfaceView[2];
            private readonly ProceduralArt art;

            public FrameBuffer(Transform parent, ProceduralArt art, int index)
            {
                this.art = art;
                Root = new GameObject("Atomic frame buffer " + index);
                Root.transform.SetParent(parent, false);
                Root.SetActive(false);
                board = new SurfaceView(Root.transform, "Original table", art.Box, art);
                rail = new SurfaceView(Root.transform, "Original rail", art.Box, art);
                handBackground = new SurfaceView(Root.transform, "Hand band", art.Quad, art);
                labels = Enumerable.Range(0, 8).Select(_ => new LabelView(Root.transform, art)).ToArray();
                for (int i = 0; i < 2; i++)
                {
                    diceBody[i] = new SurfaceView(Root.transform, "Public die", art.Box, art);
                    diceFace[i] = new SurfaceView(Root.transform, "Public die pips", art.Quad, art);
                    var target = diceBody[i].Object.AddComponent<PickTarget>();
                    target.Bind(PickKind.Dice, null);
                    diceBody[i].Object.AddComponent<BoxCollider>();
                }
            }

            public void Stage(LayoutPlan plan, RendererFrame frame)
            {
                Root.SetActive(false);
                EntityLookup.Clear();
                slotLookup.Clear();
                Bounds surface = frame.Presentation.BoardLayout == "classic" ?
                    new Bounds(Vector3.zero, new Vector3(1.74f, 0.06f, 1.74f)) : plan.BoardBounds;
                Vector3 center = new Vector3(surface.center.x, -0.035f, surface.center.z);
                board.Pose(center, Quaternion.identity, new Vector3(surface.size.x, 0.06f, surface.size.z), RendererLayers.Table);
                board.Paint(new Color(0.06f, 0.27f, 0.21f));
                rail.Pose(center - Vector3.up * 0.013f, Quaternion.identity,
                    new Vector3(surface.size.x + 0.065f, 0.07f, surface.size.z + 0.065f), RendererLayers.Table);
                rail.Paint(new Color(0.25f, 0.16f, 0.10f));
                handBackground.Pose(new Vector3(0, -0.001f, 0), Quaternion.identity,
                    new Vector3(plan.Viewport.HandCss.width * SourceCoordinates.Unit, 1,
                        plan.Viewport.HandCss.height * SourceCoordinates.Unit), RendererLayers.Hand);
                handBackground.Paint(new Color(0.075f, 0.10f, 0.14f));
                handBackground.Object.SetActive(plan.Viewport.HandCss.height > 0);
                int n = 0;
                foreach (EntityPlacement item in plan.Entities.Values)
                {
                    if (n == entities.Count) entities.Add(new EntityView(Root.transform, art));
                    entities[n].Apply(item, frame.Presentation.TileLabels);
                    EntityLookup.Add(item.Entity.EntityId, entities[n++]);
                }
                for (int i = n; i < entities.Count; i++) entities[i].Clear();
                n = 0;
                foreach (SlotPlacement item in plan.Slots.Values)
                {
                    if (n == slots.Count) slots.Add(new SlotView(Root.transform, art));
                    slots[n].Apply(item);
                    slotLookup.Add(item.Slot.SlotName, slots[n++]);
                }
                for (int i = n; i < slots.Count; i++) slots[i].Clear();
                foreach (LabelView label in labels) label.Clear();
                n = 0;
                foreach (SeatDisplay seat in frame.Seats)
                {
                    int side = SourceCoordinates.RelativeSeat(seat.Seat, frame.Session.ViewSeat);
                    Vector3 point = side == 0 ? new Vector3(0, 0.01f, surface.min.z + 0.04f) :
                        side == 1 ? new Vector3(surface.max.x - 0.04f, 0.01f, 0) :
                        side == 2 ? new Vector3(0, 0.01f, surface.max.z - 0.07f) :
                        new Vector3(surface.min.x + 0.04f, 0.01f, 0);
                    string score = seat.Score.HasValue ? seat.Score.Value.ToString("0", CultureInfo.InvariantCulture) : "-";
                    string nick = string.IsNullOrEmpty(seat.Nick) ? "" : " " + seat.Nick.Substring(0, Mathf.Min(16, seat.Nick.Length));
                    labels[n++].Apply("SEAT " + seat.Seat + nick + " " + score, 0.027f, point,
                        Quaternion.AngleAxis(-90 * side, Vector3.up), RendererLayers.Table,
                        frame.Turn?.ActiveSeat == seat.Seat ? new Color(1, 0.8f, 0.35f) : new Color(0.9f, 0.93f, 0.91f));
                }
                string phase = frame.Turn?.Phase ?? "WAITING";
                labels[4].Apply(phase, 0.025f, new Vector3(0, 0.003f, 0.14f), Quaternion.identity,
                    RendererLayers.Table, new Color(0.85f, 0.9f, 0.85f));
                if (frame.Match != null)
                    labels[5].Apply("DEALER " + frame.Match.Dealer + " HONBA " + frame.Match.Honba, 0.022f,
                        new Vector3(0, 0.003f, -0.15f), Quaternion.identity, RendererLayers.Table, Color.white);
                if (frame.Pickup != null)
                    labels[6].Apply("PICKUP " + frame.Pickup.Count + " " + frame.Pickup.DealMode, 0.022f,
                        new Vector3(0, 0.003f, -0.10f), Quaternion.identity, RendererLayers.Table,
                        frame.Pickup.CanTake ? new Color(0.5f, 0.95f, 1) : Color.gray);
                if (plan.Viewport.HandCss.height > 0)
                    labels[7].Apply("HAND", 0.04f,
                        new Vector3(0, 0.001f, plan.Viewport.HandCss.height * SourceCoordinates.Unit / 2 - 0.06f),
                        Quaternion.identity, RendererLayers.Hand, new Color(0.65f, 0.75f, 0.85f));
                for (int i = 0; i < 2; i++)
                {
                    diceBody[i].Object.SetActive(frame.Dice != null);
                    diceFace[i].Object.SetActive(frame.Dice != null);
                    if (frame.Dice == null) continue;
                    Vector3 position = new Vector3((i * 2 - 1) * 0.05f, 0.036f, 0);
                    diceBody[i].Pose(position, Quaternion.identity, new Vector3(0.065f, 0.065f, 0.065f), RendererLayers.Table);
                    diceBody[i].Paint(frame.Dice.CanRoll ? new Color(1, 0.9f, 0.65f) : Color.white);
                    diceFace[i].Pose(position + new Vector3(0, 0.0326f, 0), Quaternion.identity,
                        new Vector3(0.058f, 1, 0.058f), RendererLayers.Table);
                    diceFace[i].Paint(Color.white, frame.Dice.State == "rolled" ? art.Dice[frame.Dice.Values[i] - 1] : null);
                }
            }

            public void Cues(SemanticActions actions, RendererFrame frame)
            {
                foreach (var pair in EntityLookup)
                    pair.Value.UpdateCue(actions.Selection.Contains(pair.Key), actions.Hovered == pair.Key);
                var preview = new HashSet<string>(frame.Pickup?.BatchPreviewSlotNames ?? Array.Empty<string>(), StringComparer.Ordinal);
                foreach (var pair in slotLookup)
                    pair.Value.UpdateCue(actions.DragTarget == pair.Key, preview.Contains(pair.Key));
            }

            public void Clear()
            {
                Root.SetActive(false);
                foreach (EntityView entity in entities) entity.Clear();
                foreach (SlotView slot in slots) slot.Clear();
                foreach (LabelView label in labels) label.Clear();
                EntityLookup.Clear();
                slotLookup.Clear();
            }

            public void Dispose()
            {
                foreach (LabelView label in labels) label.Dispose();
                EngineObjects.Release(Root);
            }
        }
    }
}
