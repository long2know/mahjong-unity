import { Matrix4, Vector3 } from 'three';
import { compileProgram } from '../renderer-webgl2';
import {
  createTileMesh, disposeTileMesh, drawTileMesh, setTileInstance, uploadTileInstances,
  TILE_INSTANCE_VS, type TileMesh,
} from '../renderer-webgl2/tile-mesh';
import { invertMatrix4, pickTile, type PickRay } from '../renderer-webgl2/picking';
import type { Frame, Layout, Viewport } from './renderer-contract';
import { buildWebglPlan, fittedCamera, tileModel, type Rect, type RenderPose, type WebglPlan } from './webgl-layout';

const fragment = `#version 300 es
precision highp float;
in vec2 v_uv;
in vec3 v_normal;
flat in float v_tileId;
uniform sampler2D u_atlas;
uniform vec2 u_atlasGrid;
uniform vec3 u_lightDir;
out vec4 fragColor;
void main() {
  float row = mod(floor(v_tileId), 64.0);
  float side = clamp(floor(v_uv.x - 0.00001), 0.0, 2.0);
  vec2 uv = vec2(clamp(fract(v_uv.x), 0.015, 0.985), clamp(1.0-v_uv.y, 0.015, 0.985));
  vec2 cell = vec2(mod(row, 8.0)*3.0+side, floor(row/8.0));
  vec4 base = texture(u_atlas, (cell+uv)/u_atlasGrid);
  float light = 0.72+0.28*max(0.0, dot(normalize(v_normal), normalize(u_lightDir)));
  if (row >= 40.0 && row <= 44.0) light = 1.0;
  fragColor = vec4(base.rgb*light, 1.0);
}`;
interface Target { kind: 'entity' | 'dice'; id: string | null }
interface Batch { mesh: TileMesh; targets: Array<Target | null> }
interface SlotBatch { modelData: Float32Array; instanceCount: number; names: string[] }
interface PickBatch { modelData: Float32Array; instanceCount: number; targets: Array<Target | null> }
export interface SceneCommit { frame: Frame; plan: WebglPlan; viewport: Viewport; milliseconds: number }
const capacity = 4096;
const emptySlots = (): SlotBatch => ({ modelData: new Float32Array(capacity * 16), instanceCount: 0, names: [] });
const solidPose = (x: number, y: number, z: number, sx: number, sy: number, sz: number): Float32Array => {
  const matrix = new Matrix4().makeScale(sx, sz / (20 / 12), sy / (8 / 12));
  matrix.premultiply(new Matrix4().set(
    1, 0, 0, x, 0, 0, 1, y, 0, -1, 0, -z, 0, 0, 0, 1,
  ));
  return Float32Array.from(matrix.elements);
};

function pickRay(rect: Rect, x: number, y: number, matrix: Float32Array): PickRay {
  const inverse = invertMatrix4(matrix);
  if (!inverse) throw Error('Camera matrix is singular.');
  const transform = (z: number): [number, number, number] => {
    const point = new Vector3((x - rect.x) / rect.width * 2 - 1, 1 - (y - rect.y) / rect.height * 2, z)
      .applyMatrix4(new Matrix4().fromArray(inverse));
    return [point.x, point.y, point.z];
  };
  const origin = transform(-1), end = transform(1);
  const direction = new Vector3(...end).sub(new Vector3(...origin)).normalize();
  return { origin, direction: [direction.x, direction.y, direction.z] };
}
const contains = (rect: Rect, x: number, y: number): boolean =>
  rect.height > 0 && x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height;

export class WebglScene {
  readonly device: { version: string; renderer: string; contextAttributes: WebGLContextAttributes | null };
  readonly gl: WebGL2RenderingContext;
  private readonly program: WebGLProgram;
  private readonly texture: WebGLTexture;
  private readonly table: Batch;
  private readonly hand: Batch;
  private readonly tableSlots = emptySlots();
  private readonly handSlots = emptySlots();
  private readonly labels: HTMLElement;
  private readonly labelNodes = new Map<number, HTMLElement>();
  private matrices: [Float32Array, Float32Array] = [new Float32Array(16), new Float32Array(16)];
  private picking: { table: PickBatch; hand: PickBatch; tableSlots: SlotBatch; handSlots: SlotBatch;
    matrices: [Float32Array, Float32Array] } | null = null;
  private pending: { sync: WebGLSync; commit: SceneCommit } | null = null;
  committed: SceneCommit | null = null;
  pan: [number, number] = [0, 0];
  drawCount = 0;
  private disposed = false;

