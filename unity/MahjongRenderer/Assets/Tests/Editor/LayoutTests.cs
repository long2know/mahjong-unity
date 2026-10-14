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

        [TestCase(360, 800)]
        [TestCase(390, 844)]
        [TestCase(500, 900)]
        [TestCase(1280, 800)]
        public void CompactChangshaFitsReadablePublicTilesWithoutEmptyReserves(int width, int height)
        {
            RendererLayout layout = SourceFixtures.Lanes("CHANGSHA");
            RendererFrame frame = SourceFixtures.Empty(layout);
            Entity prototype = SourceFixtures.Read<RendererFrame>("RendererFrame").Entities[0];
            frame.Entities = layout.Slots.Where(slot => slot.Kind == "TILE" &&
                new[] { "discard", "meld", "hand", "wall" }.Contains(slot.Group)).Select(slot =>
            {
                Entity entity = SourceFixtures.Copy(prototype, "Entity");
                entity.EntityId = slot.SlotName;
                entity.SlotName = slot.SlotName;
                entity.Group = slot.Group;
                entity.Seat = slot.Seat;
                entity.Indexes = slot.Indexes;
                entity.Pose = slot.Poses[0];
                entity.RotationIndex = 0;
                entity.Interaction = new Interaction();
                return entity;
            }).ToArray();
            frame.LocalHand.Seat = 0;
            frame.LocalHand.EntityIds = frame.Entities.Where(entity => entity.Group == "hand" && entity.Seat == 0)
                .Select(entity => entity.EntityId).ToArray();
            bool portrait = width < height;
            frame.Presentation.HandLayout = portrait ? "separate" : "in-table";
            frame.Presentation.BoardLayout = "rectangular";
            frame.Presentation.ViewMode = portrait ? "flat" : "perspective";
            Viewport viewport = SourceFixtures.Read<Viewport>("Viewport");
            viewport.WidthCssPx = width;
            viewport.HeightCssPx = height;
            viewport.WidthPx = Mathf.RoundToInt(width * (float)viewport.RenderPixelRatio);
            viewport.HeightPx = Mathf.RoundToInt(height * (float)viewport.RenderPixelRatio);
            viewport.HandBandCssPx = portrait ? 148 : 0;
            viewport.InsetsCssPx = new Insets { Top = 110, Bottom = 51 };
            LayoutPlan plan = BoardLayout.Build(layout, frame, viewport);
            Assert.That(plan.GroupBounds.ContainsKey("0:hand"), Is.EqualTo(!portrait));
            Assert.That(plan.GroupBounds.Keys.All(key => !key.EndsWith(":payment") &&
                !key.EndsWith(":wall.open") && !key.EndsWith(":kita")), Is.True);
            Assert.That(plan.Slots.Count, Is.EqualTo(layout.Slots.Length));
            Bounds[] groups = plan.GroupBounds.Values.ToArray();
            for (int a = 0; a < groups.Length; a++)
                for (int b = 0; b < a; b++)
                    Assert.That(Overlaps(groups[a], groups[b]), Is.False, "Public groups must not overlap");
            float aspect = plan.Viewport.BoardCss.width / plan.Viewport.BoardCss.height;
            CameraPose camera = CameraFit.Fit(plan.BoardBounds, aspect, portrait, 0, 0, plan.Compact);
            foreach (Entity entity in frame.Entities.Where(entity => entity.Group == "discard" || entity.Group == "meld"))
            {
                RenderPose pose = plan.Entities[entity.EntityId].Pose;
                Assert.That(pose.Scale, Is.EqualTo(2).Within(0.0001));
                Assert.That(Vector3.Distance(pose.Position, plan.Slots[entity.SlotName].Poses[0].Position), Is.LessThan(0.0001));
                Vector2 low = Project(pose.Position + pose.Rotation * new Vector3(-0.03f, 0.02f, 0) * pose.Scale,
                    camera, aspect, portrait, plan.Viewport.BoardCss);
                Vector2 high = Project(pose.Position + pose.Rotation * new Vector3(0.03f, 0.02f, 0) * pose.Scale,
                    camera, aspect, portrait, plan.Viewport.BoardCss);
                Assert.That(Vector2.Distance(low, high), Is.GreaterThanOrEqualTo(portrait ? 17 : 25),
                    "Public face short edge in CSS pixels: " + entity.SlotName);
            }
            for (int seat = 0; seat < 4; seat++)
            {
                Bounds discard = plan.GroupBounds[seat + ":discard"], meld = plan.GroupBounds[seat + ":meld"];
                Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(seat));
                Bounds a = BoardLayout.RotateBounds(discard, undo), b = BoardLayout.RotateBounds(meld, undo);
                Assert.That(a.min.z - b.max.z, Is.EqualTo(0.018f).Within(0.0001), "Meld/discard gutter");
            }
            Slot empty = SourceFixtures.Copy(layout.Slots.First(slot => slot.Group == "meld"), "Slot");
            empty.SlotName = "meld.empty@0";
            empty.Seat = 0;
            foreach (Contract.Pose pose in empty.Poses) pose.Position.X += 10000;
            layout.Slots = layout.Slots.Concat(new[] { empty }).ToArray();
            LayoutPlan withEmpty = BoardLayout.Build(layout, frame, viewport);
            Assert.That(withEmpty.BoardBounds, Is.EqualTo(plan.BoardBounds), "Unused reserves must never shrink the occupied board");
        }

        private static bool Overlaps(Bounds a, Bounds b) =>
            a.min.x < b.max.x - 0.0001f && a.max.x > b.min.x + 0.0001f &&
            a.min.z < b.max.z - 0.0001f && a.max.z > b.min.z + 0.0001f;

        private static Vector2 Project(Vector3 point, CameraPose camera, float aspect, bool flat, Rect viewport)
        {
            Vector3 local = Quaternion.Inverse(camera.Rotation) * (point - camera.Position);
            float half = flat ? camera.OrthographicSize : local.z * Mathf.Tan(CameraFit.FieldOfView * Mathf.Deg2Rad / 2);
            return new Vector2(local.x / (half * aspect) * viewport.width / 2, local.y / half * viewport.height / 2);
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

        [TestCase(360, 800)]
        [TestCase(390, 844)]
        [TestCase(500, 900)]
        public void FullTouchHandFitsSafeBandWithNoMeshOverlapAndDrawnGap(int width, int height)
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
            Viewport viewport = SourceFixtures.Copy(state.Viewport, "Viewport");
            viewport.WidthCssPx = width;
            viewport.HeightCssPx = height;
            viewport.WidthPx = Mathf.RoundToInt(width * (float)viewport.RenderPixelRatio);
            viewport.HeightPx = Mathf.RoundToInt(height * (float)viewport.RenderPixelRatio);
            viewport.InsetsCssPx = new Insets { Top = 114, Bottom = 51 };
            viewport.HandBandCssPx = Mathf.Min(180, (height - 114 - 51) * 0.3f, Mathf.Max(148, width * 0.24f));
            LayoutPlan plan = BoardLayout.Build(layout, frame, viewport);
            Bounds[] boxes = entities.Select(entity => SourceCoordinates.OrientedBounds(
                plan.Entities[entity.EntityId].Pose, "TILE")).ToArray();
            for (int a = 0; a < boxes.Length; a++)
            {
                Assert.That(boxes[a].min.x, Is.GreaterThanOrEqualTo(-plan.Viewport.HandCss.width * 0.005f));
                Assert.That(boxes[a].max.x, Is.LessThanOrEqualTo(plan.Viewport.HandCss.width * 0.005f));
                Assert.That(boxes[a].min.z, Is.GreaterThanOrEqualTo(-plan.Viewport.HandCss.height * 0.005f));
                Assert.That(boxes[a].max.z, Is.LessThanOrEqualTo(plan.Viewport.HandCss.height * 0.005f));
                Assert.That(boxes[a].size.x / SourceCoordinates.Unit, Is.GreaterThanOrEqualTo(40),
                    "Every displayed hand tile needs a readable, usable portrait touch target");
                for (int b = 0; b < a; b++)
                    Assert.That(boxes[a].min.x < boxes[b].max.x && boxes[a].max.x > boxes[b].min.x &&
                                boxes[a].min.z < boxes[b].max.z && boxes[a].max.z > boxes[b].min.z, Is.False);
            }
            Bounds drawn = boxes.Last();
            Assert.That(drawn.min.x - boxes[boxes.Length - 2].max.x, Is.GreaterThan(0.04f));
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void CameraFitContainsAllBoundsAtEveryZoomAndLookDown(bool flat, bool compact)
        {
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(2, 0.15f, 4));
            foreach (double zoom in new[] { -1.0, 0.0, 1.0 })
                foreach (double down in new[] { 0.0, 1.0 })
                {
                    CameraPose pose = CameraFit.Fit(bounds, 0.5f, flat, down, zoom, compact);
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
