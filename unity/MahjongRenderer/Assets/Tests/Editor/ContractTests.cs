using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Mahjong.Renderer.Tests
{
    public sealed class ContractTests
    {
        [TestCase("{\"x\":1,}")]
        [TestCase("{x:1}")]
        [TestCase("{'x':1}")]
        [TestCase("[1,]")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":Infinity}")]
        [TestCase("{\"x\":1e999}")]
        [TestCase("{\"x\":01}")]
        [TestCase("{\"x\":.5}")]
        [TestCase("{\"x\":1}/*comment*/")]
        [TestCase("{\"x\":1,\"\\u0078\":2}")]
        [TestCase("{} {}")]
        public void StrictParserRejectsJavaScriptExtensionsAndDuplicateProperties(string text)
        {
            Assert.That(() => StrictJson.Parse(text), Throws.TypeOf<ContractException>());
        }

        [Test]
        public void FrozenFixturesRoundTripThroughActualSchemaAndDtoParser()
        {
            foreach (string definition in new[] { "RendererLayout", "RendererFrame", "RendererAction", "Viewport" })
                Assert.DoesNotThrow(() => SourceFixtures.Schema.Validate(SourceFixtures.Json(definition), definition), definition);
            RendererFrame frame = SourceFixtures.Read<RendererFrame>("RendererFrame");
            JObject json = JObject.Parse(SourceFixtures.Schema.Write(frame, "RendererFrame"));
            Assert.That(json["entities"][1]["appearance"]["faceIndex"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["localHand"]["drawnEntityId"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["pickup"].Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void OptionalCameraOverrideRoundTripsWithBoundedAxesAndRejectsMalformedOverrides()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            Assert.That(SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame").Presentation.Camera, Is.Null);
            frame["presentation"]["camera"] = new JObject { ["scale"] = 1.5, ["yaw"] = 90, ["pitch"] = 45, ["roll"] = -30 };
            RendererFrame parsed = SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame");
            Assert.That(parsed.Presentation.Camera.Scale, Is.EqualTo(1.5));
            Assert.That(parsed.Presentation.Camera.Yaw, Is.EqualTo(90));
            Assert.That(parsed.Presentation.Camera.Pitch, Is.EqualTo(45));
            Assert.That(parsed.Presentation.Camera.Roll, Is.EqualTo(-30));
            Assert.DoesNotThrow(() => SourceFixtures.Schema.Write(parsed, "RendererFrame"));
            foreach (var invalid in new[]
            {
                new KeyValuePair<string, double>("scale", 0), new KeyValuePair<string, double>("scale", 2.51),
                new KeyValuePair<string, double>("yaw", 181), new KeyValuePair<string, double>("pitch", 19),
                new KeyValuePair<string, double>("pitch", 91), new KeyValuePair<string, double>("roll", -181),
                new KeyValuePair<string, double>("scale", double.NaN),
            })
            {
                JObject copy = (JObject)frame.DeepClone();
                copy["presentation"]["camera"][invalid.Key] = invalid.Value;
                Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(copy.ToString(), "RendererFrame"),
                    Throws.TypeOf<ContractException>(), invalid.Key);
            }
            ((JObject)frame["presentation"]["camera"]).Remove("scale");
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
            Assert.That(CameraView.Same(null, new CameraView()), Is.True);
            Assert.That(CameraView.Same(null, parsed.Presentation.Camera), Is.False);
        }

        [Test]
        public void CameraCancellationIsOnlyAnInstanceBoundLocalEnvelope()
        {
            var json = new JObject { ["contractVersion"] = ContractVersion.Current, ["instanceId"] = "camera", ["type"] = "cancelInput" };
            CancelInput message = SourceFixtures.Schema.Read<CancelInput>(json.ToString(), "CancelInput");
            Assert.That(message.InstanceId, Is.EqualTo("camera"));
            json["actorSeat"] = 0;
            Assert.That(() => SourceFixtures.Schema.Read<CancelInput>(json.ToString(), "CancelInput"),
                Throws.TypeOf<ContractException>());
        }

        [TestCase("tileId")]
        [TestCase("auth")]
        [TestCase("stateVersion")]
        [TestCase("$type")]
        public void UnknownWireAndAuthPropertiesAreRejected(string property)
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0][property] = 108;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void UnsupportedVersionAndUnknownEnumsDoNotDefault()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["contractVersion"] = "2.0.0";
            ContractException version = Assert.Throws<ContractException>(() =>
                SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"));
            Assert.That(version.Code, Is.EqualTo("unsupported-contract"));
            frame["contractVersion"] = "1.0.0";
            frame["turn"]["phase"] = "EndGame";
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void NullHiddenFaceNeverBecomesFaceZeroAndPrivacyConditionsAreEnforced()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][1]["appearance"]["faceIndex"] = 0;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0]["appearance"]["faceIndex"] = JValue.CreateNull();
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void UnreadySnapshotCannotCarryEntitiesOrHand()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            frame["session"]["snapshotReady"] = false;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void MissingRequiredNullsAndNonfiniteNumbersAreNotCoerced()
        {
            JObject frame = SourceFixtures.Json("RendererFrame");
            ((JObject)frame["session"]).Remove("actorSeat");
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["presentation"]["zoom"] = double.NaN;
            Assert.That(() => SourceFixtures.Schema.Validate(frame, "RendererFrame"),
                Throws.TypeOf<ContractException>());
            frame = SourceFixtures.Json("RendererFrame");
            frame["entities"][0]["pose"]["size"]["x"] = 0;
            Assert.That(() => SourceFixtures.Schema.Read<RendererFrame>(frame.ToString(), "RendererFrame"),
                Throws.TypeOf<ContractException>());
        }

        [Test]
        public void InvalidActionsCannotInjectActorOrTileWireFields()
        {
            JObject action = SourceFixtures.Json("RendererAction");
            action["intent"]["tileId"] = 1;
            Assert.That(() => SourceFixtures.Schema.Validate(action, "RendererAction"), Throws.TypeOf<ContractException>());
            action = SourceFixtures.Json("RendererAction");
            action["actorSeat"] = 0;
            Assert.That(() => SourceFixtures.Schema.Validate(action, "RendererAction"), Throws.TypeOf<ContractException>());
        }

        [TestCase("BreakPointMarked", 4)]
        [TestCase("PickupRound1", 4)]
        [TestCase("PickupRound2", 4)]
        [TestCase("PickupRound3", 4)]
        [TestCase("SingleTilePickup", 1)]
        [TestCase("DealerExtra", 1)]
        public void OwnedManualPickupTriggerCanHoverSelectAndTakeWithoutRevealingWall(string phase, int count)
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = ManualPickupFrame(state, phase);
            frame.Pickup.Count = count;
            Assert.DoesNotThrow(() => SourceFixtures.Schema.Write(frame, "RendererFrame"));
            Assert.DoesNotThrow(() => state.ApplyFrame(frame),
                phase + " must accept the server-designated wall.0.1@1 trigger for actor seat 0.");
            Entity trigger = state.Entities["fixture-concealed-wall"];
            Assert.That(trigger.Seat, Is.EqualTo(1), "Wall ownership is not the pickup actor entitlement.");
            Assert.That(trigger.Appearance.KnownFace, Is.False);
            Assert.That(trigger.Appearance.FaceIndex, Is.Null);
            Assert.That(trigger.Appearance.ShowFace, Is.False);

            var emitted = new List<RendererAction>();
            var actions = new SemanticActions(state, emitted.Add);
            Assert.That(actions.Hover(trigger.EntityId), Is.True);
            Assert.That(actions.Select(trigger.EntityId, false), Is.True);
            Assert.That(actions.TakePickup(trigger.EntityId), Is.True);
            Assert.That(actions.BeginDrag(trigger.EntityId), Is.False);
            Assert.That(actions.Flip(trigger.EntityId, 1), Is.False);
            Assert.That(actions.Discard(trigger.EntityId), Is.False);
            Assert.That(emitted.Select(action => action.Intent["kind"].Value<string>()),
                Is.EqualTo(new[] { "hover", "select", "takePickup" }));
            Assert.That(emitted.Last().Intent.Properties().Select(property => property.Name),
                Is.EquivalentTo(new[] { "kind", "triggerEntityId" }));
            Assert.That(state.Entities[trigger.EntityId].SlotName, Is.EqualTo("wall.0.1@1"));
        }

        [TestCase("missing-pickup")]
        [TestCase("automatic")]
        [TestCase("wrong-actor")]
        [TestCase("empty-count")]
        [TestCase("not-takeable")]
        [TestCase("missing-trigger")]
        [TestCase("other-trigger")]
        [TestCase("preview-only")]
        [TestCase("selection-only")]
        [TestCase("pickup-only")]
        [TestCase("post-pickup-phase")]
        [TestCase("missing-turn")]
        [TestCase("foreign-hand")]
        [TestCase("drag")]
        [TestCase("flip")]
        [TestCase("discard-trigger")]
        [TestCase("concealed-face")]
        public void ManualPickupCannotAuthorizeUnsafeWallOrForeignHand(string mutation)
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = ManualPickupFrame(state, "PickupRound1");
            Entity trigger = frame.Entities[1];
            switch (mutation)
            {
                case "missing-pickup": frame.Pickup = null; break;
                case "automatic": frame.Pickup.DealMode = "auto"; break;
                case "wrong-actor": frame.Pickup.SeatIndex = 1; break;
                case "empty-count": frame.Pickup.Count = 0; break;
                case "not-takeable": frame.Pickup.CanTake = false; break;
                case "missing-trigger": frame.Pickup.TriggerSlotName = null; break;
                case "other-trigger": frame.Pickup.TriggerSlotName = "hand.0@0"; break;
                case "preview-only":
                    frame.Pickup.TriggerSlotName = "hand.0@0";
                    frame.Pickup.BatchPreviewSlotNames = new[] { trigger.SlotName };
                    break;
                case "selection-only": trigger.Interaction.CanPickup = false; break;
                case "pickup-only": trigger.Interaction.CanSelect = false; break;
                case "post-pickup-phase": frame.Turn.Phase = "AwaitingDiscard"; break;
                case "missing-turn": frame.Turn = null; break;
                case "foreign-hand":
                    RendererLayout layout = SourceFixtures.Copy(state.Layout, "RendererLayout");
                    layout.LayoutRevision++;
                    Slot slot = layout.Slots[1];
                    slot.Group = "hand";
                    slot.SlotName = "hand.0@1";
                    slot.Indexes = new long[] { 0 };
                    state.ApplyLayout(layout);
                    frame.LayoutRevision = layout.LayoutRevision;
                    trigger.Group = slot.Group;
                    trigger.SlotName = slot.SlotName;
                    trigger.Indexes = slot.Indexes;
                    frame.Pickup.TriggerSlotName = slot.SlotName;
                    break;
                case "drag": trigger.Interaction.CanDrag = true; break;
                case "flip": trigger.Interaction.CanFlip = true; break;
                case "discard-trigger": trigger.Interaction.CanDiscard = true; break;
                case "concealed-face":
                    trigger.Appearance.KnownFace = true;
                    trigger.Appearance.FaceIndex = 3;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            RendererFrame before = state.Frame;
            Assert.That(() => state.ApplyFrame(frame), Throws.TypeOf<ContractException>(), mutation);
            Assert.That(state.Frame, Is.SameAs(before), mutation + " must not replace the accepted frame.");
        }

        [Test]
        public void UnrelatedWallInBatchPreviewIsNotTheManualPickupTrigger()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = ManualPickupFrame(state, "PickupRound1");
            RendererLayout layout = SourceFixtures.Copy(state.Layout, "RendererLayout");
            layout.LayoutRevision++;
            Slot unrelated = SourceFixtures.Copy(layout.Slots[1], "Slot");
            unrelated.SlotName = "wall.1.1@1";
            unrelated.Indexes = new long[] { 1, 1 };
            layout.Slots = layout.Slots.Concat(new[] { unrelated }).ToArray();
            state.ApplyLayout(layout);
            frame.LayoutRevision = layout.LayoutRevision;
            frame.Entities[1].SlotName = unrelated.SlotName;
            frame.Entities[1].Indexes = unrelated.Indexes;
            frame.Pickup.BatchPreviewSlotNames = new[] { frame.Pickup.TriggerSlotName, unrelated.SlotName };
            Assert.That(() => state.ApplyFrame(frame), Throws.TypeOf<ContractException>(),
                "The display-only batch preview must not authorize wall.1.1@1.");
        }

        private static RendererFrame ManualPickupFrame(RendererState state, string phase)
        {
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.FrameId++;
            frame.Match.Conditions.DealMode = "manual";
            frame.Turn.Phase = phase;
            frame.Turn.AwaitingDiscard = false;
            frame.Entities[0].Interaction.CanDiscard = false;
            frame.Entities[1].Interaction = new Interaction { CanSelect = true, CanPickup = true };
            frame.Pickup = new PickupDisplay
            {
                Phase = phase, SeatIndex = 0, Count = 4, DealMode = "manual",
                TriggerSlotName = "wall.0.1@1", BatchPreviewSlotNames = Array.Empty<string>(), CanTake = true
            };
            return frame;
        }
    }
}
