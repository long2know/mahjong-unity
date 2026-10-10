using System;
using System.Linq;
using Mahjong.Renderer.Contract;
using NUnit.Framework;

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
    }
}
