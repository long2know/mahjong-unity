using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Contract;
using Newtonsoft.Json.Linq;

namespace Mahjong.Renderer.Input
{
    public sealed class SemanticActions
    {
        private readonly RendererState state;
        private readonly Action<RendererAction> emit;
        private readonly HashSet<string> selection = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, RendererAction> pendingDragTargets =
            new Dictionary<string, RendererAction>(StringComparer.Ordinal);
        private string gestureId;
        private string[] dragged;
        private string dragTarget;
        private string dragTargetActionId;
        private long gestureCounter;
        public IReadOnlyCollection<string> Selection => selection;
        public string Hovered { get; private set; }
        public string DragTarget => dragTarget;
        public bool Dragging => gestureId != null;
        public event Action CuesChanged;

        public SemanticActions(RendererState state, Action<RendererAction> emit)
        {
            this.state = state;
            this.emit = emit;
            state.Invalidated += Cancel;
            state.ActionResolved += Result;
        }

        public void Reconcile()
        {
            selection.RemoveWhere(id => !Eligible(id, entity => entity.Interaction.CanSelect));
            if (Hovered != null && !Eligible(Hovered, entity => entity.Interaction.CanSelect)) Hovered = null;
            if (Dragging && dragged.Any(id => !Eligible(id, entity => entity.Interaction.CanDrag))) Cancel();
            CuesChanged?.Invoke();
        }

        public bool Hover(string id)
        {
            if (id != null && !Eligible(id, entity => entity.Interaction.CanSelect)) id = null;
            if (id == Hovered || !state.Actionable) return false;
            Hovered = id;
            Send(new JObject { ["kind"] = "hover", ["entityId"] = id == null ? JValue.CreateNull() : new JValue(id) });
            CuesChanged?.Invoke();
            return true;
        }

        public bool Select(string id, bool additive)
        {
            if (!state.Actionable || id != null && !Eligible(id, entity => entity.Interaction.CanSelect)) return false;
            if (!additive) selection.Clear();
            if (id != null && (!additive || !selection.Remove(id))) selection.Add(id);
            Send(new JObject { ["kind"] = "select", ["entityIds"] = new JArray(selection.OrderBy(value => value)) });
            CuesChanged?.Invoke();
            return true;
        }

        public bool Discard(string id)
        {
            if (!Eligible(id, entity => entity.Interaction.CanDiscard)) return false;
            Send(new JObject { ["kind"] = "discard", ["entityId"] = id });
            return true;
        }

        public bool TakePickup(string id)
        {
            if (!Eligible(id, entity => entity.Interaction.CanPickup) ||
                state.Frame.Pickup?.CanTake != true ||
                state.Frame.Pickup.TriggerSlotName != state.Entities[id].SlotName) return false;
            Send(new JObject { ["kind"] = "takePickup", ["triggerEntityId"] = id });
            return true;
        }

        public bool RollDice()
        {
            if (!state.Actionable || state.Frame.Dice?.CanRoll != true) return false;
            Send(new JObject { ["kind"] = "rollDice" });
            return true;
        }

        public bool Flip(string id, int direction)
        {
            if (!state.Actionable || state.Frame.Session.Mode == "changsha" || (direction != -1 && direction != 1))
                return false;
            string[] ids = selection.Contains(id) ? selection.ToArray() : new[] { id };
            if (ids.Length == 0 || ids.Any(value => !Eligible(value, entity => entity.Interaction.CanFlip)) ||
                ids.Length > 1 && ids.Any(value => !state.Slots[state.Entities[value].SlotName].CanFlipMultiple))
                return false;
            Send(new JObject { ["kind"] = "flip", ["entityIds"] = new JArray(ids), ["direction"] = direction,
                ["animated"] = true });
            return true;
        }

        public bool BeginDrag(string anchor)
        {
            if (Dragging || !state.Actionable || state.Frame.Session.Mode == "changsha" ||
                !Eligible(anchor, entity => entity.Interaction.CanDrag)) return false;
            string[] ids = selection.Contains(anchor) ? selection.ToArray() : new[] { anchor };
            if (ids.Any(id => !Eligible(id, entity => entity.Interaction.CanDrag))) return false;
            gestureId = "gesture-" + (++gestureCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            dragged = ids;
            Send(new JObject { ["kind"] = "beginDrag", ["gestureId"] = gestureId,
                ["anchorEntityId"] = anchor, ["entityIds"] = new JArray(ids) });
            CuesChanged?.Invoke();
            return true;
        }

        public bool DragToSlot(string slotName)
        {
            if (!Dragging || !state.Actionable || slotName == null || !state.Slots.TryGetValue(slotName, out Slot target) ||
                Coherence.ForbiddenSlot(slotName) || target.Kind != state.Entities[dragged[0]].Kind ||
                dragged.Any(id => !Eligible(id, entity => entity.Interaction.CanDrag))) return false;
            if (dragTarget == slotName) return true;
            dragTarget = slotName;
            Send(new JObject { ["kind"] = "dragToSlot", ["gestureId"] = gestureId, ["slotName"] = slotName });
            CuesChanged?.Invoke();
            return true;
        }

        public bool EndDrag(bool commit)
        {
            if (!Dragging) return false;
            string gesture = gestureId;
            bool canCommit = commit && dragTarget != null && dragTargetActionId != null &&
                             !pendingDragTargets.ContainsKey(dragTargetActionId) && state.Actionable &&
                             dragged.All(id => Eligible(id, entity => entity.Interaction.CanDrag));
            gestureId = null;
            dragged = null;
            dragTarget = null;
            dragTargetActionId = null;
            if (state.Actionable)
                Send(new JObject { ["kind"] = "endDrag", ["gestureId"] = gesture, ["commit"] = canCommit });
            CuesChanged?.Invoke();
            return true;
        }

        public void ClearDragTargetCue()
        {
            if (dragTarget == null) return;
            dragTarget = null;
            dragTargetActionId = null;
            CuesChanged?.Invoke();
        }

        public void Cancel()
        {
            EndDrag(false);
            pendingDragTargets.Clear();
            selection.Clear();
            Hovered = null;
            CuesChanged?.Invoke();
        }

        private bool Eligible(string id, Func<Entity, bool> permission) =>
            state.Actionable && id != null && state.Entities.TryGetValue(id, out Entity entity) && permission(entity);

        private void Result(ActionResult result)
        {
            pendingDragTargets.TryGetValue(result.ActionId, out RendererAction candidate);
            pendingDragTargets.Remove(result.ActionId);
            if (result.Status != "ignored" && result.Status != "rejected") return;
            if (result.Status == "rejected" && result.Reason == "invalid-target" && candidate != null &&
                candidate.InstanceId == result.InstanceId && candidate.SessionId == result.SessionId)
            {
                if (candidate.Intent["gestureId"].Value<string>() != gestureId) return;
                if (state.Actionable && dragged != null &&
                    dragged.All(id => Eligible(id, entity => entity.Interaction.CanDrag)))
                {
                    if (candidate.ActionId == dragTargetActionId) ClearDragTargetCue();
                    return;
                }
            }
            Cancel();
        }

        private void Send(JObject intent)
        {
            RendererAction action = state.MakeAction(intent);
            if (intent["kind"].Value<string>() == "dragToSlot")
            {
                pendingDragTargets.Add(action.ActionId, action);
                dragTargetActionId = action.ActionId;
            }
            emit(action);
        }
    }
}
