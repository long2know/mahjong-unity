// Coordinates guide real browser input only. Assertions use actual C# callbacks,
// native WS outcomes and pixels, never this independent pointing calculation.
const add = (a, b) => a.map((v, i) => v + b[i]);
const sub = (a, b) => a.map((v, i) => v - b[i]);
const mul = (a, n) => a.map(v => v * n);
const dot = (a, b) => a.reduce((sum, v, i) => sum + v * b[i], 0);
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const rotate = (p, q) => add(p, add(mul(cross(q.slice(0, 3), p), 2 * q[3]),
  mul(cross(q.slice(0, 3), cross(q.slice(0, 3), p)), 2)));
const yaw = angle => [0, Math.sin(angle * Math.PI / 360), 0, Math.cos(angle * Math.PI / 360)];
const convert = pose => ({
  p: [(pose.position.x - 87) * 0.01, pose.position.z * 0.01, (pose.position.y - 87) * 0.01],
  q: [-pose.rotation.x, -pose.rotation.z, -pose.rotation.y, pose.rotation.w],
});
const mesh = kind => kind === 'TILE' ? [0.06, 0.04, 0.09] : kind === 'STICK' ? [0.20, 0.01, 0.02] : [0.12, 0.01, 0.06];
const bounds = () => ({ min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] });
const extend = (b, p) => {
  b.min = b.min.map((v, i) => Math.min(v, p[i]));
  b.max = b.max.map((v, i) => Math.max(v, p[i]));
};
const corners = b => [-1, 1].flatMap(x => [-1, 1].flatMap(y => [-1, 1].map(z =>
  [x, y, z].map((sign, i) => sign < 0 ? b.min[i] : b.max[i]))));
const size = b => sub(b.max, b.min);
const center = b => mul(add(b.min, b.max), 0.5);
const translated = (b, p) => ({ min: add(b.min, p), max: add(b.max, p) });
const rotated = (b, q) => {
  const result = bounds();
  corners(b).forEach(p => extend(result, rotate(p, q)));
  return result;
};

