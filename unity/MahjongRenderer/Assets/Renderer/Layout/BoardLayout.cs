using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Renderer.Contract;
using UnityEngine;

namespace Mahjong.Renderer.Layout
{
    public sealed class EntityPlacement
    {
        public Entity Entity;
        public RenderPose Pose;
        public bool Separate;
    }

    public sealed class SlotPlacement
    {
        public Slot Slot;
        public RenderPose[] Poses;
        public bool Separate;
    }

    public sealed class LayoutPlan
    {
        public ViewportGeometry Viewport;
        public bool Compact;
        public Bounds BoardBounds;
        public readonly Dictionary<string, EntityPlacement> Entities = new Dictionary<string, EntityPlacement>();
        public readonly Dictionary<string, SlotPlacement> Slots = new Dictionary<string, SlotPlacement>();
        public readonly Dictionary<string, Bounds> GroupBounds = new Dictionary<string, Bounds>();
    }

    public static class BoardLayout
    {
        private const float Padding = 0.04f;
        private static readonly string[] LaneOrder =
            { "discard", "riichi", "payment", "wall", "wall.open", "hand", "kita", "meld", "marker", "tray" };
        private static readonly string[] CompactLaneOrder =
            { "discard", "meld", "kita", "hand", "wall", "wall.open", "riichi", "payment", "marker", "tray" };

        private sealed class Group
        {
            public int Seat;
            public string Name;
            public Bounds Bounds;
            public bool Initialized;
            public Vector3 Translation;
            public bool Compact;
            public float Scale = 1;
            public readonly Dictionary<string, Vector3> Offsets = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        }

        public static LayoutPlan Build(RendererLayout layout, RendererFrame frame, Viewport viewport)
        {
            bool separate = frame.Presentation.HandLayout == "separate" && viewport.HandBandCssPx > 0;
            var result = new LayoutPlan { Viewport = new ViewportGeometry(viewport, separate) };
            Quaternion viewRotation = SourceCoordinates.ViewRotation(frame.Session.ViewSeat);
            var localIds = new HashSet<string>(frame.LocalHand.EntityIds, StringComparer.Ordinal);
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            bool rectangular = frame.Presentation.BoardLayout == "rectangular";
            bool compact = rectangular && layout.GameType == "CHANGSHA";
            result.Compact = compact;

            foreach (Slot slot in layout.Slots)
            {
                int seat = slot.Seat ?? 0;
                string key = Key(seat, slot.Group);
                if (!groups.TryGetValue(key, out Group group))
                {
                    group = new Group { Seat = seat, Name = slot.Group, Compact = compact,
                        Scale = compact && (slot.Group == "discard" || slot.Group == "meld") ? 2 : 1 };
                    groups.Add(key, group);
                }
                if (compact) continue;
                Quaternion undoSeat = Quaternion.Inverse(SourceCoordinates.SeatRotation(seat));
                foreach (Contract.Pose source in slot.Poses)
                {
                    RenderPose pose = SourceCoordinates.Convert(source);
                    var localPose = new RenderPose(undoSeat * pose.Position, undoSeat * pose.Rotation, pose.Size);
                    Bounds bounds = SourceCoordinates.OrientedBounds(localPose, slot.Kind);
                    if (!group.Initialized) { group.Bounds = bounds; group.Initialized = true; }
                    else group.Bounds.Encapsulate(bounds);
                }
            }

            if (compact)
            {
                PrepareCompact(groups, layout, frame, result.Viewport.BoardCss);
                foreach (Entity entity in frame.Entities)
                {
                    if (separate && localIds.Contains(entity.EntityId)) continue;
                    Group group = groups[Key(entity.Seat ?? 0, entity.Group)];
                    Bounds bounds = SourceCoordinates.OrientedBounds(
                        Normalize(SourceCoordinates.Convert(entity.Pose, entity.Scale), group, entity.SlotName), entity.Kind);
                    if (!group.Initialized) { group.Bounds = bounds; group.Initialized = true; }
                    else group.Bounds.Encapsulate(bounds);
                }
            }

            if (rectangular) Pack(groups, frame.Session.ViewSeat, result.Viewport.BoardCss, frame.Presentation, compact, result);
            else
            {
                result.BoardBounds = new Bounds(Vector3.zero, new Vector3(1.74f, 0.08f, 1.74f));
                foreach (Group group in groups.Values)
                {
                    Quaternion rotation = viewRotation * SourceCoordinates.SeatRotation(group.Seat);
                    Bounds bounds = RotateBounds(group.Bounds, rotation);
                    result.BoardBounds.Encapsulate(bounds);
                    result.GroupBounds[Key(group.Seat, group.Name)] = bounds;
                }
                result.BoardBounds.Expand(new Vector3(Padding * 2, 0, Padding * 2));
            }

            foreach (Slot slot in layout.Slots)
            {
                Group group = groups[Key(slot.Seat ?? 0, slot.Group)];
                var poses = new RenderPose[slot.Poses.Length];
                for (int i = 0; i < poses.Length; i++)
                    poses[i] = Map(SourceCoordinates.Convert(slot.Poses[i]), group, slot.SlotName, viewRotation, rectangular);
                result.Slots.Add(slot.SlotName, new SlotPlacement { Slot = slot, Poses = poses });
            }
            foreach (Entity entity in frame.Entities)
            {
                Group group = groups[Key(entity.Seat ?? 0, entity.Group)];
                RenderPose pose = Map(SourceCoordinates.Convert(entity.Pose, entity.Scale), group, entity.SlotName, viewRotation, rectangular);
                result.Entities.Add(entity.EntityId, new EntityPlacement
                {
                    Entity = entity, Pose = pose, Separate = separate && localIds.Contains(entity.EntityId)
                });
            }
            if (separate) SeparateHand(result, frame, layout);
            return result;
        }

