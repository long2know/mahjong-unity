using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Bridge;
using Mahjong.Renderer.Contract;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Mahjong.Renderer.Tests
{
    public sealed class LifecycleTests
    {
        [Test]
        public void DuplicateEntityAndImpossibleHandPreservePreviousCompleteFrame()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame before = state.Frame;
            RendererFrame invalid = SourceFixtures.Copy(before, "RendererFrame");
            invalid.FrameId++;
            invalid.Entities[1].EntityId = invalid.Entities[0].EntityId;
            Assert.That(() => state.ApplyFrame(invalid), Throws.TypeOf<ContractException>());
            Assert.That(state.Frame, Is.SameAs(before));
            invalid = SourceFixtures.Copy(before, "RendererFrame");
            invalid.FrameId++;
            invalid.LocalHand.EntityIds = new[] { invalid.Entities[1].EntityId };
            Assert.That(() => state.ApplyFrame(invalid), Throws.TypeOf<ContractException>());
            Assert.That(state.Frame, Is.SameAs(before));
        }

        [Test]
        public void MissingLayoutWrongInstanceAndOldFramesAreRejected()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame invalid = SourceFixtures.Copy(state.Frame, "RendererFrame");
            invalid.InstanceId = "other-instance";
            Assert.That(() => state.ApplyFrame(invalid), Throws.TypeOf<ContractException>());
            invalid.InstanceId = state.InstanceId;
            ContractException stale = Assert.Throws<ContractException>(() => state.ApplyFrame(invalid));
            Assert.That(stale.Code, Is.EqualTo("stale-frame"));
            invalid.FrameId++;
            invalid.LayoutRevision++;
            ContractException layout = Assert.Throws<ContractException>(() => state.ApplyFrame(invalid));
            Assert.That(layout.Code, Is.EqualTo("missing-layout"));
        }

        [Test]
        public void FrameSequenceCannotResetOnLayoutRebuild()
        {
            RendererState state = SourceFixtures.State();
            RendererLayout layout = SourceFixtures.Copy(state.Layout, "RendererLayout");
            layout.LayoutRevision++;
            state.ApplyLayout(layout);
            RendererFrame old = SourceFixtures.Read<RendererFrame>("RendererFrame");
            old.LayoutRevision = layout.LayoutRevision;
            Assert.That(() => state.ApplyFrame(old), Throws.TypeOf<ContractException>());
            old.FrameId++;
            Assert.DoesNotThrow(() => state.ApplyFrame(old));
        }

        [Test]
        public void CompleteReplacementDeletesAbsentEntitiesAndClearRetiresEpoch()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
            next.FrameId++;
            next.Entities = Array.Empty<Entity>();
            next.LocalHand.EntityIds = Array.Empty<string>();
            state.ApplyFrame(next);
            Assert.That(state.Entities, Is.Empty);
            state.Clear(new Clear { Type = "clear", InstanceId = state.InstanceId, SessionId = "new-epoch", Reason = "disconnect" });
            Assert.That(state.Frame, Is.Null);
            Assert.That(state.Layout, Is.Null);
            Assert.That(state.Entities, Is.Empty);
            Assert.That(state.Actionable, Is.False);
            Assert.That(() => state.ApplyFrame(next), Throws.TypeOf<ContractException>());
            Assert.That(() => state.Clear(new Clear { Type = "clear", InstanceId = state.InstanceId,
                SessionId = "fixture-session", Reason = "join" }), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void AuthorityAndViewerRevisionChangesCannotCrossEpochSilently()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame invalid = SourceFixtures.Copy(state.Frame, "RendererFrame");
            invalid.FrameId++;
            invalid.Session.ActorSeat = 1;
            Assert.That(() => state.ApplyFrame(invalid), Throws.TypeOf<ContractException>());
            invalid = SourceFixtures.Copy(state.Frame, "RendererFrame");
            invalid.FrameId++;
            invalid.Session.ViewerRevision--;
            Assert.That(() => state.ApplyFrame(invalid), Throws.TypeOf<ContractException>());
            RendererFrame pov = SourceFixtures.Copy(state.Frame, "RendererFrame");
            pov.FrameId++;
            pov.Session.ViewSeat = 2;
            state.ApplyFrame(pov);
            Assert.That(state.Frame.Session.ActorSeat, Is.EqualTo(0));
            Assert.That(state.Frame.LocalHand.Seat, Is.EqualTo(0));
        }

        [Test]
        public void SourceUnownedChangshaHandAndConcealedForeignKnowledgeFailClosed()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Entities[1].Appearance.KnownFace = true;
            frame.Entities[1].Appearance.FaceIndex = 3;
            Assert.That(() => state.ApplyFrame(frame), Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Session.Connected = false;
            Assert.That(() => state.ApplyFrame(frame), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void ExplicitProjectedHandSubsetIsNotRejectedByInventedCompletenessRule()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.LocalHand.EntityIds = Array.Empty<string>();
            Assert.DoesNotThrow(() => state.ApplyFrame(frame));
        }

        [Test]
        public void StaleResizeAndDprMismatchDoNotChangeCurrentViewport()
        {
            RendererState state = SourceFixtures.State();
            Viewport before = state.Viewport;
            Assert.That(() => state.SetViewport(SourceFixtures.Copy(before, "Viewport")), Throws.TypeOf<ContractException>());
            Viewport invalid = SourceFixtures.Copy(before, "Viewport");
            invalid.ResizeRevision++;
            invalid.WidthPx--;
            Assert.That(() => state.SetViewport(invalid), Throws.TypeOf<ContractException>());
            Assert.That(state.Viewport, Is.SameAs(before));
        }

        [Test]
        public void DuplicateSlotForbiddenStorageAndInvalidPoseDoNotReplaceLayout()
        {
            RendererState state = SourceFixtures.State();
            RendererLayout before = state.Layout;
            RendererLayout invalid = SourceFixtures.Copy(before, "RendererLayout");
            invalid.LayoutRevision++;
            invalid.Slots = invalid.Slots.Concat(new[] { invalid.Slots[0] }).ToArray();
            Assert.That(() => state.ApplyLayout(invalid), Throws.TypeOf<ContractException>());
            invalid = SourceFixtures.Copy(before, "RendererLayout");
            invalid.LayoutRevision++;
            invalid.Slots[0].SlotName = "hiddenpool@0";
            Assert.That(() => state.ApplyLayout(invalid), Throws.TypeOf<ContractException>());
            invalid = SourceFixtures.Copy(before, "RendererLayout");
            invalid.LayoutRevision++;
            invalid.Slots[0].Poses[0].Rotation.W = 0;
            Assert.That(() => state.ApplyLayout(invalid), Throws.TypeOf<ContractException>());
            Assert.That(state.Layout, Is.SameAs(before));
        }

        [Test]
        public void ViewportRedrawCannotEmitAcceptedSessionFrameTwice()
        {
            var events = new List<JObject>();
            MahjongBridge bridge = ReceiptBridge(events);
            try
            {
                IEnumerator receipts = BeginReceipts(bridge);
                CommitBridge(bridge);
                RenderCameras(bridge);
                receipts.MoveNext();
                AssertRendered(events, "fixture-session", 1, 1);
                long generation = bridge.Scene.Generation;
                for (int n = 0; n < 3; n++)
                {
                    Viewport resized = SourceFixtures.Copy(bridge.State.Viewport, "Viewport");
                    resized.ResizeRevision++;
                    resized.HandBandCssPx = n == 1 ? 0 : resized.HeightCssPx / (n + 3);
                    bridge.SetViewport(SourceFixtures.Schema.Write(resized, "Viewport"));
                    CommitBridge(bridge);
                    Assert.That(bridge.Scene.Generation, Is.GreaterThan(generation));
                    generation = bridge.Scene.Generation;
                    Assert.That(bridge.Scene.ActiveResizeRevision, Is.EqualTo(resized.ResizeRevision));
                    Assert.That(bridge.Scene.HandCamera.enabled, Is.EqualTo(resized.HandBandCssPx > 0));
                    Assert.That(bridge.Scene.TableCamera.rect,
                        Is.EqualTo(bridge.Scene.ActivePlan.Viewport.BoardNormalized));
                    RenderCameras(bridge);
                    receipts.MoveNext();
                    Assert.That(Rendered(events).Count(), Is.EqualTo(1),
                        "A completed viewport generation must not notify fixture-session/frame 1 twice.");
                }

                RendererFrame next = SourceFixtures.Copy(bridge.State.Frame, "RendererFrame");
                next.FrameId++;
                next.Presentation.ViewMode = "flat";
                bridge.ApplyFrame(SourceFixtures.Schema.Write(next, "RendererFrame"));
                CommitBridge(bridge);
                RenderCameras(bridge);
                receipts.MoveNext();
                AssertRendered(events, "fixture-session", 2, 2);
                Assert.That(bridge.Scene.TableCamera.orthographic, Is.True);
                Assert.That(Rendered(events).Select(value => value["frameId"].Value<long>()),
                    Is.EqualTo(new long[] { 1, 2 }));
            }
            finally { EngineObjects.Release(bridge.gameObject); }
        }

        [Test]
        public void PendingFrameReceiptWaitsForCurrentViewportAndBothCameras()
        {
            var events = new List<JObject>();
            MahjongBridge bridge = ReceiptBridge(events);
            try
            {
                IEnumerator receipts = BeginReceipts(bridge);
                CommitBridge(bridge);
                receipts.MoveNext();
                Assert.That(Rendered(events), Is.Empty, "No camera completed this generation.");
                RenderCamera(bridge.Scene.TableCamera);
                receipts.MoveNext();
                Assert.That(Rendered(events), Is.Empty, "The enabled separate-hand camera has not rendered.");
                long pending = bridge.Scene.Generation;

                Viewport resized = SourceFixtures.Copy(bridge.State.Viewport, "Viewport");
                resized.ResizeRevision++;
                resized.HandBandCssPx = resized.HeightCssPx / 3;
                bridge.SetViewport(SourceFixtures.Schema.Write(resized, "Viewport"));
                RenderCamera(bridge.Scene.HandCamera);
                receipts.MoveNext();
                Assert.That(bridge.Scene.ActiveResizeRevision, Is.LessThan(resized.ResizeRevision));
                Assert.That(Rendered(events), Is.Empty,
                    "Old camera completion cannot acknowledge a frame pending a newer same-size viewport.");
                Assert.That(BridgeField<long>(bridge, "acknowledgedGeneration"), Is.Not.EqualTo(pending),
                    "An invalid viewport/camera state must leave the render pending.");

                CommitBridge(bridge);
                Assert.That(bridge.Scene.Generation, Is.GreaterThan(pending));
                Assert.That(bridge.Scene.ActiveResizeRevision, Is.EqualTo(resized.ResizeRevision));
                receipts.MoveNext();
                Assert.That(Rendered(events), Is.Empty, "Previous-generation callbacks must not acknowledge the new commit.");
                RenderCamera(bridge.Scene.TableCamera);
                receipts.MoveNext();
                Assert.That(Rendered(events), Is.Empty, "Both current-generation cameras are required.");
                RenderCamera(bridge.Scene.HandCamera);
                receipts.MoveNext();
                AssertRendered(events, "fixture-session", 1, 1);
                Assert.That(BridgeField<long>(bridge, "acknowledgedGeneration"), Is.EqualTo(bridge.Scene.Generation));
                receipts.MoveNext();
                Assert.That(Rendered(events).Count(), Is.EqualTo(1));
            }
            finally { EngineObjects.Release(bridge.gameObject); }
        }

        [Test]
        public void FreshSessionCanAcknowledgeRestartedFrameIdAfterClear()
        {
            var events = new List<JObject>();
            MahjongBridge bridge = ReceiptBridge(events);
            try
            {
                IEnumerator receipts = BeginReceipts(bridge);
                CommitBridge(bridge);
                RenderCameras(bridge);
                receipts.MoveNext();
                AssertRendered(events, "fixture-session", 1, 1);
                bridge.Clear(SourceFixtures.Schema.Write(new Clear
                {
                    Type = "clear", InstanceId = bridge.State.InstanceId, SessionId = "fresh-session", Reason = "join"
                }, "Clear"));
                receipts.MoveNext();
                Assert.That(bridge.Scene.ActiveFrame, Is.Null);
                Assert.That(Rendered(events).Count(), Is.EqualTo(1));

                RendererLayout layout = SourceFixtures.Read<RendererLayout>("RendererLayout");
                RendererFrame frame = SourceFixtures.Read<RendererFrame>("RendererFrame");
                layout.SessionId = frame.SessionId = "fresh-session";
                bridge.ApplyLayout(SourceFixtures.Schema.Write(layout, "RendererLayout"));
                bridge.ApplyFrame(SourceFixtures.Schema.Write(frame, "RendererFrame"));
                CommitBridge(bridge);
                receipts.MoveNext();
                Assert.That(Rendered(events).Count(), Is.EqualTo(1), "A fresh epoch still requires its own camera completion.");
                RenderCameras(bridge);
                receipts.MoveNext();
                AssertRendered(events, "fresh-session", 1, 2);
                Assert.That(Rendered(events).Select(value => value["sessionId"].Value<string>()),
                    Is.EqualTo(new[] { "fixture-session", "fresh-session" }));
            }
            finally { EngineObjects.Release(bridge.gameObject); }
        }

        private const BindingFlags BridgePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static MahjongBridge ReceiptBridge(List<JObject> events)
        {
            var root = new GameObject("Bridge receipt lifecycle test");
            root.SetActive(false);
            MahjongBridge bridge = root.AddComponent<MahjongBridge>();
            typeof(MahjongBridge).GetField("surfaceShader", BridgePrivate)
                .SetValue(bridge, Shader.Find("Mahjong/OriginalSurface"));
            root.SetActive(true);
            if (bridge.State == null) InvokeBridge(bridge, "Awake");
            Assert.That(bridge.State, Is.Not.Null);
            bridge.EditorEventSink = json => events.Add(JObject.Parse(json));
            string buildId = (string)typeof(MahjongBridge)
                .GetMethod("EmbeddedBuildId", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            bridge.Configure(SourceFixtures.Schema.Write(new Configure
            {
                Type = "configure", InstanceId = "fixture-instance", BuildId = buildId
            }, "Configure"));
            bridge.Clear(SourceFixtures.Schema.Write(new Clear
            {
                Type = "clear", InstanceId = bridge.State.InstanceId, SessionId = "fixture-session", Reason = "join"
            }, "Clear"));
            Viewport viewport = SourceFixtures.Read<Viewport>("Viewport");
            viewport.WidthCssPx = viewport.WidthPx = Screen.width;
            viewport.HeightCssPx = viewport.HeightPx = Screen.height;
            viewport.DevicePixelRatio = viewport.RenderPixelRatio = 1;
            viewport.HandBandCssPx = viewport.HeightCssPx / 4;
            bridge.SetViewport(SourceFixtures.Schema.Write(viewport, "Viewport"));
            bridge.ApplyLayout(SourceFixtures.Schema.Write(SourceFixtures.Read<RendererLayout>("RendererLayout"), "RendererLayout"));
            bridge.ApplyFrame(SourceFixtures.Schema.Write(SourceFixtures.Read<RendererFrame>("RendererFrame"), "RendererFrame"));
            Assert.That(events.Select(value => value["type"].Value<string>()), Is.EqualTo(new[] { "ready" }));
            return bridge;
        }

        private static IEnumerator BeginReceipts(MahjongBridge bridge)
        {
            // Drive the production end-of-frame coroutine with actual Camera.Render callbacks in EditMode.
            var receipts = (IEnumerator)InvokeBridge(bridge, "RenderReceipts");
            Assert.That(receipts.MoveNext(), Is.True);
            Assert.That(receipts.Current, Is.TypeOf<WaitForEndOfFrame>());
            return receipts;
        }

        private static object InvokeBridge(MahjongBridge bridge, string method) =>
            typeof(MahjongBridge).GetMethod(method, BridgePrivate).Invoke(bridge, null);

        private static T BridgeField<T>(MahjongBridge bridge, string name) =>
            (T)typeof(MahjongBridge).GetField(name, BridgePrivate).GetValue(bridge);

        private static void CommitBridge(MahjongBridge bridge)
        {
            typeof(MahjongBridge).GetField("nextVisual", BridgePrivate).SetValue(bridge, 0f);
            InvokeBridge(bridge, "LateUpdate");
            Assert.That(bridge.Scene.ActiveFrame, Is.SameAs(bridge.State.Frame));
            Assert.That(bridge.Scene.ActiveFrame, Is.Not.Null);
        }

        private static void RenderCameras(MahjongBridge bridge)
        {
            RenderCamera(bridge.Scene.TableCamera);
            Assert.That(BridgeField<long>(bridge, "tableGeneration"), Is.EqualTo(bridge.Scene.Generation),
                "The production Camera.onPostRender callback must observe the actual table camera.");
            if (!bridge.Scene.HandCamera.enabled) return;
            RenderCamera(bridge.Scene.HandCamera);
            Assert.That(BridgeField<long>(bridge, "handGeneration"), Is.EqualTo(bridge.Scene.Generation),
                "The production Camera.onPostRender callback must observe the actual hand camera.");
        }

        private static void RenderCamera(Camera camera)
        {
            int mask = camera.cullingMask;
            // Null graphics supports camera completion, but cannot submit the scene's geometry.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                camera.cullingMask = 0;
            try { camera.Render(); }
            finally { camera.cullingMask = mask; }
        }

        private static IEnumerable<JObject> Rendered(List<JObject> events) =>
            events.Where(value => value["type"].Value<string>() == "frameRendered");

        private static void AssertRendered(List<JObject> events, string session, long frame, int count)
        {
            JObject[] rendered = Rendered(events).ToArray();
            Assert.That(rendered.Length, Is.EqualTo(count));
            Assert.That(rendered.Last()["instanceId"].Value<string>(), Is.EqualTo("fixture-instance"));
            Assert.That(rendered.Last()["sessionId"].Value<string>(), Is.EqualTo(session));
            Assert.That(rendered.Last()["frameId"].Value<long>(), Is.EqualTo(frame));
            Assert.That(rendered.Last()["layoutRevision"].Value<long>(), Is.EqualTo(1));
            Assert.That(rendered.Last().Properties().Select(property => property.Name),
                Is.EquivalentTo(new[] { "contractVersion", "type", "instanceId", "sessionId", "frameId", "layoutRevision" }));
            Assert.That(events.Any(value => value["type"].Value<string>() == "error"), Is.False);
        }
    }
}