function plan(layout, frame, viewport) {
  const inset = viewport.insetsCssPx;
  const band = frame.presentation.handLayout === 'separate' ? viewport.handBandCssPx : 0;
  const safe = { x: inset.left, y: inset.bottom, width: viewport.widthCssPx - inset.left - inset.right,
    height: viewport.heightCssPx - inset.top - inset.bottom };
  const board = { ...safe, y: safe.y + band, height: safe.height - band };
  const hand = { ...safe, height: band };
  const groups = new Map();
  for (const slot of layout.slots) {
    const seat = slot.seat ?? 0, key = `${seat}:${slot.group}`;
    if (!groups.has(key)) groups.set(key, { seat, name: slot.group, bounds: bounds(), translation: [0, 0, 0] });
    const g = groups.get(key), undo = yaw(90 * seat);
    for (const source of slot.poses) {
      const pose = convert(source), half = mul(mesh(slot.kind), 0.5);
      for (const x of [-1, 1]) for (const y of [-1, 1]) for (const z of [-1, 1]) {
        extend(g.bounds, rotate(add(pose.p, rotate([x * half[0], y * half[1], z * half[2]], pose.q)), undo));
      }
    }
  }
  const viewSeat = frame.session.viewSeat ?? 0, view = yaw(90 * viewSeat);
  const rectangular = frame.presentation.boardLayout === 'rectangular';
  const compact = rectangular && layout.gameType === 'CHANGSHA';
  const localIds = new Set(frame.localHand.entityIds);
  if (compact) {
    const occupied = new Set(frame.entities.map(entity => entity.slotName));
    for (const g of groups.values()) {
      g.bounds = bounds();
      g.scale = ['discard', 'meld'].includes(g.name) ? 2 : 1;
      g.offsets = new Map();
      if (!['discard', 'meld', 'wall'].includes(g.name)) continue;
      const slots = layout.slots.filter(slot => (slot.seat ?? 0) === g.seat
        && slot.group === g.name && occupied.has(slot.slotName))
        .sort((a, b) => (a.indexes[0] ?? 0) - (b.indexes[0] ?? 0)
          || (a.indexes[1] ?? 0) - (b.indexes[1] ?? 0));
      const columns = board.width < board.height && (g.seat - viewSeat + 4) % 2 !== 0 ? 12 : 6;
      let x = 0, previousMeld = -1, previousStack = -1, stack = -1;
      slots.forEach((slot, i) => {
        const source = rotate(convert(slot.poses[0]).p, yaw(90 * g.seat));
        const target = [...source];
        if (g.name === 'discard') {
          target[0] = i % columns * 0.064;
          target[2] = -Math.floor(i / columns) * 0.094;
        } else if (g.name === 'meld') {
          const meld = slot.indexes[0] ?? 0;
          if (i && meld !== previousMeld) x += 0.018;
          target[0] = x; target[2] = 0;
          x += 0.064; previousMeld = meld;
        } else {
          const column = slot.indexes[0] ?? i;
          if (column !== previousStack) stack++;
          target[0] = stack * 0.062;
          previousStack = column;
        }
        g.offsets.set(slot.slotName, sub(target, source));
      });
    }
    for (const entity of frame.entities) {
      if (band > 0 && localIds.has(entity.entityId)) continue;
      const g = groups.get(`${entity.seat ?? 0}:${entity.group}`);
      const pose = convert(entity.pose), undo = yaw(90 * g.seat);
      const position = mul(add(rotate(pose.p, undo), g.offsets.get(entity.slotName) ?? [0, 0, 0]), g.scale);
      const half = mul(mesh(entity.kind), (entity.scale ?? 1) * g.scale * 0.5);
      const rotation = pose.q;
      for (const x of [-1, 1]) for (const y of [-1, 1]) for (const z of [-1, 1])
        extend(g.bounds, add(position, rotate(rotate([x * half[0], y * half[1], z * half[2]], rotation), undo)));
    }
    for (const [key, g] of groups) if (!Number.isFinite(g.bounds.min[0])) groups.delete(key);
  }
  let boardBounds;
  if (!rectangular) {
    boardBounds = { min: [-0.87, -0.04, -0.87], max: [0.87, 0.04, 0.87] };
    for (const g of groups.values()) {
      const b = rotated(g.bounds, yaw(90 * (viewSeat - g.seat)));
      extend(boardBounds, b.min); extend(boardBounds, b.max);
    }
    boardBounds.min = sub(boardBounds.min, [0.04, 0, 0.04]);
    boardBounds.max = add(boardBounds.max, [0.04, 0, 0.04]);
  } else {
    const padding = compact ? 0.018 : 0.04;
    const thickness = [0, 0, 0, 0];
    let cw = compact ? 0.12 : 0.35, cd = cw;
    for (const g of groups.values()) {
      const side = (g.seat - viewSeat + 4) % 4, s = size(g.bounds);
      thickness[side] += s[2] + padding;
      if (side % 2 === 0) cw = Math.max(cw, s[0] + padding * 2);
      else cd = Math.max(cd, s[0] + padding * 2);
    }
    let width = cw + thickness[1] + thickness[3] + padding * 2;
    let depth = cd + thickness[0] + thickness[2] + padding * 2;
    const projection = compact && frame.presentation.viewMode !== 'flat'
      ? Math.sin((38 + 40 * frame.presentation.lookDown) * Math.PI / 180) : 1;
    const aspect = Math.max(0.35, Math.min(3, board.width / board.height * projection));
    if (width / depth < aspect) cw += depth * aspect - width;
    else cd += width / aspect - depth;
    width = cw + thickness[1] + thickness[3] + padding * 2;
    depth = cd + thickness[0] + thickness[2] + padding * 2;
    const offsets = [0, 0, 0, 0], packed = [];
    const lanes = compact ? ['discard', 'meld', 'kita', 'hand', 'wall', 'wall.open', 'riichi', 'payment', 'marker', 'tray']
      : ['discard', 'riichi', 'payment', 'wall', 'wall.open', 'hand', 'kita', 'meld', 'marker', 'tray'];
    for (const lane of lanes) {
      for (const g of [...groups.values()].filter(g => g.name === lane).sort((a, b) => a.seat - b.seat)) {
        const side = (g.seat - viewSeat + 4) % 4, s = size(g.bounds), c = center(g.bounds);
        const z = -(side % 2 === 0 ? cd / 2 : cw / 2) - offsets[side] - s[2] / 2;
        g.translation = [-c[0], 0, z - c[2]];
        offsets[side] += s[2] + padding;
        packed.push(rotated(translated(g.bounds, g.translation), yaw(90 * (viewSeat - g.seat))));
      }
    }
    const c = [(thickness[1] - thickness[3]) / 2, 0, (thickness[2] - thickness[0]) / 2];
    boardBounds = { min: sub(c, [width / 2, 0.04, depth / 2]), max: add(c, [width / 2, 0.04, depth / 2]) };
    packed.forEach(b => { extend(boardBounds, b.min); extend(boardBounds, b.max); });
  }
  return { groups, view, rectangular, compact, boardBounds, board, hand };
}