        private static void PrepareCompact(Dictionary<string, Group> groups, RendererLayout layout, RendererFrame frame, Rect viewport)
        {
            var occupied = new HashSet<string>(frame.Entities.Select(entity => entity.SlotName), StringComparer.Ordinal);
            foreach (Group group in groups.Values)
            {
                if (group.Name != "discard" && group.Name != "meld" && group.Name != "wall") continue;
                Slot[] slots = layout.Slots.Where(slot => (slot.Seat ?? 0) == group.Seat && slot.Group == group.Name &&
                    occupied.Contains(slot.SlotName)).OrderBy(slot => slot.Indexes.Length == 0 ? 0 : slot.Indexes[0])
                    .ThenBy(slot => slot.Indexes.Length < 2 ? 0 : slot.Indexes[1]).ToArray();
                Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(group.Seat));
                int columns = viewport.width < viewport.height &&
                    SourceCoordinates.RelativeSeat(group.Seat, frame.Session.ViewSeat) % 2 != 0 ? 12 : 6;
                long previousMeld = -1;
                long previousStack = -1;
                float x = 0;
                int stack = -1;
                for (int i = 0; i < slots.Length; i++)
                {
                    Slot slot = slots[i];
                    Vector3 source = undo * SourceCoordinates.Position(slot.Poses[0].Position);
                    Vector3 target = source;
                    if (group.Name == "discard")
                    {
                        target.x = i % columns * 0.064f;
                        target.z = -(i / columns) * 0.094f;
                    }
                    else if (group.Name == "meld")
                    {
                        long meld = slot.Indexes.Length == 0 ? 0 : slot.Indexes[0];
                        if (i > 0 && meld != previousMeld) x += 0.018f;
                        target.x = x;
                        target.z = 0;
                        x += 0.064f;
                        previousMeld = meld;
                    }
                    else
                    {
                        long column = slot.Indexes.Length == 0 ? i : slot.Indexes[0];
                        if (column != previousStack) stack++;
                        target.x = stack * 0.062f;
                        previousStack = column;
                    }
                    group.Offsets.Add(slot.SlotName, target - source);
                }
            }
        }

