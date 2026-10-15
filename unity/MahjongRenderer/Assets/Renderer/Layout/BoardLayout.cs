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
        public readonly Dictionary<int, Bounds> Rivers = new Dictionary<int, Bounds>();
        public float CenterSize = 0.338f;
    }

    public static class BoardLayout
    {
        private const float Padding = 0.04f;
        private static readonly string[] LaneOrder =
            { "discard", "riichi", "payment", "wall", "wall.open", "hand", "kita", "meld", "marker", "tray" };
        private sealed class Group
        {
            public int Seat;
            public string Name;
            public Bounds Bounds;
            public bool Initialized;
            public Vector3 Translation;
            public bool Compact;
            public readonly Dictionary<string, Vector2> Positions = new Dictionary<string, Vector2>(StringComparer.Ordinal);
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
                    group = new Group { Seat = seat, Name = slot.Group, Compact = compact };
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
                PrepareTraditional(groups, layout, frame, separate, result);
                foreach (Entity entity in frame.Entities)
                {
                    if (IsSeparate(entity, frame, localIds, separate, compact)) continue;
                    Group group = groups[Key(entity.Seat ?? 0, entity.Group)];
                    Bounds bounds = SourceCoordinates.OrientedBounds(
                        Normalize(SourceCoordinates.Convert(entity.Pose, entity.Scale), group, entity.SlotName), entity.Kind);
                    if (!group.Initialized) { group.Bounds = bounds; group.Initialized = true; }
                    else group.Bounds.Encapsulate(bounds);
                }
                float edge = result.BoardBounds.extents.x;
                float low = -0.083f, high = 0.08f;
                foreach (Group group in groups.Values.Where(value => value.Initialized))
                {
                    Bounds bounds = RotateBounds(group.Bounds, viewRotation * SourceCoordinates.SeatRotation(group.Seat));
                    result.GroupBounds[Key(group.Seat, group.Name)] = bounds;
                    edge = Mathf.Max(edge, Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x),
                        Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
                    low = Mathf.Min(low, bounds.min.y);
                    high = Mathf.Max(high, bounds.max.y);
                }
                result.BoardBounds = new Bounds(new Vector3(0, (low + high) / 2, 0),
                    new Vector3(edge * 2, high - low, edge * 2));
            }
            else if (rectangular) Pack(groups, frame.Session.ViewSeat, result.Viewport.BoardCss, result);
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
                    Entity = entity, Pose = pose, Separate = IsSeparate(entity, frame, localIds, separate, compact)
                });
            }
            if (separate) SeparateHand(result, frame, layout);
            return result;
        }

        private static bool IsSeparate(Entity entity, RendererFrame frame, HashSet<string> hand, bool separate, bool traditional) =>
            separate && (hand.Contains(entity.EntityId) ||
                traditional && entity.Group == "meld" && entity.Seat == frame.LocalHand.Seat);

        private static void PrepareTraditional(Dictionary<string, Group> groups, RendererLayout layout,
            RendererFrame frame, bool separate, LayoutPlan plan)
        {
            float inner = float.PositiveInfinity;
            float outer = 0;
            foreach (Slot slot in layout.Slots.Where(slot => slot.Group == "wall"))
            {
                Group group = groups[Key(slot.Seat ?? 0, slot.Group)];
                foreach (Contract.Pose source in slot.Poses)
                {
                    Bounds bounds = SourceCoordinates.OrientedBounds(
                        Normalize(SourceCoordinates.Convert(source), group, slot.SlotName), slot.Kind);
                    inner = Mathf.Min(inner, -bounds.max.z);
                    outer = Mathf.Max(outer, Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x),
                        Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
                    if (!group.Initialized) { group.Bounds = bounds; group.Initialized = true; }
                    else group.Bounds.Encapsulate(bounds);
                }
            }

            foreach (Group group in groups.Values.Where(value => value.Name == "discard"))
            {
                Slot[] grid = layout.Slots.Where(slot => slot.Group == "discard" && slot.Seat == group.Seat &&
                    slot.Indexes.Length == 2).ToArray();
                if (grid.Length == 0 || float.IsInfinity(inner)) continue;
                int rows = (int)grid.Max(slot => slot.Indexes[0]) + 1;
                int columns = (int)grid.Max(slot => slot.Indexes[1]) + 1;
                float width = columns * 0.06f + (columns - 1) * 0.002f;
                float depth = rows * 0.09f + (rows - 1) * 0.002f;
                float hole = inner - depth - 0.018f;
                foreach (Slot slot in grid)
                    group.Positions[slot.SlotName] = new Vector2(
                        hole - 0.006f - width + 0.03f + slot.Indexes[1] * 0.062f,
                        -hole - 0.006f - 0.045f - slot.Indexes[0] * 0.092f);
                var river = new Bounds(new Vector3(hole - 0.006f - width / 2, -0.004f,
                    -hole - 0.006f - depth / 2), new Vector3(width + 0.006f, 0, depth + 0.006f));
                plan.Rivers[group.Seat] = RotateBounds(river,
                    SourceCoordinates.ViewRotation(frame.Session.ViewSeat) * SourceCoordinates.SeatRotation(group.Seat));
                plan.CenterSize = Mathf.Min(plan.CenterSize, 2 * (hole + 0.006f) - 0.012f);
            }

            float radius = outer + 0.063f;
            var rowsBySeat = new Dictionary<int, Entity[]>();
            foreach (int seat in layout.ActiveSeats)
            {
                if (separate && seat == frame.LocalHand.Seat) continue;
                Entity[] row = SeatRow(frame, seat);
                rowsBySeat[seat] = row;
                float width = RowWidth(row, frame.LocalHand.DrawnEntityId);
                radius = Mathf.Max(radius, (width + 0.114f) / 2);
            }
            foreach (var pair in rowsBySeat)
            {
                Entity[] row = pair.Value;
                float x = -RowWidth(row, frame.LocalHand.DrawnEntityId) / 2;
                Entity previous = null;
                foreach (Entity entity in row)
                {
                    x += RowGap(previous, entity, frame.LocalHand.DrawnEntityId);
                    float width = LocalWidth(entity);
                    groups[Key(pair.Key, entity.Group)].Positions[entity.SlotName] = new Vector2(x + width / 2, -radius);
                    x += width;
                    previous = entity;
                }
                foreach (Entity entity in frame.Entities.Where(entity => entity.Seat == pair.Key &&
                    entity.Group == "meld" && IsAddedKong(entity, frame.Entities)))
                {
                    Entity lower = frame.Entities.First(item => item.Seat == entity.Seat && item.Group == "meld" &&
                        item.Indexes[0] == entity.Indexes[0] && item.Indexes[1] == 1);
                    groups[Key(pair.Key, "meld")].Positions[entity.SlotName] =
                        groups[Key(pair.Key, "meld")].Positions[lower.SlotName];
                }
            }
            float edge = radius + 0.057f;
            plan.BoardBounds = new Bounds(Vector3.zero, new Vector3(edge * 2, 0.16f, edge * 2));
        }

        private static bool IsAddedKong(Entity entity, Entity[] entities) =>
            entity.Group == "meld" && entity.Indexes.Length == 2 && entity.Indexes[1] == 4 &&
            entities.Any(lower => lower.Seat == entity.Seat && lower.Group == "meld" &&
                lower.Indexes.Length == 2 && lower.Indexes[0] == entity.Indexes[0] && lower.Indexes[1] == 1);

        private static Entity[] SeatRow(RendererFrame frame, int seat) =>
            frame.Entities.Where(entity => entity.Seat == seat && entity.Group == "meld" &&
                !IsAddedKong(entity, frame.Entities)).OrderBy(MeldIndex)
                .ThenBy(MeldTileIndex)
                .Concat(frame.Entities.Where(entity => entity.Seat == seat && entity.Group == "hand")
                    .OrderBy(entity => entity.EntityId == frame.LocalHand.DrawnEntityId ? 1 : 0)
                    .ThenBy(entity => (Quaternion.Inverse(SourceCoordinates.SeatRotation(seat)) *
                        SourceCoordinates.Position(entity.Pose.Position)).x)).ToArray();

        private static long MeldIndex(Entity entity) => entity.Indexes.Length > 0 ? entity.Indexes[0] : 0;
        private static long MeldTileIndex(Entity entity) => entity.Indexes.Length > 1 ? entity.Indexes[1] : 0;

        private static float LocalWidth(Entity entity)
        {
            RenderPose pose = SourceCoordinates.Convert(entity.Pose, entity.Scale);
            Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(entity.Seat ?? 0));
            return SourceCoordinates.OrientedBounds(new RenderPose(Vector3.zero, undo * pose.Rotation,
                pose.Size, pose.Scale), entity.Kind).size.x;
        }

        private static float RowGap(Entity previous, Entity entity, string drawn)
        {
            if (previous == null) return 0;
            if (entity.EntityId == drawn) return 0.03f;
            return previous.Group != entity.Group || entity.Group == "meld" &&
                MeldIndex(previous) != MeldIndex(entity) ? 0.012f : 0.002f;
        }

        private static float RowWidth(Entity[] row, string drawn)
        {
            float width = 0;
            Entity previous = null;
            foreach (Entity entity in row)
            {
                width += RowGap(previous, entity, drawn) + LocalWidth(entity);
                previous = entity;
            }
            return width;
        }

        private static RenderPose Normalize(RenderPose pose, Group group, string slotName)
        {
            Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(group.Seat));
            Vector3 position = undo * pose.Position;
            if (group.Positions.TryGetValue(slotName, out Vector2 target))
            {
                position.x = target.x;
                position.z = target.y;
            }
            return new RenderPose(position, undo * pose.Rotation, pose.Size, pose.Scale);
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

        private static void Pack(Dictionary<string, Group> groups, int? viewSeat, Rect viewport, LayoutPlan result)
        {
            float padding = Padding;
            float[] thickness = new float[4];
            float centerWidth = 0.35f;
            float centerDepth = 0.35f;
            foreach (Group group in groups.Values.Where(value => value.Initialized))
            {
                int side = SourceCoordinates.RelativeSeat(group.Seat, viewSeat);
                thickness[side] += group.Bounds.size.z + padding;
                if (side % 2 == 0) centerWidth = Mathf.Max(centerWidth, group.Bounds.size.x + padding * 2);
                else centerDepth = Mathf.Max(centerDepth, group.Bounds.size.x + padding * 2);
            }
            float width = centerWidth + thickness[1] + thickness[3] + padding * 2;
            float depth = centerDepth + thickness[0] + thickness[2] + padding * 2;
            float aspect = Mathf.Clamp(viewport.width / viewport.height, 0.35f, 3f);
            if (width / depth < aspect) centerWidth += depth * aspect - width;
            else centerDepth += width / aspect - depth;
            width = centerWidth + thickness[1] + thickness[3] + padding * 2;
            depth = centerDepth + thickness[0] + thickness[2] + padding * 2;
            var offsets = new float[4];
            Quaternion view = SourceCoordinates.ViewRotation(viewSeat);
            foreach (string name in LaneOrder)
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
            Entity[] melds = plan.Compact ? frame.Entities.Where(entity => entity.Group == "meld" &&
                entity.Seat == frame.LocalHand.Seat).ToArray() : Array.Empty<Entity>();
            if (melds.Length > 0)
            {
                while (!SeparateMeldHand(plan, frame, ordered, melds, bestRows, bestWidth, availableWidth))
                {
                    if (bestWidth <= 1)
                        throw new ContractException("renderer-failure", "The hand and meld band cannot fit the current viewport.");
                    bestWidth = Mathf.Max(1, bestWidth - 0.5f);
                }
                return;
            }
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

        private static bool SeparateMeldHand(LayoutPlan plan, RendererFrame frame, List<string> hand,
            Entity[] melds, int rows, float width, float available)
        {
            var placements = new Dictionary<string, (int Row, float Center)>(StringComparer.Ordinal);
            var used = new float[rows];
            int row = 0;
            foreach (var meld in melds.Where(entity => !IsAddedKong(entity, melds))
                .OrderBy(MeldIndex).ThenBy(MeldTileIndex).GroupBy(MeldIndex))
            {
                Entity[] tiles = meld.ToArray();
                float total = tiles.Sum(entity => LocalWidth(entity) / 0.06f * width * 0.6f) + (tiles.Length - 1) * 2;
                while (row < rows && used[row] + (used[row] > 0 ? 6 : 0) + total > available + 0.001f) row++;
                if (row == rows) return false;
                float x = used[row] + (used[row] > 0 ? 6 : 0);
                foreach (Entity entity in tiles)
                {
                    float itemWidth = LocalWidth(entity) / 0.06f * width * 0.6f;
                    placements[entity.EntityId] = (row, x + itemWidth / 2);
                    x += itemWidth + 2;
                }
                used[row] = x - 2;
            }
            row = 0;
            foreach (string id in hand)
            {
                float gap;
                while (true)
                {
                    if (row == rows) return false;
                    gap = used[row] > 0 ? 4 : 0;
                    if (id == frame.LocalHand.DrawnEntityId) gap += width * 0.5f;
                    if (used[row] + gap + width <= available + 0.001f) break;
                    row++;
                }
                placements[id] = (row, used[row] + gap + width / 2);
                used[row] += gap + width;
            }
            foreach (Entity upper in melds.Where(entity => IsAddedKong(entity, melds)))
            {
                Entity lower = melds.First(entity => entity.Indexes[0] == upper.Indexes[0] && entity.Indexes[1] == 1);
                placements[upper.EntityId] = placements[lower.EntityId];
            }
            float scale = width * SourceCoordinates.Unit / SourceCoordinates.MeshSize("TILE").x;
            Quaternion undo = Quaternion.Inverse(SourceCoordinates.SeatRotation(frame.LocalHand.Seat.Value));
            foreach (var entry in placements)
            {
                EntityPlacement item = plan.Entities[entry.Key];
                bool exposed = item.Entity.Group == "meld";
                float factor = exposed ? 0.6f : 1;
                Vector3 position = new Vector3((entry.Value.Center - used[entry.Value.Row] / 2) * SourceCoordinates.Unit,
                    exposed ? SourceCoordinates.Position(item.Entity.Pose.Position).y * scale * factor : 0.02f * scale,
                    ((rows - 1) * (width * 1.5f + 6) / 2 - entry.Value.Row * (width * 1.5f + 6)) * SourceCoordinates.Unit);
                Quaternion rotation = exposed ? undo * SourceCoordinates.Rotation(item.Entity.Pose.Rotation) : Quaternion.identity;
                float itemScale = scale * factor * (exposed ? (float)item.Entity.Scale : 1);
                item.Pose = new RenderPose(position, rotation, SourceCoordinates.MeshSize("TILE") * itemScale, itemScale);
                item.Separate = true;
                plan.Slots[item.Entity.SlotName] = new SlotPlacement
                {
                    Slot = plan.Slots[item.Entity.SlotName].Slot, Separate = true, Poses = new[] { item.Pose }
                };
            }
            return true;
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
