import { Box3, Matrix4, Quaternion, Vector3 } from 'three';
import type { Entity, Frame, Layout, Pose, Viewport } from './renderer-contract';
import { TILE_DEPTH, TILE_HEIGHT, TILE_WIDTH } from './gpu/tile-mesh';
import { perspective4 } from './gpu/math';
import { orthographic4 } from './gpu/camera';

export interface Rect { x: number; y: number; width: number; height: number }
export interface RenderPose { position: Vector3; rotation: Quaternion; scale: number }
export interface Placement { entity: Entity; pose: RenderPose; separate: boolean }
export interface SlotPlacement { slot: Layout['slots'][number]; poses: RenderPose[]; separate: boolean }
export interface WebglPlan {
  board: Rect; hand: Rect; bounds: Box3; compact: boolean;
  entities: Map<string, Placement>; slots: Map<string, SlotPlacement>; groups: Map<string, Box3>;
  rivers: Map<number, Box3>; centerSize: number;
}
interface Lane {
  seat: number; name: string; bounds: Box3; translation: Vector3; targets: Map<string, [number, number]>;
}
const unit = 0.01;
const yAxis = new Vector3(0, 1, 0);
const key = (seat: number | null, group: string): string => `${seat ?? 0}:${group}`;
const seatRotation = (seat: number): Quaternion => new Quaternion().setFromAxisAngle(yAxis, -seat * Math.PI / 2);
const meshSize = (kind: string): Vector3 => kind === 'TILE' ? new Vector3(.06, .04, .09)
  : kind === 'STICK' ? new Vector3(.20, .01, .02) : new Vector3(.12, .01, .06);
const center = (bounds: Box3): Vector3 => bounds.getCenter(new Vector3());
const size = (bounds: Box3): Vector3 => bounds.getSize(new Vector3());
const boundsFrom = (position: Vector3, dimensions: Vector3): Box3 =>
  new Box3().setFromCenterAndSize(position, dimensions);
const corners = (bounds: Box3): Vector3[] => {
  const result: Vector3[] = [];
  for (const x of [bounds.min.x, bounds.max.x]) for (const y of [bounds.min.y, bounds.max.y])
    for (const z of [bounds.min.z, bounds.max.z]) result.push(new Vector3(x, y, z));
  return result;
};
const rotateBounds = (bounds: Box3, rotation: Quaternion): Box3 =>
  new Box3().setFromPoints(corners(bounds).map(point => point.applyQuaternion(rotation)));

export function sourcePose(pose: Pose, scale = 1): RenderPose {
  const { x, y, z, w } = pose.rotation;
  const norm = Math.hypot(x, y, z, w);
  if (!Number.isFinite(norm) || Math.abs(norm - 1) > .002) throw Error('Invalid pose quaternion.');
  return {
    position: new Vector3((pose.position.x - 87) * unit, pose.position.z * unit, (pose.position.y - 87) * unit),
    rotation: new Quaternion(-x / norm, -z / norm, -y / norm, w / norm), scale,
  };
}
const poseBounds = (pose: RenderPose, kind: string): Box3 => {
  const half = meshSize(kind).multiplyScalar(pose.scale / 2);
  return new Box3().setFromPoints(corners(new Box3(half.clone().negate(), half))
    .map(point => point.applyQuaternion(pose.rotation).add(pose.position)));
};

export function viewportRects(viewport: Viewport): { board: Rect; hand: Rect } {
  const inset = viewport.insetsCssPx;
  const width = Math.max(1, viewport.widthCssPx - inset.left - inset.right);
  const height = Math.max(1, viewport.heightCssPx - inset.top - inset.bottom);
  const band = Math.min(viewport.handBandCssPx, Math.max(0, height - 1));
  return {
    board: { x: inset.left, y: inset.top, width, height: height - band },
    hand: { x: inset.left, y: inset.top + height - band, width, height: band },
  };
}