function separateHandPoint(layout, frame, band, entityId, slotName) {
  const ordered = frame.localHand.entityIds.filter(id => id !== frame.localHand.drawnEntityId);
  if (frame.localHand.drawnEntityId) ordered.push(frame.localHand.drawnEntityId);
  const targets = layout.slots.filter(s => s.group === 'hand' && s.seat === frame.localHand.seat)
    .sort((a, b) => (a.indexes[0] ?? Infinity) - (b.indexes[0] ?? Infinity));
  const count = layout.gameType === 'CHANGSHA' ? Math.max(14, ordered.length) : Math.max(ordered.length, targets.length);
  let index = entityId ? ordered.indexOf(entityId) : -1;
  if (index < 0) {
    const occupied = ordered.map(id => frame.entities.find(e => e.entityId === id).slotName);
    const empties = targets.filter(s => !occupied.includes(s.slotName));
    index = ordered.length + empties.findIndex(s => s.slotName === slotName);
  }
  let rows = 1, columns = count, width = 0;
  for (let r = 1; r <= count; r++) {
    const c = Math.ceil(count / r);
    const w = Math.min(44, (Math.max(1, band.width - 16) - (c - 1) * 4) / (c + 0.5),
      (Math.max(1, band.height - 12) - (r - 1) * 6) / (r * 1.5));
    if (w > width) { width = w; rows = r; columns = c; }
    if (w >= 32) break;
  }
  width = Math.max(1, width);
  const row = Math.floor(index / columns), col = index % columns;
  const rowCount = Math.min(columns, count - row * columns);
  const rowWidth = rowCount * width + (rowCount - 1) * 4 + width * 0.5;
  const drawn = entityId === frame.localHand.drawnEntityId;
  const x = -rowWidth / 2 + width / 2 + col * (width + 4) + (drawn ? width * 0.5 : 0);
  const z = (rows - 1) * (width * 1.5 + 6) / 2 - row * (width * 1.5 + 6);
  return { x: band.x + band.width / 2 + x, bottomY: band.y + band.height / 2 + z };
}

export function aimPoint(state, target) {
  const { layout, frame, viewport } = state;
  const p = plan(layout, frame, viewport);
  const entityId = target.entityId;
  if (p.hand.height > 0 && ((entityId && frame.localHand.entityIds.includes(entityId))
      || (!entityId && target.group === 'hand' && target.seat === frame.localHand.seat))) {
    const point = separateHandPoint(layout, frame, p.hand, entityId, target.slotName);
    return { x: point.x, y: viewport.heightCssPx - point.bottomY };
  }
  const pose = convert(target.pose ?? target.poses[0]);
  const group = p.groups.get(`${target.seat ?? 0}:${target.group}`);
  let position = pose.p;
  if (p.compact) position = rotate(add(
    mul(add(rotate(position, yaw(90 * group.seat)), group.offsets.get(target.slotName) ?? [0, 0, 0]), group.scale),
    group.translation), yaw(-90 * group.seat));
  else if (p.rectangular) position = add(position, rotate(group.translation, yaw(-90 * group.seat)));
  position = rotate(position, p.view);
  const flat = frame.presentation.viewMode === 'flat';
  const elevation = (flat ? 90 : 38 + 40 * frame.presentation.lookDown) * Math.PI / 180;
  const forward = [0, -Math.sin(elevation), Math.cos(elevation)], up = [0, Math.cos(elevation), Math.sin(elevation)];
  const half = mul(size(p.boardBounds), 0.5);
  let maxX = 0, maxY = 0, distance = 0;
  const aspect = p.board.width / p.board.height;
  const margin = p.compact ? 1.10 + (1.02 - 1.10) * (frame.presentation.zoom + 1) / 2
    : 1.28 + (1.04 - 1.28) * (frame.presentation.zoom + 1) / 2;
  for (const x of [-1, 1]) for (const y of [-1, 1]) for (const z of [-1, 1]) {
    const c = [x * half[0], y * half[1], z * half[2]];
    maxX = Math.max(maxX, Math.abs(c[0]));
    maxY = Math.max(maxY, Math.abs(dot(c, up)));
    const fit = Math.max(Math.abs(dot(c, up)), Math.abs(c[0]) / Math.max(0.01, aspect));
    distance = Math.max(distance, fit * margin / Math.tan(20 * Math.PI / 180) - dot(c, forward));
  }
  const ortho = Math.max(maxY, maxX / Math.max(0.01, aspect)) * margin;
  distance = flat ? Math.hypot(...size(p.boardBounds)) + 1 : distance + 0.1;
  const delta = sub(position, center(p.boardBounds));
  const denominator = flat ? ortho : (distance + dot(delta, forward)) * Math.tan(20 * Math.PI / 180);
  const x = delta[0] / denominator / aspect, y = dot(delta, up) / denominator;
  return {
    x: p.board.x + (x + 1) * p.board.width / 2,
    y: viewport.heightCssPx - (p.board.y + (y + 1) * p.board.height / 2),
  };
}
