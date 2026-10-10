using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Layout;

namespace Mahjong.Renderer.Contract
{
    public static class Coherence
    {
        public static Dictionary<string, Slot> Layout(RendererLayout layout)
        {
            int[] seats;
            switch (layout.GameType)
            {
                case "CHANGSHA":
                case "FOUR_PLAYER": seats = new[] { 0, 1, 2, 3 }; break;
                case "THREE_PLAYER": seats = new[] { 0, 1, 2 }; break;
                case "BAMBOO":
                case "MINEFIELD": seats = new[] { 0, 2 }; break;
                default: throw Invalid("Unsupported layout game type.");
            }
            if (!new HashSet<int>(layout.ActiveSeats).SetEquals(seats))
                throw Invalid("Active seats do not match the variant.");
            var result = new Dictionary<string, Slot>(StringComparer.Ordinal);
            foreach (Slot slot in layout.Slots)
            {
                if (ForbiddenSlot(slot.SlotName) || result.ContainsKey(slot.SlotName))
                    throw Invalid("Duplicate or forbidden layout slot.");
                if (slot.ShadowRotationIndex >= slot.Poses.Length)
                    throw Invalid("Shadow rotation is not in the source slot catalog.");
                string expectedKind = slot.Group == "marker" ? "MARKER" :
                    slot.Group == "tray" || slot.Group == "payment" || slot.Group == "riichi" ? "STICK" : "TILE";
                if (slot.Kind != expectedKind) throw Invalid("Slot group and source thing kind are inconsistent.");
                foreach (Pose pose in slot.Poses) SourceCoordinates.Convert(pose);
                result.Add(slot.SlotName, slot);
            }
            return result;
        }