export function buildWebglPlan(layout: Layout, frame: Frame, viewport: Viewport): WebglPlan {
  const rects = viewportRects(viewport);
  const separate = frame.presentation.handLayout === 'separate' && rects.hand.height > 0;
  const rectangular = frame.presentation.boardLayout === 'rectangular';
  const compact = rectangular && layout.gameType === 'CHANGSHA';
  const plan: WebglPlan = {
    ...rects, bounds: boundsFrom(new Vector3(), new Vector3(1.74, .08, 1.74)), compact,
    entities: new Map(), slots: new Map(), groups: new Map(), rivers: new Map(), centerSize: .338,
  };
  const view = new Quaternion().setFromAxisAngle(yAxis, (frame.session.viewSeat ?? 0) * Math.PI / 2);
  const lanes = new Map<string, Lane>();
  const own = new Set(frame.localHand.entityIds);
  for (const slot of layout.slots) {
    const id = key(slot.seat, slot.group);
    if (!lanes.has(id)) lanes.set(id, {
      seat: slot.seat ?? 0, name: slot.group, bounds: new Box3(), translation: new Vector3(), targets: new Map(),
    });
    if (!compact) {
      const lane = lanes.get(id)!;
      const undo = seatRotation(lane.seat).invert();
      for (const source of slot.poses) {
        const pose = sourcePose(source);
        pose.position.applyQuaternion(undo);
        pose.rotation.premultiply(undo);
        lane.bounds.union(poseBounds(pose, slot.kind));
      }
    }
  }
  const normalize = (pose: RenderPose, lane: Lane, name: string): RenderPose => {
    const undo = seatRotation(lane.seat).invert();
    const position = pose.position.clone().applyQuaternion(undo);
    const target = lane.targets.get(name);
    if (target) { position.x = target[0]; position.z = target[1]; }
    return { position, rotation: pose.rotation.clone().premultiply(undo), scale: pose.scale };
  };
  const addedKong = (entity: Entity): boolean => entity.group === 'meld' && entity.indexes[1] === 4
    && frame.entities.some(lower => lower.group === 'meld' && lower.seat === entity.seat
      && lower.indexes[0] === entity.indexes[0] && lower.indexes[1] === 1);
  const localWidth = (entity: Entity): number => {
    const pose = sourcePose(entity.pose, entity.scale);
    pose.position.set(0, 0, 0);
    pose.rotation.premultiply(seatRotation(entity.seat ?? 0).invert());
    return size(poseBounds(pose, entity.kind)).x;
  };
  const gap = (previous: Entity | undefined, entity: Entity): number => !previous ? 0
    : entity.entityId === frame.localHand.drawnEntityId ? .03
      : previous.group !== entity.group || entity.group === 'meld' && previous.indexes[0] !== entity.indexes[0] ? .012 : .002;
  if (compact) {
    let inner = Infinity, outer = 0;
    for (const slot of layout.slots.filter(slot => slot.group === 'wall')) {
      const lane = lanes.get(key(slot.seat, slot.group))!;
      for (const source of slot.poses) {
        const bounds = poseBounds(normalize(sourcePose(source), lane, slot.slotName), slot.kind);
        inner = Math.min(inner, -bounds.max.z);
        outer = Math.max(outer, Math.abs(bounds.min.x), Math.abs(bounds.max.x), Math.abs(bounds.min.z), Math.abs(bounds.max.z));
        lane.bounds.union(bounds);
      }
    }
    for (const lane of lanes.values()) {
      if (lane.name !== 'discard' || !Number.isFinite(inner)) continue;
      const grid = layout.slots.filter(slot => slot.group === 'discard' && slot.seat === lane.seat && slot.indexes.length === 2);
      if (!grid.length) continue;
      const rows = Math.max(...grid.map(slot => slot.indexes[0])) + 1;
      const columns = Math.max(...grid.map(slot => slot.indexes[1])) + 1;
      const width = columns * .06 + (columns - 1) * .002;
      const depth = rows * .09 + (rows - 1) * .002;
      const hole = inner - rows * .09 - (rows - 1) * .002 - .018;
      for (const slot of grid) lane.targets.set(slot.slotName, [
        hole - .006 - width + .03 + slot.indexes[1] * .062,
        -hole - .006 - .045 - slot.indexes[0] * .092,
      ]);
      const river = boundsFrom(new Vector3(hole - .006 - width / 2, -.004, -hole - .006 - depth / 2),
        new Vector3(width + .006, 0, depth + .006));
      plan.rivers.set(lane.seat, rotateBounds(river, view.clone().multiply(seatRotation(lane.seat))));
      plan.centerSize = Math.min(plan.centerSize, 2 * (hole + .006) - .012);
    }
    let radius = outer + .063;
    const rows = new Map<number, Entity[]>();
    for (const seat of layout.activeSeats) {
      if (separate && seat === frame.localHand.seat) continue;
      const melds = frame.entities.filter(entity => entity.seat === seat && entity.group === 'meld' && !addedKong(entity))
        .sort((a, b) => (a.indexes[0] ?? 0) - (b.indexes[0] ?? 0) || (a.indexes[1] ?? 0) - (b.indexes[1] ?? 0));
      const hand = frame.entities.filter(entity => entity.seat === seat && entity.group === 'hand')
        .sort((a, b) => Number(a.entityId === frame.localHand.drawnEntityId) - Number(b.entityId === frame.localHand.drawnEntityId)
          || sourcePose(a.pose).position.applyQuaternion(seatRotation(seat).invert()).x
          - sourcePose(b.pose).position.applyQuaternion(seatRotation(seat).invert()).x);
      const row = [...melds, ...hand];
      rows.set(seat, row);
      radius = Math.max(radius, (row.reduce((sum, entity, i) => sum + gap(row[i - 1], entity) + localWidth(entity), 0) + .114) / 2);
    }
    for (const [seat, row] of rows) {
      let x = -row.reduce((sum, entity, i) => sum + gap(row[i - 1], entity) + localWidth(entity), 0) / 2;
      row.forEach((entity, index) => {
        x += gap(row[index - 1], entity);
        const width = localWidth(entity);
        lanes.get(key(seat, entity.group))!.targets.set(entity.slotName, [x + width / 2, -radius]);
        x += width;
      });
      for (const entity of frame.entities.filter(entity => entity.seat === seat && addedKong(entity))) {
        const lower = frame.entities.find(lower => lower.seat === seat && lower.group === 'meld'
          && lower.indexes[0] === entity.indexes[0] && lower.indexes[1] === 1)!;
        const lane = lanes.get(key(seat, 'meld'))!;
        lane.targets.set(entity.slotName, lane.targets.get(lower.slotName)!);
      }
    }
    plan.bounds = boundsFrom(new Vector3(), new Vector3((radius + .057) * 2, .16, (radius + .057) * 2));
  } else if (rectangular) {
    const thickness = [0, 0, 0, 0];
    let middleWidth = .35, middleDepth = .35;
    const relative = (seat: number): number => (seat - (frame.session.viewSeat ?? 0) + 4) % 4;
    for (const lane of lanes.values()) if (!lane.bounds.isEmpty()) {
      const side = relative(lane.seat), dimensions = size(lane.bounds);
      thickness[side] += dimensions.z + .04;
      if (side % 2 === 0) middleWidth = Math.max(middleWidth, dimensions.x + .08);
      else middleDepth = Math.max(middleDepth, dimensions.x + .08);
    }
    const width = middleWidth + thickness[1] + thickness[3] + .08;
    const depth = middleDepth + thickness[0] + thickness[2] + .08;
    const aspect = Math.max(.35, Math.min(3, plan.board.width / plan.board.height));
    if (width / depth < aspect) middleWidth += depth * aspect - width;
    else middleDepth += width / aspect - depth;
    const offsets = [0, 0, 0, 0];
    for (const name of ['discard', 'riichi', 'payment', 'wall', 'wall.open', 'hand', 'kita', 'meld', 'marker', 'tray']) {
      for (const lane of [...lanes.values()].filter(lane => lane.name === name && !lane.bounds.isEmpty()).sort((a, b) => a.seat - b.seat)) {
        const side = relative(lane.seat), dimensions = size(lane.bounds), midpoint = center(lane.bounds);
        const z = -(side % 2 === 0 ? middleDepth : middleWidth) / 2 - offsets[side] - dimensions.z / 2;
        lane.translation.set(-midpoint.x, 0, z - midpoint.z);
        offsets[side] += dimensions.z + .04;
      }
    }
    plan.bounds = boundsFrom(new Vector3((thickness[1] - thickness[3]) / 2, 0, (thickness[2] - thickness[0]) / 2),
      new Vector3(middleWidth + thickness[1] + thickness[3] + .08, .08, middleDepth + thickness[0] + thickness[2] + .08));
  }
  const map = (source: Pose, scale: number, lane: Lane, name: string): RenderPose => {
    const pose = sourcePose(source, scale);
    if (compact) {
      const local = normalize(pose, lane, name);
      const rotation = view.clone().multiply(seatRotation(lane.seat));
      return { position: local.position.add(lane.translation).applyQuaternion(rotation), rotation: rotation.multiply(local.rotation), scale };
    }
    if (rectangular) pose.position.add(lane.translation.clone().applyQuaternion(seatRotation(lane.seat)));
    pose.position.applyQuaternion(view);
    pose.rotation.premultiply(view);
    return pose;
  };
  for (const slot of layout.slots) {
    const lane = lanes.get(key(slot.seat, slot.group))!;
    plan.slots.set(slot.slotName, { slot, poses: slot.poses.map(pose => map(pose, 1, lane, slot.slotName)), separate: false });
  }
  for (const entity of frame.entities) {
    const lane = lanes.get(key(entity.seat, entity.group));
    if (!lane) throw Error('Entity has no current layout lane.');
    const placement = { entity, pose: map(entity.pose, entity.scale, lane, entity.slotName),
      separate: separate && (own.has(entity.entityId) || compact && entity.group === 'meld' && entity.seat === frame.localHand.seat) };
    plan.entities.set(entity.entityId, placement);
    if (!placement.separate) plan.bounds.union(poseBounds(placement.pose, entity.kind));
  }
  for (const [id, lane] of lanes) if (!lane.bounds.isEmpty()) {
    const bounds = lane.bounds.clone().translate(lane.translation);
    plan.groups.set(id, rotateBounds(bounds, view.clone().multiply(seatRotation(lane.seat))));
  }
  if (separate && frame.localHand.seat !== null) {
    separateHand(plan, layout, frame, localWidth, addedKong);
  }
  plan.rivers = discardRegions(plan, frame.session.viewSeat);
  return plan;
}