        private static RenderPose Normalize(RenderPose pose, Group group, string slotName)
        {
            Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(group.Seat));
            Vector3 position = undo * pose.Position;
            if (group.Offsets.TryGetValue(slotName, out Vector3 offset)) position += offset;
            return new RenderPose(position * group.Scale, undo * pose.Rotation, pose.Size * group.Scale,
                pose.Scale * group.Scale);
        }

        private static RenderPose Map(RenderPose pose, Group group, string slotName, Quaternion view, bool rectangular)
        {
            if (group.Compact)
            {
                RenderPose local = Normalize(pose, group, slotName);
                Quaternion rotation = view * SourceCoordinates.SeatRotation(group.Seat);
                return new RenderPose(rotation * (local.Position + group.Translation), rotation * local.Rotation,
                    local.Size, local.Scale);
            }
            // Slot poses already contain the source seat rotation; only a group translation and POV rotation remain.
            Vector3 translated = pose.Position;
            if (rectangular) translated += SourceCoordinates.SeatRotation(group.Seat) * group.Translation;
            return new RenderPose(view * translated, view * pose.Rotation, pose.Size, pose.Scale);
        }

        private static void Pack(Dictionary<string, Group> groups, int? viewSeat, Rect viewport,
            Presentation presentation, bool compact, LayoutPlan result)
        {
            float padding = compact ? 0.018f : Padding;
            float[] thickness = new float[4];
            float centerWidth = compact ? 0.12f : 0.35f;
            float centerDepth = compact ? 0.12f : 0.35f;
            foreach (Group group in groups.Values.Where(value => value.Initialized))
            {
                int side = SourceCoordinates.RelativeSeat(group.Seat, viewSeat);
                thickness[side] += group.Bounds.size.z + padding;
                if (side % 2 == 0) centerWidth = Mathf.Max(centerWidth, group.Bounds.size.x + padding * 2);
                else centerDepth = Mathf.Max(centerDepth, group.Bounds.size.x + padding * 2);
            }
            float width = centerWidth + thickness[1] + thickness[3] + padding * 2;
            float depth = centerDepth + thickness[0] + thickness[2] + padding * 2;
            float projection = compact && presentation.ViewMode != "flat" ?
                Mathf.Sin(Mathf.Lerp(38, 78, (float)presentation.LookDown) * Mathf.Deg2Rad) : 1;
            float aspect = Mathf.Clamp(viewport.width / viewport.height * projection, 0.35f, 3f);
            if (width / depth < aspect) centerWidth += depth * aspect - width;
            else centerDepth += width / aspect - depth;
            width = centerWidth + thickness[1] + thickness[3] + padding * 2;
            depth = centerDepth + thickness[0] + thickness[2] + padding * 2;
            var offsets = new float[4];
            Quaternion view = SourceCoordinates.ViewRotation(viewSeat);
            foreach (string name in compact ? CompactLaneOrder : LaneOrder)
            {
                foreach (Group group in groups.Values.Where(value => value.Name == name && value.Initialized).OrderBy(value => value.Seat))
                {
                    int side = SourceCoordinates.RelativeSeat(group.Seat, viewSeat);
                    float inner = side % 2 == 0 ? centerDepth / 2 : centerWidth / 2;
                    float z = -inner - offsets[side] - group.Bounds.size.z / 2;
                    group.Translation = new Vector3(-group.Bounds.center.x, 0, z - group.Bounds.center.z);
                    offsets[side] += group.Bounds.size.z + padding;
                    Bounds local = group.Bounds;
                    local.center += group.Translation;
                    result.GroupBounds[Key(group.Seat, group.Name)] =
                        RotateBounds(local, view * SourceCoordinates.SeatRotation(group.Seat));
                }
            }
            result.BoardBounds = new Bounds(
                new Vector3((thickness[1] - thickness[3]) / 2, 0, (thickness[2] - thickness[0]) / 2),
                new Vector3(width, 0.08f, depth));
            foreach (Bounds bounds in result.GroupBounds.Values) result.BoardBounds.Encapsulate(bounds);
        }

        private static void SeparateHand(LayoutPlan plan, RendererFrame frame, RendererLayout layout)
        {
            if (frame.LocalHand.Seat == null) return;
            var ordered = frame.LocalHand.EntityIds.Where(id => id != frame.LocalHand.DrawnEntityId).ToList();
            if (frame.LocalHand.DrawnEntityId != null) ordered.Add(frame.LocalHand.DrawnEntityId);
            Slot[] targets = layout.Slots.Where(slot => slot.Group == "hand" && slot.Seat == frame.LocalHand.Seat)
                .OrderBy(slot => slot.Indexes.Length == 0 ? long.MaxValue : slot.Indexes[0]).ToArray();
            // Changsha's spare drag-only slot must not force an eighth touch column.
            int count = layout.GameType == "CHANGSHA" ? Mathf.Max(14, ordered.Count) : Mathf.Max(ordered.Count, targets.Length);
            if (count == 0) return;
            Rect band = plan.Viewport.HandCss;
            float availableWidth = Mathf.Max(1, band.width - 16);
            float availableHeight = Mathf.Max(1, band.height - 12);
            int bestRows = 1;
            float bestWidth = 0;
            int columns = count;
            for (int rows = 1; rows <= count; rows++)
            {
                int cols = Mathf.CeilToInt(count / (float)rows);
                float tileWidth = Mathf.Min(44, (availableWidth - (cols - 1) * 4) / (cols + 0.5f),
                    (availableHeight - (rows - 1) * 6) / (rows * 1.5f));
                if (tileWidth > bestWidth)
                {
                    bestWidth = tileWidth;
                    bestRows = rows;
                    columns = cols;
                }
                if (tileWidth >= 32) break;
            }
            bestWidth = Mathf.Max(1, bestWidth);
            float tileHeight = bestWidth * 1.5f;
            float scale = bestWidth * SourceCoordinates.Unit / SourceCoordinates.MeshSize("TILE").x;
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++)
            {
                EntityPlacement item = plan.Entities[ordered[i]];
                bool drawn = item.Entity.EntityId == frame.LocalHand.DrawnEntityId;
                RenderPose pose = HandPose(i, count, columns, bestRows, bestWidth, tileHeight, scale, drawn);
                item.Pose = pose;
                item.Separate = true;
                if (occupied.Add(item.Entity.SlotName))
                    plan.Slots[item.Entity.SlotName] = new SlotPlacement
                    {
                        Slot = plan.Slots[item.Entity.SlotName].Slot, Separate = true, Poses = new[] { pose }
                    };
            }
            int emptyIndex = ordered.Count;
            int drawnIndex = frame.LocalHand.DrawnEntityId == null ? -1 : ordered.Count - 1;
            foreach (Slot slot in targets)
            {
                if (occupied.Contains(slot.SlotName)) continue;
                plan.Slots[slot.SlotName] = new SlotPlacement
                {
                    Slot = slot, Separate = true,
                    Poses = new[] { HandPose(emptyIndex, count, columns, bestRows, bestWidth, tileHeight, scale,
                        drawnIndex >= 0 && emptyIndex / columns == drawnIndex / columns) }
                };
                emptyIndex++;
            }
        }

        private static RenderPose HandPose(int index, int count, int columns, int rows,
            float width, float height, float scale, bool drawn)
        {
            int row = index / columns;
            int col = index % columns;
            int rowCount = Mathf.Min(columns, count - row * columns);
            float rowWidth = rowCount * width + (rowCount - 1) * 4 + width * 0.5f;
            float x = -rowWidth / 2 + width / 2 + col * (width + 4) + (drawn ? width * 0.5f : 0);
            float z = (rows - 1) * (height + 6) / 2 - row * (height + 6);
            return new RenderPose(new Vector3(x * SourceCoordinates.Unit, 0.02f * scale, z * SourceCoordinates.Unit),
                Quaternion.identity, SourceCoordinates.MeshSize("TILE") * scale, scale);
        }

        public static Bounds RotateBounds(Bounds source, Quaternion rotation)
        {
            var result = new Bounds(rotation * source.center, Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        result.Encapsulate(rotation * (source.center +
                            Vector3.Scale(source.extents, new Vector3(x, y, z))));
            return result;
        }

        private static string Key(int seat, string group) => seat + ":" + group;
    }
}
