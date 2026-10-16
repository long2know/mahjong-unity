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
        public void TraditionalChangshaHasCentralWallsBoundedRiversAndHandAdjacentMelds(int width, int height)
        {
            RendererLayout layout = SourceFixtures.CanonicalChangsha();
            RendererFrame frame = TraditionalFrame(layout);
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
            Assert.That(plan.GroupBounds.ContainsKey("0:meld"), Is.EqualTo(!portrait));
            Assert.That(plan.Slots.Count, Is.EqualTo(layout.Slots.Length));
            Assert.That(plan.Rivers.Count, Is.EqualTo(4));
            foreach (var river in plan.Rivers)
            {
                Assert.That(Mathf.Max(Mathf.Abs(river.Value.min.x), Mathf.Abs(river.Value.max.x),
                    Mathf.Abs(river.Value.min.z), Mathf.Abs(river.Value.max.z)), Is.LessThan(0.3675f));
                foreach (var other in plan.Rivers.Where(other => other.Key != river.Key))
                    Assert.That(Overlaps(river.Value, other.Value), Is.False, "Each player has a separate river");
            }
            foreach (Entity entity in frame.Entities.Where(entity => entity.Group == "wall"))
                Assert.That(Vector3.Distance(plan.Entities[entity.EntityId].Pose.Position,
                    SourceCoordinates.Position(entity.Pose.Position)), Is.LessThan(0.00001), entity.SlotName);
            float aspect = plan.Viewport.BoardCss.width / plan.Viewport.BoardCss.height;
            CameraPose camera = CameraFit.Fit(plan.BoardBounds, aspect, portrait, 0, 0, plan.Compact);
            foreach (Entity entity in frame.Entities.Where(entity => (entity.Group == "discard" || entity.Group == "meld") &&
                !plan.Entities[entity.EntityId].Separate))
            {
                RenderPose pose = plan.Entities[entity.EntityId].Pose;
                Assert.That(pose.Scale, Is.EqualTo(1).Within(0.0001));
                Assert.That(Vector3.Distance(pose.Position, plan.Slots[entity.SlotName].Poses[0].Position), Is.LessThan(0.0001));
                if (entity.Group == "discard")
                {
                    Bounds river = plan.Rivers[entity.Seat.Value];
                    Bounds tile = SourceCoordinates.OrientedBounds(pose, entity.Kind);
                    Assert.That(tile.min.x, Is.GreaterThanOrEqualTo(river.min.x - 0.0001f));
                    Assert.That(tile.max.x, Is.LessThanOrEqualTo(river.max.x + 0.0001f));
                    Assert.That(tile.min.z, Is.GreaterThanOrEqualTo(river.min.z - 0.0001f));
                    Assert.That(tile.max.z, Is.LessThanOrEqualTo(river.max.z + 0.0001f));
                }
                Vector2 low = Project(pose.Position + pose.Rotation * new Vector3(-0.03f, 0.02f, 0) * pose.Scale,
                    camera, aspect, portrait, plan.Viewport.BoardCss);
                Vector2 high = Project(pose.Position + pose.Rotation * new Vector3(0.03f, 0.02f, 0) * pose.Scale,
                    camera, aspect, portrait, plan.Viewport.BoardCss);
                Assert.That(Vector2.Distance(low, high), Is.GreaterThanOrEqualTo(portrait ? 17 : 25),
                    "Public face short edge in CSS pixels: " + entity.SlotName);
            }
            for (int seat = 0; seat < 4; seat++)
            {
                if (portrait && seat == 0) continue;
                Bounds hand = plan.GroupBounds[seat + ":hand"], meld = plan.GroupBounds[seat + ":meld"];
                Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(seat));
                Bounds a = BoardLayout.RotateBounds(hand, undo), b = BoardLayout.RotateBounds(meld, undo);
                Assert.That(a.center.z, Is.EqualTo(b.center.z).Within(0.0001), "Meld and remaining hand share one outer row");
                Assert.That(a.min.x - b.max.x, Is.EqualTo(0.012f).Within(0.0001), "Melds sit beside the hand, not beside discards");
                Assert.That(b.max.z, Is.LessThan(-0.4725f), "Player melds remain outside the central wall");
            }
            if (portrait)
                Assert.That(frame.Entities.Where(entity => entity.Seat == 0 && entity.Group == "meld")
                    .All(entity => plan.Entities[entity.EntityId].Separate), Is.True, "Local melds follow the separate hand");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryCanonicalWallPoseAndDrawGapSurvivesDepletionAndViewChanges(int viewSeat)
        {
            RendererLayout layout = SourceFixtures.CanonicalChangsha();
            RendererFrame frame = SourceFixtures.Empty(layout);
            frame.Presentation.BoardLayout = "rectangular";
            frame.Presentation.HandLayout = "in-table";
            frame.Session.ViewSeat = viewSeat;
            Slot[] wall = layout.Slots.Where(slot => slot.Group == "wall").ToArray();
            frame.Entities = wall.Select(slot => At(slot)).ToArray();
            Viewport viewport = SourceFixtures.Read<Viewport>("Viewport");
            LayoutPlan full = BoardLayout.Build(layout, frame, viewport);
            frame.Entities = frame.Entities.Where(entity => entity.Indexes[0] != 7 &&
                (entity.Seat != 1 || entity.Indexes[0] >= 10) &&
                (entity.Seat != 3 || entity.Indexes[0] <= 3)).ToArray();
            LayoutPlan depleted = BoardLayout.Build(layout, frame, viewport);
            Assert.That(depleted.BoardBounds, Is.EqualTo(full.BoardBounds), "Drawing cannot move or zoom the wall ring");
            Quaternion view = SourceCoordinates.ViewRotation(viewSeat);
            foreach (Slot slot in wall)
                for (int index = 0; index < slot.Poses.Length; index++)
                {
                    RenderPose actual = depleted.Slots[slot.SlotName].Poses[index];
                    RenderPose source = SourceCoordinates.Convert(slot.Poses[index]);
                    Assert.That(Vector3.Distance(actual.Position, view * source.Position), Is.LessThan(0.00001), slot.SlotName);
                    Assert.That(Quaternion.Angle(actual.Rotation, view * source.Rotation), Is.LessThan(0.001), slot.SlotName);
                    Assert.That(actual.Scale, Is.EqualTo(source.Scale), slot.SlotName);
                    Assert.That(Vector3.Distance(actual.Position, full.Slots[slot.SlotName].Poses[index].Position),
                        Is.LessThan(0.00001), "Vacant draw stacks must not be collapsed");
                }
            foreach (Entity entity in frame.Entities)
                Assert.That(Vector3.Distance(depleted.Entities[entity.EntityId].Pose.Position,
                    full.Entities[entity.EntityId].Pose.Position), Is.LessThan(0.00001), entity.SlotName);
        }

        [TestCase(1, false)]
        [TestCase(2, false)]
        [TestCase(3, false)]
        [TestCase(4, false)]
        [TestCase(1, true)]
        [TestCase(4, true)]
        public void SeparateMeldsStayWithTheHandWithoutShrinkingDiscardTargets(int count, bool added)
        {
            RendererLayout layout = SourceFixtures.CanonicalChangsha();
            RendererFrame frame = TraditionalFrame(layout);
            var entities = frame.Entities.Where(entity => entity.Seat != 0 ||
                entity.Group != "hand" && entity.Group != "meld").ToList();
            entities.AddRange(layout.Slots.Where(slot => slot.Seat == 0 && slot.Group == "hand" &&
                slot.Indexes.Length > 0 && slot.Indexes[0] < 14 - count * 3).Select(slot => At(slot)));
            entities.AddRange(layout.Slots.Where(slot => slot.Seat == 0 && slot.Group == "meld" &&
                slot.Indexes[0] < count && (added ? slot.Indexes[1] != 3 : slot.Indexes[1] < 4)).Select(slot => At(slot)));
            frame.Entities = entities.ToArray();
            frame.LocalHand.EntityIds = entities.Where(entity => entity.Seat == 0 && entity.Group == "hand")
                .Select(entity => entity.EntityId).ToArray();
            frame.LocalHand.DrawnEntityId = frame.LocalHand.EntityIds.Last();
            frame.Presentation.BoardLayout = "rectangular";
            frame.Presentation.HandLayout = "separate";
            Viewport viewport = SourceFixtures.Read<Viewport>("Viewport");
            viewport.WidthCssPx = 360;
            viewport.HeightCssPx = 800;
            viewport.WidthPx = Mathf.RoundToInt(360 * (float)viewport.RenderPixelRatio);
            viewport.HeightPx = Mathf.RoundToInt(800 * (float)viewport.RenderPixelRatio);
            viewport.HandBandCssPx = 148;
            LayoutPlan plan = BoardLayout.Build(layout, frame, viewport);
            foreach (Entity entity in entities.Where(entity => entity.Seat == 0 &&
                (entity.Group == "hand" || entity.Group == "meld")))
            {
                EntityPlacement item = plan.Entities[entity.EntityId];
                Assert.That(item.Separate, Is.True);
                Bounds box = SourceCoordinates.OrientedBounds(item.Pose, "TILE");
                Assert.That(box.min.x / SourceCoordinates.Unit, Is.GreaterThanOrEqualTo(-172.001f));
                Assert.That(box.max.x / SourceCoordinates.Unit, Is.LessThanOrEqualTo(172.001f));
                Assert.That(box.min.z / SourceCoordinates.Unit, Is.GreaterThanOrEqualTo(-68.001f));
                Assert.That(box.max.z / SourceCoordinates.Unit, Is.LessThanOrEqualTo(68.001f));
                if (entity.Group == "hand")
                    Assert.That(box.size.x / SourceCoordinates.Unit, Is.GreaterThanOrEqualTo(40));
            }
            Assert.That(plan.GroupBounds.ContainsKey("0:meld"), Is.False, "Do not draw the same local melds on the table");
            if (added)
            {
                Entity lower = entities.First(entity => entity.SlotName == "meld.0.1@0");
                Entity upper = entities.First(entity => entity.SlotName == "meld.0.4@0");
                RenderPose a = plan.Entities[lower.EntityId].Pose, b = plan.Entities[upper.EntityId].Pose;
                Assert.That(a.Position.x, Is.EqualTo(b.Position.x).Within(0.0001));
                Assert.That(a.Position.z, Is.EqualTo(b.Position.z).Within(0.0001));
                Assert.That(b.Position.y - a.Position.y, Is.EqualTo(0.04f * a.Scale).Within(0.0001));
            }
        }

        private static Entity At(Slot slot)
        {
            Entity entity = SourceFixtures.Copy(SourceFixtures.Read<RendererFrame>("RendererFrame").Entities[0], "Entity");
            entity.EntityId = slot.SlotName;
            entity.SlotName = slot.SlotName;
            entity.Group = slot.Group;
            entity.Seat = slot.Seat;
            entity.Indexes = slot.Indexes;
            entity.Pose = slot.Poses[0];
            entity.RotationIndex = 0;
            entity.Interaction = new Interaction();
            entity.Appearance.ShowFace = slot.Group == "discard" || slot.Group == "meld" || slot.Group == "hand" && slot.Seat == 0;
            entity.Appearance.KnownFace = entity.Appearance.ShowFace;
            entity.Appearance.FaceIndex = entity.Appearance.ShowFace ? 0 : (int?)null;
            return entity;
        }

        private static RendererFrame TraditionalFrame(RendererLayout layout)
        {
            RendererFrame frame = SourceFixtures.Empty(layout);
            var publicAndHands = layout.Slots.Where(slot =>
                slot.Group == "hand" && slot.Indexes.Length > 0 && slot.Indexes[0] < (slot.Seat == 0 ? 11 : 10) ||
                slot.Group == "meld" && slot.Indexes[0] == 0 && slot.Indexes[1] < 3 ||
                slot.Group == "discard" && slot.Indexes.Length == 2 && slot.Indexes[0] == 0)
                .Select(slot => At(slot)).ToList();
            publicAndHands.AddRange(layout.Slots.Where(slot => slot.Group == "wall")
                .Skip(35).Take(108 - publicAndHands.Count).Select(slot => At(slot)));
            frame.Entities = publicAndHands.ToArray();
            frame.LocalHand.Seat = 0;
            frame.LocalHand.EntityIds = frame.Entities.Where(entity => entity.Group == "hand" && entity.Seat == 0)
                .Select(entity => entity.EntityId).ToArray();
            return frame;
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

        [TestCase(true)]
        [TestCase(false)]
        public void DefaultOverridePreservesExactFitAndManualScaleReallyResizesTheTable(bool flat)
        {
            var bounds = new Bounds(new Vector3(0.2f, 0.05f, -0.4f), new Vector3(2, 0.15f, 4));
            CameraPose original = CameraFit.Fit(bounds, 0.6f, flat, 0, 0, true);
            CameraPose defaults = CameraFit.Fit(bounds, 0.6f, flat, 0, 0, true, new CameraView());
            Assert.That(defaults.Position, Is.EqualTo(original.Position));
            Assert.That(defaults.Rotation, Is.EqualTo(original.Rotation));
            Assert.That(defaults.OrthographicSize, Is.EqualTo(original.OrthographicSize));
            CameraPose enlarged = CameraFit.Fit(bounds, 0.6f, flat, 0, 0, true, new CameraView { Scale = 1.5 });
            Assert.That(enlarged.OrthographicSize, Is.EqualTo(original.OrthographicSize / 1.5f).Within(0.00001f));
            if (!flat)
                Assert.That(Vector3.Distance(enlarged.Position, bounds.center),
                    Is.LessThan(Vector3.Distance(original.Position, bounds.center) * 0.9f));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ThreeAxisCameraFitsEveryCornerAtUnitScaleAndNeverCrossesTheNearPlaneWhenZoomed(bool flat)
        {
            var bounds = new Bounds(new Vector3(0.2f, 0.05f, -0.4f), new Vector3(2, 0.15f, 4));
            foreach (double yaw in new[] { -170.0, 0, 90 })
                foreach (double roll in new[] { -120.0, 0, 35 })
                    foreach (double pitch in new[] { 20.0, 55, 90 })
                    {
                        var view = new CameraView { Yaw = yaw, Roll = roll, Pitch = pitch };
                        CameraPose pose = CameraFit.Fit(bounds, 0.6f, flat, 0, 0, true, view);
                        Assert.That(Quaternion.Angle(pose.Rotation,
                            Quaternion.Euler(flat ? 90 : (float)pitch, (float)yaw, (float)roll)), Is.LessThan(0.05f));
                        Quaternion inverse = Quaternion.Inverse(pose.Rotation);
                        view.Scale = 2.5;
                        CameraPose close = CameraFit.Fit(bounds, 0.6f, flat, 0, 0, true, view);
                        Assert.That(close.Position.y, Is.GreaterThan(bounds.max.y));
                        for (int x = -1; x <= 1; x += 2)
                            for (int y = -1; y <= 1; y += 2)
                                for (int z = -1; z <= 1; z += 2)
                                {
                                    Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                                    Vector3 local = inverse * (point - pose.Position);
                                    float half = flat ? pose.OrthographicSize : local.z * Mathf.Tan(CameraFit.FieldOfView * Mathf.Deg2Rad / 2);
                                    Assert.That(Mathf.Abs(local.y), Is.LessThanOrEqualTo(half + 0.0001f));
                                    Assert.That(Mathf.Abs(local.x), Is.LessThanOrEqualTo(half * 0.6f + 0.0001f));
                                    Assert.That((Quaternion.Inverse(close.Rotation) * (point - close.Position)).z,
                                        Is.GreaterThan(0.025f), "Manual zoom may crop, but must not pass through table geometry");
                                }
                    }
        }
    }
}