export function discardRegions(plan: WebglPlan, viewSeat: number | null): Map<number, Box3> {
  const regions = new Map<number, Box3>();
  const limitX = Math.min(Math.abs(plan.bounds.min.x), plan.bounds.max.x) - .02;
  const limitZ = Math.min(Math.abs(plan.bounds.min.z), plan.bounds.max.z) - .02;
  const gap = Math.min(.09, limitX * .25, limitZ * .25);
  for (let seat = 0; seat < 4; seat++) {
    let bounds = plan.rivers.get(seat)?.clone();
    const side = (seat - (viewSeat ?? 0) + 4) % 4;
    if (!bounds) {
      bounds = new Box3();
      for (const placement of plan.slots.values()) {
        if (placement.slot.group !== 'discard' || placement.slot.seat !== seat || placement.slot.indexes.length !== 2) continue;
        const pose = placement.poses[placement.slot.shadowRotationIndex] ?? placement.poses[0];
        if (pose) bounds.union(poseBounds(pose, placement.slot.kind));
      }
      if (bounds.isEmpty()) {
        const rotation = new Quaternion().setFromAxisAngle(yAxis, -side * Math.PI / 2);
        const fallback = boundsFrom(new Vector3(-gap * .5, -.004, -(gap + Math.min(limitZ, .46)) / 2),
          new Vector3(Math.min(limitX, .36), 0, Math.max(.04, Math.min(limitZ, .46) - gap)));
        bounds = rotateBounds(fallback, rotation);
      } else {
        bounds.min.x -= .003; bounds.max.x += .003;
        bounds.min.z -= .003; bounds.max.z += .003;
      }
    }
    bounds.min.x = Math.max(bounds.min.x, plan.bounds.min.x + .01);
    bounds.max.x = Math.min(bounds.max.x, plan.bounds.max.x - .01);
    bounds.min.z = Math.max(bounds.min.z, plan.bounds.min.z + .01);
    bounds.max.z = Math.min(bounds.max.z, plan.bounds.max.z - .01);
    // Clip only the decoration around the dice, never a source slot/entity pose.
    if (side === 0) bounds.max.z = Math.min(bounds.max.z, -gap);
    if (side === 1) bounds.min.x = Math.max(bounds.min.x, gap);
    if (side === 2) bounds.min.z = Math.max(bounds.min.z, gap);
    if (side === 3) bounds.max.x = Math.min(bounds.max.x, -gap);
    bounds.min.y = bounds.max.y = -.004;
    regions.set(seat, bounds);
  }
  for (let seat = 0; seat < 4; seat++) {
    const current = regions.get(seat)!, next = regions.get((seat + 1) % 4)!;
    const overlap = Math.min(current.max.x, next.max.x) - Math.max(current.min.x, next.min.x) > .00001
      && Math.min(current.max.z, next.max.z) - Math.max(current.min.z, next.min.z) > .00001;
    if (!overlap) continue;
    const side = (seat - (viewSeat ?? 0) + 4) % 4;
    if (side === 0) current.max.x = Math.min(current.max.x, next.min.x);
    if (side === 1) current.max.z = Math.min(current.max.z, next.min.z);
    if (side === 2) current.min.x = Math.max(current.min.x, next.max.x);
    if (side === 3) current.min.z = Math.max(current.min.z, next.max.z);
  }
  return regions;
}

