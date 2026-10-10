using System;
using System.Linq;
using Mahjong.Renderer.Contract;
using Mahjong.Renderer.Layout;
using NUnit.Framework;
using UnityEngine;

namespace Mahjong.Renderer.Tests
{
    public sealed class LayoutTests
    {
        [Test]
        public void SourceBasisAndQuaternionConvertExactlyOnce()
        {
            var point = new Vector3Dto { X = 187, Y = 287, Z = 30 };
            Assert.That(Vector3.Distance(SourceCoordinates.Position(point), new Vector3(1, 0.3f, 2)), Is.LessThan(0.00001));
            var source = new QuaternionDto { Z = Math.Sqrt(0.5), W = Math.Sqrt(0.5) };
            Quaternion result = SourceCoordinates.Rotation(source);
            Assert.That(Vector3.Distance(result * Vector3.forward, Vector3.left), Is.LessThan(0.00001));
            Assert.That(Quaternion.Angle(SourceCoordinates.ViewRotation(1) * result, Quaternion.identity), Is.LessThan(0.001));
            Assert.That(SourceCoordinates.RelativeSeat(3, 1), Is.EqualTo(2));
        }

        [TestCase("CHANGSHA")]
        [TestCase("FOUR_PLAYER")]
        [TestCase("THREE_PLAYER")]
        [TestCase("BAMBOO")]
        [TestCase("MINEFIELD")]
        public void RectangularLanesPreserveEveryGroupAndNeverOverlapGroups(string gameType)
        {
            RendererLayout layout = SourceFixtures.Lanes(gameType);
            RendererFrame frame = SourceFixtures.Empty(layout);
            frame.Presentation.HandLayout = "in-table";
            frame.Presentation.BoardLayout = "rectangular";
            Coherence.Layout(layout);
            Coherence.Frame(frame, layout, Coherence.Layout(layout));
            LayoutPlan plan = BoardLayout.Build(layout, frame, SourceFixtures.Read<Viewport>("Viewport"));
            Bounds[] groups = plan.GroupBounds.Values.ToArray();
            for (int a = 0; a < groups.Length; a++)
                for (int b = 0; b < a; b++)
                {
                    bool overlap = groups[a].min.x < groups[b].max.x - 0.0001f &&
                                   groups[a].max.x > groups[b].min.x + 0.0001f &&
                                   groups[a].min.z < groups[b].max.z - 0.0001f &&
                                   groups[a].max.z > groups[b].min.z + 0.0001f;
                    Assert.That(overlap, Is.False, "Overlapping independent slot groups");
                }
            Assert.That(plan.Slots.Count, Is.EqualTo(layout.Slots.Length));
            foreach (Slot slot in layout.Slots)
            {
                RenderPose mapped = plan.Slots[slot.SlotName].Poses[0];
                Assert.That(mapped.Scale, Is.EqualTo(1));
                Assert.That(Quaternion.Angle(mapped.Rotation, SourceCoordinates.Rotation(slot.Poses[0].Rotation)), Is.LessThan(0.001));
            }
            Slot[] wall = layout.Slots.Where(slot => slot.Group == "wall" && slot.Seat == 0 && slot.Indexes[0] == 0).ToArray();
            Assert.That(plan.Slots[wall[1].SlotName].Poses[0].Position.y -
                        plan.Slots[wall[0].SlotName].Poses[0].Position.y, Is.EqualTo(0.04f).Within(0.00001));
        }

        [Test]
        public void ClassicPreservesSuppliedPoseAndSeparateHandIsExclusive()
        {
            RendererState state = SourceFixtures.State();
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            frame.Presentation.BoardLayout = "classic";
            frame.Presentation.HandLayout = "in-table";
            LayoutPlan classic = BoardLayout.Build(state.Layout, frame, state.Viewport);
            Assert.That(Vector3.Distance(classic.Entities[frame.Entities[0].EntityId].Pose.Position,
                SourceCoordinates.Position(frame.Entities[0].Pose.Position)), Is.LessThan(0.00001));
            frame.Presentation.HandLayout = "separate";
            LayoutPlan separate = BoardLayout.Build(state.Layout, frame, state.Viewport);
            Assert.That(separate.Entities[frame.Entities[0].EntityId].Separate, Is.True);
            Assert.That(separate.Entities[frame.Entities[1].EntityId].Separate, Is.False);
            Assert.That(separate.Entities.Count, Is.EqualTo(frame.Entities.Length));
            Assert.That(separate.Slots[frame.Entities[0].SlotName].Separate, Is.True);
        }

