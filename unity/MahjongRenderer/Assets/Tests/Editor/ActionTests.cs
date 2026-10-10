using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Mahjong.Renderer.Tests
{
    public sealed class ActionTests
    {
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
    }
}