function separateHand(plan: WebglPlan, layout: Layout, frame: Frame, localWidth: (entity: Entity) => number,
  addedKong: (entity: Entity) => boolean): void {
  const ordered = frame.localHand.entityIds.filter(id => id !== frame.localHand.drawnEntityId);
  if (frame.localHand.drawnEntityId) ordered.push(frame.localHand.drawnEntityId);
  const targets = layout.slots.filter(slot => slot.group === 'hand' && slot.seat === frame.localHand.seat)
    .sort((a, b) => (a.indexes[0] ?? Infinity) - (b.indexes[0] ?? Infinity));
  const count = layout.gameType === 'CHANGSHA' ? Math.max(14, ordered.length) : Math.max(ordered.length, targets.length);
  if (!count) return;
  const available = Math.max(1, plan.hand.width - 16), height = Math.max(1, plan.hand.height - 12);
  let rows = 1, columns = count, width = 0;
  for (let n = 1; n <= count; n++) {
    const cols = Math.ceil(count / n);
    const fit = Math.min(44, (available - (cols - 1) * 4) / (cols + .5), (height - (n - 1) * 6) / (n * 1.5));
    if (fit > width) { width = fit; rows = n; columns = cols; }
    if (fit >= 32) break;
  }
  width = Math.max(1, width);
  const occupied = new Set<string>();
  const place = (id: string, pose: RenderPose): void => {
    const item = plan.entities.get(id);
    if (!item) throw Error('Local hand contains an absent entity.');
    item.pose = pose; item.separate = true;
    occupied.add(item.entity.slotName);
    const slot = plan.slots.get(item.entity.slotName)!;
    slot.poses = [pose]; slot.separate = true;
  };
  const melds = plan.compact ? frame.entities.filter(entity => entity.group === 'meld' && entity.seat === frame.localHand.seat) : [];
  if (melds.length) {
    for (; width >= 1; width -= .5) {
      const used = Array<number>(rows).fill(0);
      const positions = new Map<string, { row: number; x: number; factor: number }>();
      let row = 0;
      const exposed = melds.filter(entity => !addedKong(entity))
        .sort((a, b) => (a.indexes[0] ?? 0) - (b.indexes[0] ?? 0) || (a.indexes[1] ?? 0) - (b.indexes[1] ?? 0));
      for (const entity of [...exposed, ...ordered.map(id => plan.entities.get(id)!.entity)]) {
        const factor = entity.group === 'meld' ? .6 : 1;
        const itemWidth = entity.group === 'meld' ? localWidth(entity) / .06 * width * factor : width;
        const gap = entity.entityId === frame.localHand.drawnEntityId ? width * .5 + 4 : 4;
        while (row < rows && used[row] + (used[row] ? gap : 0) + itemWidth > available) row++;
        if (row === rows) break;
        if (used[row]) used[row] += gap;
        positions.set(entity.entityId, { row, x: used[row] + itemWidth / 2, factor });
        used[row] += itemWidth;
      }
      if (row === rows) continue;
      const scale = width * unit / .06;
      for (const upper of melds.filter(addedKong)) {
        const lower = melds.find(entity => entity.indexes[0] === upper.indexes[0] && entity.indexes[1] === 1)!;
        positions.set(upper.entityId, positions.get(lower.entityId)!);
      }
      for (const [id, at] of positions) {
        const entity = plan.entities.get(id)!.entity;
        place(id, {
          position: new Vector3((at.x - used[at.row] / 2) * unit,
            entity.group === 'meld' ? sourcePose(entity.pose).position.y * scale * at.factor : .02 * scale,
            ((rows - 1) / 2 - at.row) * (width * 1.5 + 6) * unit),
          rotation: entity.group === 'meld' ? sourcePose(entity.pose).rotation.premultiply(seatRotation(entity.seat!).invert()) : new Quaternion(),
          scale: scale * at.factor * (entity.group === 'meld' ? entity.scale : 1),
        });
      }
      return;
    }
    throw Error('Hand and meld band cannot fit.');
  }
  const handPose = (index: number, drawn: boolean): RenderPose => {
    const row = Math.floor(index / columns), column = index % columns;
    const rowCount = Math.max(1, Math.min(columns, count - row * columns));
    const rowWidth = rowCount * width + (rowCount - 1) * 4 + width * .5;
    return {
      position: new Vector3((-rowWidth / 2 + width / 2 + column * (width + 4) + (drawn ? width * .5 : 0)) * unit,
        .02 * width * unit / .06, ((rows - 1) / 2 - row) * (width * 1.5 + 6) * unit),
      rotation: new Quaternion(), scale: width * unit / .06,
    };
  };
  ordered.forEach((id, index) => place(id, handPose(index, id === frame.localHand.drawnEntityId)));
  let empty = ordered.length;
  for (const slot of targets) if (!occupied.has(slot.slotName)) {
    const target = plan.slots.get(slot.slotName)!;
    target.poses = [handPose(empty++, false)]; target.separate = true;
  }
}