        public static Dictionary<string, Entity> Frame(RendererFrame frame, RendererLayout layout,
            IReadOnlyDictionary<string, Slot> slots)
        {
            Session session = frame.Session;
            if (session.ActorSeat.HasValue && !layout.ActiveSeats.Contains(session.ActorSeat.Value))
                throw Invalid("Actor seat is not an active player seat.");
            if (session.Mode != "offline" &&
                ((session.Mode == "changsha") != (layout.GameType == "CHANGSHA")))
                throw Invalid("Session mode does not match the layout.");
            if (session.Mode != "changsha" && session.ViewerRevision.HasValue)
                throw Invalid("Viewer revision belongs only to the Changsha viewer projection.");
            if (session.Mode != "offline" && session.SnapshotReady && (!session.Connected || session.GameId == null))
                throw Invalid("An unbound network session cannot expose a ready snapshot.");
            if (frame.Match != null && (frame.Match.Conditions.GameType != layout.GameType ||
                                       !layout.ActiveSeats.Contains(frame.Match.Dealer)))
                throw Invalid("Match is inconsistent with the layout.");

            var entities = new Dictionary<string, Entity>(StringComparer.Ordinal);
            foreach (Entity entity in frame.Entities)
            {
                if (entities.ContainsKey(entity.EntityId)) throw Invalid("Duplicate renderer entity identifier.");
                if (ForbiddenSlot(entity.SlotName) || !slots.TryGetValue(entity.SlotName, out Slot slot))
                    throw Invalid("Entity references a missing or forbidden slot.");
                if (entity.Kind != slot.Kind || entity.Group != slot.Group || entity.Seat != slot.Seat ||
                    !entity.Indexes.SequenceEqual(slot.Indexes) || entity.RotationIndex >= slot.Poses.Length)
                    throw Invalid("Entity metadata is inconsistent with its source slot.");
                SourceCoordinates.Convert(entity.Pose, entity.Scale);
                if (!entity.Appearance.ShowFace && entity.Appearance.KnownFace &&
                    !(entity.Seat.HasValue && entity.Seat == session.ActorSeat &&
                      (entity.Group == "hand" || entity.Group == "meld")))
                    throw Invalid("Concealed foreign or wall identity is not entitled to this viewer.");
                if (entity.Kind == "MARKER" && entity.Appearance.ValueIndex != 0)
                    throw Invalid("Marker has only the source value index zero.");
                if (entity.Interaction.Any && !CanInteract(frame))
                    throw Invalid("Unbound or unowned session cannot authorize interaction.");
                if (session.Mode == "changsha")
                {
                    if (entity.Appearance.BackIndex != 0 || entity.Kind != "TILE")
                        throw Invalid("Changsha presentation cannot contain relay sticks or alternate backs.");
                    if (entity.Interaction.CanDrag || entity.Interaction.CanFlip)
                        throw Invalid("Changsha never authorizes renderer relocation or flipping.");
                    if (entity.Interaction.CanSelect &&
                        !(entity.Group == "hand" && entity.Seat == session.ActorSeat))
                        throw Invalid("Changsha selection must be in the owned hand.");
                    if (entity.Interaction.CanDiscard &&
                        !(entity.Group == "hand" && entity.Seat == session.ActorSeat &&
                          entity.Appearance.KnownFace && frame.Turn?.AwaitingDiscard == true &&
                          frame.Turn.ActiveSeat == session.ActorSeat))
                        throw Invalid("Discard eligibility is inconsistent with the current hand/turn.");
                    if (entity.Interaction.CanPickup &&
                        !(frame.Pickup?.CanTake == true && frame.Pickup.DealMode == "manual" &&
                          frame.Pickup.SeatIndex == session.ActorSeat &&
                          frame.Pickup.TriggerSlotName == entity.SlotName))
                        throw Invalid("Pickup eligibility is inconsistent with the current source trigger.");
                }
                entities.Add(entity.EntityId, entity);
            }

            var displaySeats = new HashSet<int>();
            foreach (SeatDisplay seat in frame.Seats)
                if (!layout.ActiveSeats.Contains(seat.Seat) || !displaySeats.Add(seat.Seat))
                    throw Invalid("Duplicate or inactive display seat.");
            if (frame.Turn?.ActiveSeat != null && !layout.ActiveSeats.Contains(frame.Turn.ActiveSeat.Value))
                throw Invalid("Turn seat is not active.");
            if (frame.LocalHand.Seat == null &&
                (frame.LocalHand.EntityIds.Length != 0 || frame.LocalHand.DrawnEntityId != null))
                throw Invalid("An unbound hand cannot contain entities.");
            if (frame.LocalHand.Seat.HasValue &&
                frame.LocalHand.Seat != session.ActorSeat &&
                frame.LocalHand.Seat != (session.ViewSeat ?? 0))
                throw Invalid("Hand band must belong to the actor or the current camera seat.");
            foreach (string id in frame.LocalHand.EntityIds)
            {
                if (!entities.TryGetValue(id, out Entity entity) || entity.Kind != "TILE" ||
                    entity.Group != "hand" || entity.Seat != frame.LocalHand.Seat)
                    throw Invalid("Local hand contains an absent or foreign/non-hand entity.");
            }
            if (frame.LocalHand.DrawnEntityId != null &&
                !frame.LocalHand.EntityIds.Contains(frame.LocalHand.DrawnEntityId))
                throw Invalid("Drawn entity must be a member of the current local hand.");
            if (session.Mode == "changsha" && frame.LocalHand.EntityIds.Length != 0 &&
                (!session.ActorSeat.HasValue || frame.LocalHand.Seat != session.ActorSeat))
                throw Invalid("Changsha camera POV cannot establish a private hand entitlement.");
            if (frame.Pickup != null)
            {
                if (!layout.ActiveSeats.Contains(frame.Pickup.SeatIndex) ||
                    frame.Pickup.TriggerSlotName != null && !slots.ContainsKey(frame.Pickup.TriggerSlotName) ||
                    frame.Pickup.BatchPreviewSlotNames.Any(name => !slots.ContainsKey(name)))
                    throw Invalid("Pickup preview references an invalid seat or slot.");
                if (frame.Pickup.CanTake && (!CanInteract(frame) || frame.Pickup.DealMode != "manual" ||
                                            frame.Pickup.SeatIndex != session.ActorSeat ||
                                            frame.Pickup.TriggerSlotName == null))
                    throw Invalid("Manual pickup is not authorized for this actor.");
            }
            if (frame.Dice?.CanRoll == true &&
                (!CanInteract(frame) || frame.Match == null || frame.Match.Dealer != session.ActorSeat ||
                 frame.Turn?.Phase != "RollingDice" || frame.Match.Conditions.DealMode != "manual"))
                throw Invalid("Dice action is not authorized by the current source frame.");
            return entities;
        }

        public static void Viewport(Viewport value)
        {
            if (value.WidthPx > int.MaxValue || value.HeightPx > int.MaxValue ||
                Math.Abs(value.RenderPixelRatio - Math.Min(value.DevicePixelRatio, 2)) > 0.00001 ||
                Math.Floor(value.WidthCssPx * value.RenderPixelRatio + 0.5) != value.WidthPx ||
                Math.Floor(value.HeightCssPx * value.RenderPixelRatio + 0.5) != value.HeightPx)
                throw Invalid("Viewport buffer dimensions or DPR do not match the measured CSS rect.");
            Insets inset = value.InsetsCssPx;
            if (inset.Left + inset.Right >= value.WidthCssPx ||
                inset.Top + inset.Bottom + value.HandBandCssPx >= value.HeightCssPx)
                throw Invalid("Safe areas and hand band leave no board viewport.");
            SourceCoordinates.Float(value.WidthCssPx);
            SourceCoordinates.Float(value.HeightCssPx);
            SourceCoordinates.Float(value.HandBandCssPx);
        }

        public static bool CanInteract(RendererFrame frame) =>
            frame.Session.SnapshotReady && frame.Session.ActorSeat.HasValue &&
            (frame.Session.Mode == "offline" || frame.Session.Connected);

        public static bool ForbiddenSlot(string name) =>
            name.StartsWith("hiddenpool", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("offTable", StringComparison.OrdinalIgnoreCase);

        private static ContractException Invalid(string message) => new ContractException("invalid-message", message);
    }
}