        [Test]
        public void SafeAreaAndDprUseCssGeometryAndDisjointCameraViewports()
        {
            Viewport value = SourceFixtures.Read<Viewport>("Viewport");
            value.InsetsCssPx = new Insets { Top = 24, Right = 8, Bottom = 16, Left = 12 };
            var geometry = new ViewportGeometry(value, true);
            Assert.That(geometry.SafeCss, Is.EqualTo(new Rect(12, 16, 370, 804)));
            Assert.That(geometry.HandCss.yMax, Is.EqualTo(geometry.BoardCss.yMin));
            Assert.That(geometry.BoardNormalized.yMax, Is.LessThanOrEqualTo(1));
            Vector2 css = ViewportGeometry.ScreenToCss(new Vector2(200, 400),
                new Vector2(780, 1688), value);
            Assert.That(css.x, Is.EqualTo(100).Within(0.001));
            Assert.That(css.y, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void FullTouchHandFitsSafeBandWithNoMeshOverlapAndDrawnGap()
        {
            RendererState state = SourceFixtures.State();
            RendererLayout layout = SourceFixtures.Copy(state.Layout, "RendererLayout");
            RendererFrame frame = SourceFixtures.Copy(state.Frame, "RendererFrame");
            Slot prototype = layout.Slots[0];
            Entity entityPrototype = frame.Entities[0];
            var slots = new System.Collections.Generic.List<Slot>();
            var entities = new System.Collections.Generic.List<Entity>();
            for (int i = 0; i < 15; i++)
            {
                Slot slot = SourceFixtures.Copy(prototype, "Slot");
                slot.SlotName = "hand." + i + "@0";
                slot.Indexes = new long[] { i };
                foreach (Contract.Pose pose in slot.Poses) pose.Position.X = 49 + i * 6;
                slots.Add(slot);
                if (i == 14) continue;
                Entity entity = SourceFixtures.Copy(entityPrototype, "Entity");
                entity.EntityId = "touch-hand-" + i;
                entity.SlotName = slot.SlotName;
                entity.Indexes = slot.Indexes;
                entity.Pose = slot.Poses[1];
                entities.Add(entity);
            }
            layout.Slots = slots.ToArray();
            frame.Entities = entities.ToArray();
            frame.LocalHand.EntityIds = entities.Select(entity => entity.EntityId).ToArray();
            frame.LocalHand.DrawnEntityId = entities.Last().EntityId;
            LayoutPlan plan = BoardLayout.Build(layout, frame, state.Viewport);
            Bounds[] boxes = entities.Select(entity => SourceCoordinates.OrientedBounds(
                plan.Entities[entity.EntityId].Pose, "TILE")).ToArray();
            for (int a = 0; a < boxes.Length; a++)
            {
                Assert.That(boxes[a].min.x, Is.GreaterThanOrEqualTo(-plan.Viewport.HandCss.width * 0.005f));
                Assert.That(boxes[a].max.x, Is.LessThanOrEqualTo(plan.Viewport.HandCss.width * 0.005f));
                Assert.That(boxes[a].min.z, Is.GreaterThanOrEqualTo(-plan.Viewport.HandCss.height * 0.005f));
                Assert.That(boxes[a].max.z, Is.LessThanOrEqualTo(plan.Viewport.HandCss.height * 0.005f));
                for (int b = 0; b < a; b++)
                    Assert.That(boxes[a].min.x < boxes[b].max.x && boxes[a].max.x > boxes[b].min.x &&
                                boxes[a].min.z < boxes[b].max.z && boxes[a].max.z > boxes[b].min.z, Is.False);
            }
            Bounds drawn = boxes.Last();
            Assert.That(drawn.min.x - boxes[boxes.Length - 2].max.x, Is.GreaterThan(0.04f));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CameraFitContainsAllBoundsAtEveryZoomAndLookDown(bool flat)
        {
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(2, 0.15f, 4));
            foreach (double zoom in new[] { -1.0, 0.0, 1.0 })
                foreach (double down in new[] { 0.0, 1.0 })
                {
                    CameraPose pose = CameraFit.Fit(bounds, 0.5f, flat, down, zoom);
                    Quaternion inverse = Quaternion.Inverse(pose.Rotation);
                    for (int x = -1; x <= 1; x += 2)
                        for (int y = -1; y <= 1; y += 2)
                            for (int z = -1; z <= 1; z += 2)
                            {
                                Vector3 world = Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                                Vector3 local = inverse * (world - pose.Position);
                                float halfHeight = flat ? pose.OrthographicSize :
                                    local.z * Mathf.Tan(CameraFit.FieldOfView * Mathf.Deg2Rad / 2);
                                Assert.That(Mathf.Abs(local.y), Is.LessThanOrEqualTo(halfHeight + 0.0001f));
                                Assert.That(Mathf.Abs(local.x), Is.LessThanOrEqualTo(halfHeight * 0.5f + 0.0001f));
                            }
                }
        }
    }
}