// Layout stays in the retained source basis; reflect Z only at the GL boundary.
const reflect = new Matrix4().makeScale(1, 1, -1);
export function tileModel(pose: RenderPose, kind = 'TILE'): Float32Array {
  const dimensions = meshSize(kind);
  const basis = new Matrix4().set(
    dimensions.x / TILE_WIDTH, 0, 0, 0,
    0, 0, dimensions.y / TILE_DEPTH, 0,
    0, dimensions.z / TILE_HEIGHT, 0, 0,
    0, 0, 0, 1,
  );
  const model = new Matrix4().compose(pose.position, pose.rotation, new Vector3(pose.scale, pose.scale, pose.scale));
  return Float32Array.from(reflect.clone().multiply(model).multiply(basis).elements);
}

export function fittedCamera(plan: WebglPlan, frame: Frame, hand = false, pan: [number, number] = [0, 0]): Float32Array {
  const rect = hand ? plan.hand : plan.board, aspect = rect.width / Math.max(1, rect.height);
  if (hand) {
    const eye = new Vector3(0, 10, 0);
    const view = new Matrix4().makeBasis(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)).setPosition(eye).invert();
    return Float32Array.from(new Matrix4().fromArray(orthographic4(rect.height * unit / 2, aspect, .01, 30)).multiply(view).elements);
  }
  const presentation = frame.presentation, camera = presentation.camera;
  const flat = presentation.viewMode === 'flat';
  const pitch = flat ? 90 : camera?.pitch ?? (plan.compact ? 78 + 10 * presentation.lookDown : 38 + 40 * presentation.lookDown);
  const rotation = new Quaternion().setFromAxisAngle(yAxis, (camera?.yaw ?? 0) * Math.PI / 180)
    .multiply(new Quaternion().setFromAxisAngle(new Vector3(1, 0, 0), pitch * Math.PI / 180))
    .multiply(new Quaternion().setFromAxisAngle(new Vector3(0, 0, 1), (camera?.roll ?? 0) * Math.PI / 180));
  const forward = new Vector3(0, 0, 1).applyQuaternion(rotation);
  const right = new Vector3(1, 0, 0).applyQuaternion(rotation);
  const up = new Vector3(0, 1, 0).applyQuaternion(rotation);
  const midpoint = center(plan.bounds);
  const tangent = Math.tan(40 * Math.PI / 360);
  const margin = plan.compact ? 1.06 - .04 * presentation.zoom : 1.16 - .12 * presentation.zoom;
  let maxRight = 0, maxUp = 0, distance = 0, nearest = 0;
  for (const point of corners(plan.bounds).map(point => point.sub(midpoint))) {
    maxRight = Math.max(maxRight, Math.abs(point.dot(right)));
    maxUp = Math.max(maxUp, Math.abs(point.dot(up)));
    nearest = Math.max(nearest, -point.dot(forward) + .1);
    distance = Math.max(distance, Math.max(Math.abs(point.dot(up)), Math.abs(point.dot(right)) / aspect) * margin / tangent - point.dot(forward));
  }
  const scale = camera?.scale ?? 1;
  const orthoSize = Math.max(maxUp, maxRight / aspect) * margin / scale;
  distance = flat ? size(plan.bounds).length() + 1 : Math.max(nearest, (distance + .1) / scale);
  midpoint.addScaledVector(right, pan[0]).addScaledVector(up, pan[1]);
  const eye = midpoint.clone().addScaledVector(forward, -distance);
  for (const vector of [eye, right, up, forward]) vector.z *= -1;
  const view = new Matrix4().makeBasis(right, up, forward.negate()).setPosition(eye).invert();
  const projection = flat ? orthographic4(orthoSize, aspect, .01, 100) : perspective4(40 * Math.PI / 180, aspect, .01, 100);
  return Float32Array.from(new Matrix4().fromArray(projection).multiply(view).elements);
}