  static async create(canvas: HTMLCanvasElement): Promise<WebglScene> {
    const urls = [
      new URL('../../../../../unity/MahjongRenderer/Assets/Resources/Art/Faces.png', import.meta.url).href,
      new URL('../../../../../unity/MahjongRenderer/Assets/Resources/Art/FacesLabelled.png', import.meta.url).href,
    ];
    const images = await Promise.all(urls.map(url => new Promise<HTMLImageElement>((resolve, reject) => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = () => reject(Error('Required owned tile artwork could not load; no placeholder atlas was substituted.'));
      image.src = url;
    })));
    try { return new WebglScene(canvas, images); }
    catch (error) {
      canvas.getContext('webgl2')?.getExtension('WEBGL_lose_context')?.loseContext();
      throw error;
    }
  }

  private readonly atlases: [WebGLTexture, WebGLTexture];
  private constructor(private readonly canvas: HTMLCanvasElement, images: HTMLImageElement[]) {
    const gl = canvas.getContext('webgl2', { alpha: false, antialias: true, depth: true, preserveDrawingBuffer: false });
    if (!gl) throw Error('WebGL2 is unavailable. No alternate renderer was started.');
    this.gl = gl;
    if (gl.getParameter(gl.MAX_TEXTURE_SIZE) < 1536) throw Error('WebGL texture limits cannot hold the tile atlas.');
    this.device = { version: gl.getParameter(gl.VERSION), renderer: gl.getParameter(gl.RENDERER), contextAttributes: gl.getContextAttributes() };
    this.program = compileProgram(gl, TILE_INSTANCE_VS, fragment);
    this.atlases = [this.atlas(images[0]), this.atlas(images[1])];
    this.texture = this.atlases[0];
    this.table = { mesh: createTileMesh(gl, this.program, capacity), targets: [] };
    this.hand = { mesh: createTileMesh(gl, this.program, capacity), targets: [] };
    this.labels = document.createElement('div');
    this.labels.className = 'webgl-labels';
    this.labels.setAttribute('aria-label', 'Public seat scores');
    canvas.parentElement!.append(this.labels);
    this.check();
  }

  private atlas(image: HTMLImageElement): WebGLTexture {
    const canvas = document.createElement('canvas');
    canvas.width = 1536; canvas.height = 768;
    const context = canvas.getContext('2d');
    if (!context) throw Error('Tile texture composition is unavailable.');
    const colors: Record<number, string> = {
      37: '#216c76', 38: '#ad7639', 39: '#f7f3e6', 40: '#0d5748', 41: '#573c2b',
      42: '#ffd36e', 43: '#60d9da', 44: '#a43e44', 58: '#205d53', 59: '#c4aa57', 60: '#101f2a',
    };
    for (let row = 0; row < 64; row++) for (let side = 0; side < 3; side++) {
      const x = ((row % 8) * 3 + side) * 64, y = Math.floor(row / 8) * 96;
      context.fillStyle = row < 37 ? side === 1 ? '#216c76' : '#f7f3e6' : colors[row] ?? '#f7f3e6';
      context.fillRect(x, y, 64, 96);
      if (row < 40 || row >= 45 && row <= 57) {
        context.strokeStyle = row === 37 || side === 1 && row < 37 ? '#338692' : '#dfd8c5';
        context.lineWidth = 2;
        context.strokeRect(x + 2, y + 2, 60, 92);
      }
      if (row < 37 && side === 0) {
        context.drawImage(image, row % 8 * 192, 1536 - (Math.floor(row / 8) + 1) * 288, 192, 288, x, y, 64, 96);
      }
      if (row >= 45 && row <= 51 && side === 0) {
        context.fillStyle = row === 50 ? '#a42e38' : '#172c3a';
        context.font = `bold ${row === 51 ? 16 : 12}px sans-serif`;
        context.textAlign = 'center'; context.textBaseline = 'middle';
        context.fillText(['100', '500', '1000', '5000', '10000', '-10000', 'EAST'][row - 45], x + 32, y + 48);
      }
      if (row >= 52 && row <= 57 && side === 0) {
        const n = row - 51, positions = [[.25, .25], [.75, .75], [.25, .75], [.75, .25], [.25, .5], [.75, .5]];
        if (n % 2) positions.unshift([.5, .5]);
        const pips = n === 1 ? positions.slice(0, 1) : positions.slice(n % 2 ? 1 : 0, (n % 2 ? 1 : 0) + n - n % 2)
          .concat(n % 2 ? [[.5, .5]] : []);
        context.fillStyle = n === 1 ? '#a42e38' : '#172c3a';
        for (const [px, py] of pips) { context.beginPath(); context.arc(x + px * 64, y + py * 96, 6, 0, Math.PI * 2); context.fill(); }
      }
    }
    const gl = this.gl, texture = gl.createTexture();
    if (!texture) throw Error('GPU texture allocation failed.');
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, canvas);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return texture;
  }

  draw(layout: Layout, frame: Frame, viewport: Viewport): void {
    if (this.disposed || this.pending) throw Error('Overlapping or disposed WebGL publication.');
    const started = performance.now(), gl = this.gl;
    const plan = buildWebglPlan(layout, frame, viewport);
    this.matrices = [fittedCamera(plan, frame, false, this.pan), fittedCamera(plan, frame, true)];
    for (const batch of [this.table, this.hand]) { batch.mesh.instanceCount = 0; batch.targets.length = 0; }
    for (const batch of [this.tableSlots, this.handSlots]) { batch.instanceCount = 0; batch.names.length = 0; }
    const push = (batch: Batch, model: Float32Array, row: number, target: Target | null = null): void => {
      const index = batch.mesh.instanceCount;
      setTileInstance(batch.mesh, index, model, row);
      batch.targets[index] = target;
    };
    const midpoint = plan.bounds.getCenter(new Vector3()), dimensions = plan.bounds.getSize(new Vector3());
    push(this.table, solidPose(midpoint.x, -.035, midpoint.z, dimensions.x + .08, .04, dimensions.z + .08), 41);
    push(this.table, solidPose(midpoint.x, -.01, midpoint.z, dimensions.x, .008, dimensions.z), 40);
    for (const placement of plan.entities.values()) {
      const { entity, pose, separate } = placement, batch = separate ? this.hand : this.table;
      const model = tileModel(pose, entity.kind);
      const row = entity.kind === 'TILE' ? entity.appearance.faceIndex !== null && entity.appearance.showFace
        ? entity.appearance.faceIndex : entity.appearance.backIndex % 2 === 0 ? 37 : 38
        : entity.kind === 'STICK' ? 45 + Math.min(5, Math.max(0, entity.appearance.valueIndex ?? 0)) : 51;
      if (row < 0 || row > 63) throw Error('Unsupported tile appearance.');
      const pickup = frame.pickup?.canTake && frame.pickup.batchPreviewSlotNames.includes(entity.slotName);
      if (entity.display.selected || entity.display.hovered || entity.display.highlight > 0 || entity.display.drawn || pickup) {
        const border = new Matrix4().fromArray(model).multiply(new Matrix4().makeScale(1.10, 1.06, .3));
        push(batch, Float32Array.from(border.elements), entity.display.selected || pickup ? 42 : 43, { kind: 'entity', id: entity.entityId });
      }
      push(batch, model, row, { kind: 'entity', id: entity.entityId });
    }
    const occupied = new Set(frame.entities.map(entity => entity.slotName));
    const dragging = frame.entities.some(entity => entity.display.held);
    for (const [name, placement] of plan.slots) {
      const pose = placement.poses[placement.slot.shadowRotationIndex] ?? placement.poses[0];
      if (!pose) continue;
      const batch = placement.separate ? this.handSlots : this.tableSlots;
      if (batch.instanceCount >= capacity) throw Error('Slot picking capacity exceeded.');
      batch.modelData.set(tileModel(pose, placement.slot.kind), batch.instanceCount * 16);
      batch.names[batch.instanceCount++] = name;
      if (!occupied.has(name) && placement.slot.drawShadow
        && (dragging || frame.pickup?.batchPreviewSlotNames.includes(name))) {
        const shadow: RenderPose = { position: pose.position.clone(), rotation: pose.rotation, scale: pose.scale };
        shadow.position.y = -.003;
        const model = new Matrix4().fromArray(tileModel(shadow, placement.slot.kind)).multiply(new Matrix4().makeScale(1, 1, .035));
        push(placement.separate ? this.hand : this.table, Float32Array.from(model.elements),
          frame.pickup?.batchPreviewSlotNames.includes(name) ? 59 : 58);
      }
    }
    if (frame.dice) for (let i = 0; i < 2; i++) {
      push(this.table, solidPose((i ? 1 : -1) * .035, .028, 0, .055, .055, .055),
        51 + frame.dice.values[i], frame.dice.canRoll ? { kind: 'dice', id: null } : null);
    }
    gl.disable(gl.SCISSOR_TEST); gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.clearColor(.035, .065, .085, 1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LEQUAL); gl.disable(gl.CULL_FACE); gl.disable(gl.BLEND);
    gl.useProgram(this.program); gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.atlases[frame.presentation.tileLabels ? 1 : 0] ?? this.texture);
    gl.uniform1i(gl.getUniformLocation(this.program, 'u_atlas'), 0);
    gl.uniform2f(gl.getUniformLocation(this.program, 'u_atlasGrid'), 24, 8);
    gl.uniform3f(gl.getUniformLocation(this.program, 'u_lightDir'), -.3, 1, .4);
    this.drawBatch(this.table, plan.board, this.matrices[0], viewport);
    if (plan.hand.height > 0) this.drawBatch(this.hand, plan.hand, this.matrices[1], viewport);
    gl.disable(gl.SCISSOR_TEST);
    this.check();
    const sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
    if (!sync) throw Error('GPU frame fence allocation failed.');
    gl.flush();
    this.pending = { sync, commit: { frame, plan, viewport, milliseconds: performance.now() - started } };
  }

  private drawBatch(batch: Batch, rect: Rect, matrix: Float32Array, viewport: Viewport): void {
    const gl = this.gl, ratio = viewport.renderPixelRatio;
    const x = Math.round(rect.x * ratio), y = Math.round((viewport.heightCssPx - rect.y - rect.height) * ratio);
    const width = Math.max(1, Math.round(rect.width * ratio)), height = Math.max(1, Math.round(rect.height * ratio));
    gl.enable(gl.SCISSOR_TEST); gl.scissor(x, y, width, height); gl.viewport(x, y, width, height);
    gl.clearColor(.035, .065, .085, 1); gl.clear(gl.DEPTH_BUFFER_BIT | (batch === this.hand ? gl.COLOR_BUFFER_BIT : 0));
    gl.uniformMatrix4fv(gl.getUniformLocation(this.program, 'u_viewProj'), false, matrix);
    uploadTileInstances(gl, batch.mesh); drawTileMesh(gl, batch.mesh); this.drawCount++;
  }

  poll(): SceneCommit | null {
    if (!this.pending) return null;
    const gl = this.gl, { sync, commit } = this.pending;
    this.check();
    const result = gl.clientWaitSync(sync, 0, 0);
    if (result === gl.TIMEOUT_EXPIRED) return null;
    if (result === gl.WAIT_FAILED) throw Error('GPU frame fence failed.');
    gl.deleteSync(sync); this.pending = null;
    this.committed = commit;
    const snapshot = (batch: Batch): PickBatch => ({ modelData: batch.mesh.modelData.slice(0, batch.mesh.instanceCount * 16),
      instanceCount: batch.mesh.instanceCount, targets: batch.targets.slice() });
    const slots = (batch: SlotBatch): SlotBatch => ({ modelData: batch.modelData.slice(0, batch.instanceCount * 16),
      instanceCount: batch.instanceCount, names: batch.names.slice() });
    this.picking = { table: snapshot(this.table), hand: snapshot(this.hand), tableSlots: slots(this.tableSlots),
      handSlots: slots(this.handSlots), matrices: this.matrices };
    this.updateLabels(commit);
    return commit;
  }

  cancelPending(): void {
    if (this.pending) this.gl.deleteSync(this.pending.sync);
    this.pending = null;
  }

  pick(x: number, y: number, touch = false): Target | null {
    const commit = this.committed;
    if (!commit || !this.picking) return null;
    const hand = contains(commit.plan.hand, x, y), rect = hand ? commit.plan.hand : commit.plan.board;
    if (!contains(rect, x, y)) return null;
    const batch = hand ? this.picking.hand : this.picking.table, matrix = this.picking.matrices[hand ? 1 : 0];
    const hit = pickTile(batch, pickRay(rect, x, y, matrix));
    if (hit && batch.targets[hit.instanceIndex]) return batch.targets[hit.instanceIndex];
    if (!touch) return null;
    let nearest: Target | null = null, distance = 22 * 22;
    for (const placement of commit.plan.entities.values()) {
      if (placement.separate !== hand || !Object.values(placement.entity.interaction).some(Boolean)) continue;
      const point = this.project(placement.entity.entityId);
      if (!point) continue;
      const d = (point.x - x) ** 2 + (point.y - y) ** 2;
      if (d < distance) { distance = d; nearest = { kind: 'entity', id: placement.entity.entityId }; }
    }
    return nearest;
  }

  pickSlot(x: number, y: number): string | null {
    const commit = this.committed;
    if (!commit || !this.picking) return null;
    const hand = contains(commit.plan.hand, x, y), rect = hand ? commit.plan.hand : commit.plan.board;
    if (!contains(rect, x, y)) return null;
    const batch = hand ? this.picking.handSlots : this.picking.tableSlots;
    const hit = pickTile(batch, pickRay(rect, x, y, this.picking.matrices[hand ? 1 : 0]));
    return hit ? batch.names[hit.instanceIndex] : null;
  }

  project(entityId: string): { x: number; y: number; separate: boolean } | null {
    const commit = this.committed, placement = commit?.plan.entities.get(entityId);
    if (!commit || !placement || !this.picking) return null;
    const rect = placement.separate ? commit.plan.hand : commit.plan.board;
    const point = placement.pose.position.clone(); point.z *= -1;
    point.applyMatrix4(new Matrix4().fromArray(this.picking.matrices[placement.separate ? 1 : 0]));
    if (point.z < -1 || point.z > 1) return null;
    return { x: rect.x + (point.x + 1) * rect.width / 2, y: rect.y + (1 - point.y) * rect.height / 2, separate: placement.separate };
  }

  projectSlot(slotName: string): { x: number; y: number } | null {
    const commit = this.committed, placement = commit?.plan.slots.get(slotName);
    if (!commit || !placement || !this.picking) return null;
    const pose = placement.poses[placement.slot.shadowRotationIndex] ?? placement.poses[0];
    if (!pose) return null;
    const rect = placement.separate ? commit.plan.hand : commit.plan.board;
    const point = pose.position.clone(); point.z *= -1;
    point.applyMatrix4(new Matrix4().fromArray(this.picking.matrices[placement.separate ? 1 : 0]));
    return { x: rect.x + (point.x + 1) * rect.width / 2, y: rect.y + (1 - point.y) * rect.height / 2 };
  }

  private updateLabels(commit: SceneCommit): void {
    const present = new Set<number>();
    for (const seat of commit.frame.seats) {
      present.add(seat.seat);
      let node = this.labelNodes.get(seat.seat);
      if (!node) {
        node = document.createElement('span'); this.labelNodes.set(seat.seat, node); this.labels.append(node);
      }
      const relative = (seat.seat - (commit.frame.session.viewSeat ?? 0) + 4) % 4;
      const rect = commit.plan.board;
      const positions = [[.5, .95], [.95, .5], [.5, .04], [.05, .5]];
      node.style.left = `${rect.x + rect.width * positions[relative][0]}px`;
      node.style.top = `${rect.y + rect.height * positions[relative][1]}px`;
      const text = `${['E', 'S', 'W', 'N'][seat.seat]} · ${seat.nick ?? `Seat ${seat.seat + 1}`}${seat.score === null ? '' : ` · ${seat.score}`}`;
      if (node.textContent !== text) node.textContent = text;
      node.className = commit.frame.turn?.activeSeat === seat.seat ? 'webgl-seat active' : 'webgl-seat';
    }
    for (const [seat, node] of this.labelNodes) if (!present.has(seat)) { node.remove(); this.labelNodes.delete(seat); }
  }

  clear(): void {
    this.cancelPending(); this.committed = null; this.picking = null;
    this.table.mesh.instanceCount = 0; this.hand.mesh.instanceCount = 0;
    this.table.targets.length = 0; this.hand.targets.length = 0;
    this.labels.replaceChildren(); this.labelNodes.clear();
    const gl = this.gl;
    gl.disable(gl.SCISSOR_TEST); gl.clearColor(.035, .065, .085, 1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  }

  private check(): void {
    if (this.gl.isContextLost() || this.gl.getError() !== this.gl.NO_ERROR) throw Error('WebGL context or GPU operation failed.');
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true; this.cancelPending(); this.committed = null; this.picking = null;
    disposeTileMesh(this.gl, this.table.mesh); disposeTileMesh(this.gl, this.hand.mesh);
    for (const texture of this.atlases) this.gl.deleteTexture(texture);
    this.gl.deleteProgram(this.program); this.labels.remove(); this.labelNodes.clear();
    this.gl.getExtension('WEBGL_lose_context')?.loseContext();
  }
}
