using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Renderer.Art;
using Mahjong.Renderer.Bridge;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Mahjong.Renderer.Layout;
using Mahjong.Renderer.Rendering;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mahjong.Renderer.Tests
{
    public sealed class ActionTests
    {
        [Test]
        public void OrdinaryTouchDiscardsOnceAndCannotReplayAsSyntheticMouseOrSecondRelease()
        {
            WithPointer((pointer, state, position, emitted) =>
            {
                PointerCall(pointer, "Begin", position, true, 0);
                PointerCall(pointer, "End", position, false);
                PointerCall(pointer, "End", position, false);
                PointerCall(pointer, "Begin", position, false, -1);
                PointerCall(pointer, "End", position, false);
                Assert.That(emitted.Select(action => action.Intent["kind"].Value<string>()),
                    Is.EqualTo(new[] { "discard" }));
                Assert.That(emitted[0].Intent["entityId"].Value<string>(), Is.EqualTo("fixture-own-tile"));
            });
        }

        [TestCase("moved")]
        [TestCase("canceled")]
        [TestCase("long-pressed")]
        [TestCase("permission-lost")]
        [TestCase("resized")]
        public void NonTapOrStaleTouchCannotDiscard(string reason)
        {
            WithPointer((pointer, state, position, emitted) =>
            {
                PointerCall(pointer, "Begin", position, true, 0);
                if (reason == "moved") PointerCall(pointer, "Move", position + new Vector2(Screen.width, 0));
                else if (reason == "canceled") pointer.Reset();
                else if (reason == "long-pressed")
                {
                    typeof(PointerInput).GetField("started", BindingFlags.NonPublic | BindingFlags.Instance)
                        .SetValue(pointer, Time.unscaledTime - 1);
                    PointerCall(pointer, "Move", position);
                }
                else if (reason == "resized")
                {
                    Viewport next = SourceFixtures.Copy(state.Viewport, "Viewport");
                    next.ResizeRevision++;
                    state.SetViewport(next);
                }
                else
                {
                    RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
                    next.FrameId++;
                    next.Entities[0].Interaction.CanDiscard = false;
                    state.ApplyFrame(next);
                }
                PointerCall(pointer, "End", position, false);
                Assert.That(emitted.Any(action => action.Intent["kind"].Value<string>() == "discard"), Is.False, reason);
            });
        }

        [Test]
        public void MouseRetainsSelectThenDoubleClickDiscard()
        {
            WithPointer((pointer, state, position, emitted) =>
            {
                PointerCall(pointer, "Begin", position, false, -1);
                PointerCall(pointer, "End", position, false);
                Assert.That(emitted.Single().Intent["kind"].Value<string>(), Is.EqualTo("select"));
                PointerCall(pointer, "Begin", position, false, -1);
                PointerCall(pointer, "End", position, false);
                Assert.That(emitted.Select(action => action.Intent["kind"].Value<string>()),
                    Is.EqualTo(new[] { "select", "discard" }));
            });
        }

        private static void PointerCall(PointerInput pointer, string method, params object[] arguments) =>
            typeof(PointerInput).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(pointer, arguments);

        private static void WithPointer(System.Action<PointerInput, RendererState, Vector2, List<RendererAction>> test)
        {
            RendererState state = SourceFixtures.State();
            var emitted = new List<RendererAction>();
            var parent = new GameObject("Native pointer contract");
            var scene = new RendererScene(parent.transform, Shader.Find("Mahjong/OriginalSurface"));
            bool simulatedMouse = UnityEngine.Input.simulateMouseWithTouches;
            try
            {
                var actions = new SemanticActions(state, emitted.Add);
                scene.Commit(BoardLayout.Build(state.Layout, state.Frame, state.Viewport), state.Frame, actions,
                    state.Viewport.ResizeRevision);
                scene.TableCamera.pixelRect = new Rect(0, 150, 800, 600);
                scene.HandCamera.pixelRect = new Rect(0, 0, 800, 140);
                Physics.SyncTransforms();
                var pointer = parent.AddComponent<PointerInput>();
                pointer.Initialize(state, actions, new CameraPicker(scene, state));
                Vector2 position = scene.HandCamera.WorldToScreenPoint(scene.ActiveEntities["fixture-own-tile"].Root.transform.position);
                test(pointer, state, position, emitted);
            }
            finally
            {
                scene.Dispose();
                EngineObjects.Release(parent);
                UnityEngine.Input.simulateMouseWithTouches = simulatedMouse;
            }
        }

        [Test]
        public void SemanticDiscardHasOnlyRendererHandleAndCurrentContext()
        {
            RendererState state = SourceFixtures.State();
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            Assert.That(actions.Discard("fixture-concealed-wall"), Is.False);
            Assert.That(actions.Discard("fixture-own-tile"), Is.True);
            RendererAction action = emitted.Single();
            JObject json = JObject.Parse(SourceFixtures.Schema.Write(action, "RendererAction"));
            Assert.That(action.ContextFrameId, Is.EqualTo(state.Frame.FrameId));
            Assert.That(action.InstanceId, Is.EqualTo(state.InstanceId));
            Assert.That(action.SessionId, Is.EqualTo(state.SessionId));
            Assert.That(((JObject)json["intent"]).Properties().Select(property => property.Name),
                Is.EquivalentTo(new[] { "kind", "entityId" }));
            Assert.That(json["actorSeat"], Is.Null);
            Assert.That(json["tileId"], Is.Null);
            Assert.That(actions.BeginDrag("fixture-own-tile"), Is.False);
            Assert.That(actions.Flip("fixture-own-tile", 1), Is.False);
        }

        [Test]
        public void RelayDragTargetsCanonicalSlotsAndResizeCancelsCurrentGesture()
        {
            RendererState state = SourceFixtures.State(true);
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            Assert.That(actions.BeginDrag("fixture-own-tile"), Is.True);
            RendererFrame claimed = SourceFixtures.Copy(state.Frame, "RendererFrame");
            claimed.FrameId++;
            claimed.Entities[0].Interaction.CanSelect = false;
            claimed.Entities[0].Interaction.CanFlip = false;
            claimed.Entities[0].Interaction.CanDrag = true;
            state.ApplyFrame(claimed);
            actions.Reconcile();
            Assert.That(actions.Dragging, Is.True, "An owned claimed frame retains only active-drag permission.");
            Assert.That(actions.Select("fixture-own-tile", false), Is.False);
            Assert.That(actions.Flip("fixture-own-tile", 1), Is.False);
            Assert.That(emitted.Count, Is.EqualTo(1), "Reconcile must not emit endDrag before physical release.");
            Assert.That(actions.DragToSlot("hiddenpool@0"), Is.False);
            Assert.That(actions.DragToSlot("wall.0.1@1"), Is.True);
            Viewport resized = SourceFixtures.Copy(state.Viewport, "Viewport");
            resized.ResizeRevision++;
            state.SetViewport(resized);
            Assert.That(actions.Dragging, Is.False);
            Assert.That(emitted.Last().Intent["kind"].Value<string>(), Is.EqualTo("endDrag"));
            Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
            Assert.That(emitted[0].Intent["gestureId"], Is.EqualTo(emitted.Last().Intent["gestureId"]));
            Assert.That(emitted[1].Intent.Properties().Select(property => property.Name),
                Is.EquivalentTo(new[] { "kind", "gestureId", "slotName" }));
        }

        [Test]
        public void ResultsAreSingleUseAndSubmittedDoesNotMoveAnyTile()
        {
            RendererState state = SourceFixtures.State();
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            Entity before = state.Entities["fixture-own-tile"];
            actions.Discard(before.EntityId);
            var result = new ActionResult { Type = "actionResult", InstanceId = state.InstanceId,
                SessionId = state.SessionId, ActionId = emitted[0].ActionId, Status = "submitted" };
            state.ReceiveActionResult(result);
            Assert.That(state.Entities[before.EntityId], Is.SameAs(before));
            Assert.That(state.Entities[before.EntityId].SlotName, Is.EqualTo("hand.0@0"));
            Assert.That(() => state.ReceiveActionResult(result), Throws.TypeOf<ContractException>());
            result.ActionId = "invented-action";
            Assert.That(() => state.ReceiveActionResult(result), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void RemovalSuspensionAndSessionResetRevokeSelectionAndActions()
        {
            RendererState state = SourceFixtures.State(true);
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            actions.Select("fixture-own-tile", false);
            Assert.That(actions.Selection, Contains.Item("fixture-own-tile"));
            state.SetPlayback(new Playback { Type = "playback", InstanceId = state.InstanceId, Suspended = true });
            Assert.That(actions.Selection, Is.Empty);
            Assert.That(actions.Discard("fixture-own-tile"), Is.False);
            state.SetPlayback(new Playback { Type = "playback", InstanceId = state.InstanceId, Suspended = false });
            state.Clear(new Clear { Type = "clear", InstanceId = state.InstanceId, SessionId = "next", Reason = "authority-change" });
            Assert.That(actions.Selection, Is.Empty);
            Assert.That(actions.BeginDrag("fixture-own-tile"), Is.False);
            Assert.That(() => state.ReceiveActionResult(new ActionResult { Type = "actionResult", InstanceId = state.InstanceId,
                SessionId = "fixture-session", ActionId = emitted[0].ActionId, Status = "local" }), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void CurrentSourceFlagsWinOverOldSelection()
        {
            RendererState state = SourceFixtures.State(true);
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            actions.Select("fixture-own-tile", false);
            RendererFrame next = SourceFixtures.Copy(state.Frame, "RendererFrame");
            next.FrameId++;
            next.Entities[0].Interaction = new Interaction();
            state.ApplyFrame(next);
            actions.Reconcile();
            Assert.That(actions.Selection, Is.Empty);
            Assert.That(actions.Discard("fixture-own-tile"), Is.False);
            Assert.That(actions.Flip("fixture-own-tile", -1), Is.False);
            Assert.That(actions.BeginDrag("fixture-own-tile"), Is.False);
        }

        [Test]
        public void LeavingAValidDropTargetCannotCommitThePreviouslyHoveredSlot()
        {
            RendererState state = SourceFixtures.State(true);
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            actions.BeginDrag("fixture-own-tile");
            actions.DragToSlot("wall.0.1@1");
            actions.ClearDragTargetCue();
            Assert.That(actions.DragTarget, Is.Null);
            actions.EndDrag(true);
            Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
        }

        [Test]
        public void BridgeRejectedIntermediateTargetRecoversWithoutEndingTheOwnedGesture()
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                Assert.That(actions.BeginDrag("fixture-own-tile"), Is.True);
                Resolve(bridge, emitted.Last());
                RendererFrame claimed = SourceFixtures.Copy(bridge.State.Frame, "RendererFrame");
                claimed.FrameId++;
                claimed.Entities[0].Interaction.CanSelect = false;
                claimed.Entities[0].Interaction.CanFlip = false;
                bridge.ApplyFrame(SourceFixtures.Schema.Write(claimed, "RendererFrame"));
                Assert.That(actions.Dragging, Is.True);
                Assert.That(actions.Select("fixture-own-tile", false), Is.False);
                Assert.That(actions.Flip("fixture-own-tile", 1), Is.False);

                Assert.That(actions.DragToSlot("wall.0.1@1"), Is.True);
                Resolve(bridge, emitted.Last(), "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.True, "A rejected intermediate slot is not a pointer release.");
                Assert.That(actions.DragTarget, Is.Null);
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.Zero);
                Assert.That(bridge.State.Entities["fixture-own-tile"].SlotName, Is.EqualTo("hand.0@0"));

                Assert.That(actions.DragToSlot("discard.0@0"), Is.True);
                Resolve(bridge, emitted.Last());
                Assert.That(actions.EndDrag(true), Is.True);
                Assert.That(actions.EndDrag(true), Is.False, "The real release must not finish a gesture twice.");
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "beginDrag"), Is.EqualTo(1));
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.EqualTo(1));
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.True);
                Assert.That(emitted.Last().Intent["gestureId"], Is.EqualTo(emitted.First().Intent["gestureId"]));
            });
        }

        [Test]
        public void BridgeLateOldCandidateRejectionCannotClearTheNewAcceptedTarget()
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("wall.0.1@1");
                RendererAction oldCandidate = emitted.Last();
                actions.DragToSlot("discard.0@0");
                Resolve(bridge, emitted.Last());
                Resolve(bridge, oldCandidate, "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.True);
                Assert.That(actions.DragTarget, Is.EqualTo("discard.0@0"));
                actions.EndDrag(true);
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.True);
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.EqualTo(1));
            });
        }

        [Test]
        public void BridgeLatePreviousGestureCandidateCannotCancelTheNewGesture()
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("wall.0.1@1");
                RendererAction oldCandidate = emitted.Last();
                actions.EndDrag(false);
                Resolve(bridge, emitted.Last());
                actions.BeginDrag("fixture-own-tile");
                RendererAction newBegin = emitted.Last();
                Resolve(bridge, newBegin);
                actions.DragToSlot("discard.0@0");
                Resolve(bridge, emitted.Last());
                Resolve(bridge, oldCandidate, "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.True);
                Assert.That(actions.DragTarget, Is.EqualTo("discard.0@0"));
                actions.EndDrag(true);
                Assert.That(emitted.Last().Intent["gestureId"], Is.EqualTo(newBegin.Intent["gestureId"]));
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.True);
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.EqualTo(2));
            });
        }

        [Test]
        public void BridgeRejectedCurrentCandidateCannotCommitThePreviouslyAcceptedTarget()
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("discard.0@0");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("wall.0.1@1");
                Resolve(bridge, emitted.Last(), "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.True);
                Assert.That(actions.DragTarget, Is.Null);
                actions.EndDrag(true);
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.EqualTo(1));
            });
        }

        [Test]
        public void BridgeReleaseCannotCommitAnUnresolvedCandidate()
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("discard.0@0");
                RendererAction candidate = emitted.Last();
                actions.EndDrag(true);
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
                Resolve(bridge, candidate, "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.False);
                Assert.That(emitted.Count(action => action.Intent["kind"].Value<string>() == "endDrag"), Is.EqualTo(1));
            });
        }

        [TestCase("stale-session")]
        [TestCase("stale-entity")]
        [TestCase("not-actionable")]
        [TestCase("protocol-unavailable")]
        public void BridgeNonCandidateRejectionsStillCancelTheGesture(string reason)
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("discard.0@0");
                Resolve(bridge, emitted.Last(), "rejected", reason);
                Assert.That(actions.Dragging, Is.False);
                Assert.That(actions.DragTarget, Is.Null);
                Assert.That(actions.Selection, Is.Empty);
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
            });
        }

        [TestCase("beginDrag", "rejected", "invalid-target")]
        [TestCase("dragToSlot", "ignored", "invalid-target")]
        [TestCase("dragToSlot", "rejected", null)]
        public void BridgeUnknownOrNonCandidateInvalidTargetsStillFailClosed(string kind, string status, string reason)
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                if (kind == "dragToSlot")
                {
                    Resolve(bridge, emitted.Last());
                    actions.DragToSlot("discard.0@0");
                }
                Resolve(bridge, emitted.Last(), status, reason);
                Assert.That(actions.Dragging, Is.False);
                Assert.That(emitted.Last().Intent["commit"].Value<bool>(), Is.False);
                Resolve(bridge, emitted.Last(), "rejected", "invalid-target");
                Assert.That(actions.Dragging, Is.False, "An endDrag rejection is not a candidate acknowledgement.");
            });
        }

        [TestCase("actor-change")]
        [TestCase("disconnect")]
        [TestCase("stale-frame")]
        [TestCase("session-reset")]
        [TestCase("permission-loss")]
        public void BridgeTrueAuthorityOrSessionLossCancelsRatherThanRecovering(string loss)
        {
            WithRelayBridge((bridge, actions, emitted) =>
            {
                actions.BeginDrag("fixture-own-tile");
                Resolve(bridge, emitted.Last());
                actions.DragToSlot("discard.0@0");
                Resolve(bridge, emitted.Last());
                RendererFrame frame = SourceFixtures.Copy(bridge.State.Frame, "RendererFrame");
                if (loss == "session-reset")
                    bridge.Clear(SourceFixtures.Schema.Write(new Clear
                    {
                        Type = "clear", InstanceId = bridge.State.InstanceId,
                        SessionId = "replacement-session", Reason = "authority-change"
                    }, "Clear"));
                else
                {
                    if (loss != "stale-frame") frame.FrameId++;
                    if (loss == "actor-change")
                    {
                        frame.Session.ActorSeat = 1;
                        LogAssert.Expect(LogType.Error, "[Mahjong renderer] invalid-message: Room, actor or protocol change requires Clear with a new session epoch.");
                    }
                    if (loss == "stale-frame")
                        LogAssert.Expect(LogType.Error, "[Mahjong renderer] stale-frame: Frame identifier is not newer than the accepted frame.");
                    if (loss == "disconnect")
                    {
                        frame.Session.Connected = false;
                        frame.Session.SnapshotReady = false;
                        frame.Entities = System.Array.Empty<Entity>();
                        frame.LocalHand.EntityIds = System.Array.Empty<string>();
                        frame.LocalHand.DrawnEntityId = null;
                        if (frame.Dice != null) frame.Dice.CanRoll = false;
                        if (frame.Pickup != null) frame.Pickup.CanTake = false;
                    }
                    if (loss == "disconnect" || loss == "permission-loss")
                        foreach (Entity entity in frame.Entities) entity.Interaction = new Interaction();
                    bridge.ApplyFrame(SourceFixtures.Schema.Write(frame, "RendererFrame"));
                }
                Assert.That(actions.Dragging, Is.False);
                Assert.That(actions.DragTarget, Is.Null);
                Assert.That(actions.Selection, Is.Empty);
                Assert.That(emitted.Where(action => action.Intent["kind"].Value<string>() == "endDrag")
                    .All(action => !action.Intent["commit"].Value<bool>()), Is.True);
                Assert.That(actions.BeginDrag("fixture-concealed-wall"), Is.False);
                if (loss != "permission-loss") Assert.That(bridge.State.Actionable, Is.False);
            });
        }

        [Test]
        public void MultiselectionCannotBypassSourceCanFlipMultiple()
        {
            RendererState state = SourceFixtures.State(true);
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Entities[1].Interaction.CanSelect = true;
            frame.Entities[1].Interaction.CanFlip = true;
            state.ApplyFrame(frame);
            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            actions.Select("fixture-own-tile", false);
            actions.Select("fixture-concealed-wall", true);
            Assert.That(actions.Selection.Count, Is.EqualTo(2));
            Assert.That(actions.Flip("fixture-own-tile", 1), Is.False);
            Assert.That(emitted.Any(value => value.Intent["kind"].Value<string>() == "flip"), Is.False);
        }

        private const BindingFlags BridgePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void WithRelayBridge(System.Action<MahjongBridge, SemanticActions, List<RendererAction>> test)
        {
            var root = new GameObject("Relay action result test");
            root.SetActive(false);
            MahjongBridge bridge = root.AddComponent<MahjongBridge>();
            typeof(MahjongBridge).GetField("surfaceShader", BridgePrivate)
                .SetValue(bridge, Shader.Find("Mahjong/OriginalSurface"));
            try
            {
                root.SetActive(true);
                if (bridge.State == null) typeof(MahjongBridge).GetMethod("Awake", BridgePrivate).Invoke(bridge, null);
                var emitted = new List<RendererAction>();
                bridge.EditorEventSink = json =>
                {
                    if (JObject.Parse(json)["type"].Value<string>() == "action")
                        emitted.Add(SourceFixtures.Schema.Read<RendererAction>(json, "RendererAction"));
                };
                string buildId = (string)typeof(MahjongBridge)
                    .GetMethod("EmbeddedBuildId", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                bridge.Configure(SourceFixtures.Schema.Write(new Configure
                {
                    Type = "configure", InstanceId = "fixture-instance", BuildId = buildId
                }, "Configure"));
                bridge.Clear(SourceFixtures.Schema.Write(new Clear
                {
                    Type = "clear", InstanceId = "fixture-instance", SessionId = "fixture-session", Reason = "join"
                }, "Clear"));
                RendererState fixture = SourceFixtures.State(true);
                RendererLayout layout = SourceFixtures.Copy(fixture.Layout, "RendererLayout");
                Slot discard = SourceFixtures.Copy(layout.Slots[0], "Slot");
                discard.SlotName = "discard.0@0";
                discard.Group = "discard";
                discard.CanFlipMultiple = false;
                layout.Slots = layout.Slots.Concat(new[] { discard }).ToArray();
                bridge.ApplyLayout(SourceFixtures.Schema.Write(layout, "RendererLayout"));
                bridge.ApplyFrame(SourceFixtures.Schema.Write(fixture.Frame, "RendererFrame"));
                Assert.That(bridge.State.Actionable, Is.True);
                var actions = (SemanticActions)typeof(MahjongBridge).GetField("actions", BridgePrivate).GetValue(bridge);
                test(bridge, actions, emitted);
            }
            finally { EngineObjects.Release(root); }
        }

        private static void Resolve(MahjongBridge bridge, RendererAction action, string status = "local", string reason = null) =>
            bridge.ReceiveActionResult(SourceFixtures.Schema.Write(new ActionResult
            {
                Type = "actionResult", InstanceId = action.InstanceId, SessionId = action.SessionId,
                ActionId = action.ActionId, Status = status, Reason = reason
            }, "ActionResult"));
    }
}
