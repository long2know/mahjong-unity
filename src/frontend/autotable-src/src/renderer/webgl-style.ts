import type { Entity } from './renderer-contract';

export const TILE_FINISH = {
  ivory: '#f7f3e6', side: '#e9e3d3',
  teal: { top: '#357d87', bottom: '#235f6b', edge: '#487e85' },
  amber: { top: '#b3824b', bottom: '#916034', edge: '#c19c6b' },
};

export function tileMaterialId(entity: Entity): number {
  const { appearance } = entity;
  const back = appearance.backIndex % 2;
  if (entity.kind !== 'TILE') return entity.kind === 'STICK'
    ? 45 + Math.min(5, Math.max(0, appearance.valueIndex ?? 0)) : 51;
  if (!appearance.knownFace || !appearance.showFace || appearance.faceIndex === null) return 37 + back;
  if (!Number.isInteger(appearance.faceIndex) || appearance.faceIndex < 0 || appearance.faceIndex > 36) {
    throw Error('Unsupported tile face.');
  }
  return appearance.faceIndex + back * 64;
}

export function tileSurface(material: number, side: number): {
  base: string; face: number | null; cap: typeof TILE_FINISH.teal | null;
} {
  const row = Math.floor(material) % 64;
  const back = row === 38 || Math.floor(material / 64) % 2 === 1;
  if (side === 2) return { base: TILE_FINISH.side, face: null, cap: null };
  if (row === 37 || row === 38 || row < 37 && side === 1) {
    return { base: TILE_FINISH.ivory, face: null, cap: back ? TILE_FINISH.amber : TILE_FINISH.teal };
  }
  return { base: TILE_FINISH.ivory, face: row < 37 ? row : null, cap: null };
}

/** Decorations never enter the canonical ray/AABB model catalog, including expanded visual cues. */
export function pickableModels<T>(modelData: Float32Array, targets: Array<T | null>, count: number): {
  modelData: Float32Array; instanceCount: number; targets: T[];
} {
  const eligible = Array.from({ length: count }, (_, index) => index).filter(index => targets[index] != null);
  const models = new Float32Array(eligible.length * 16);
  const visible: T[] = [];
  eligible.forEach((source, index) => {
    models.set(modelData.subarray(source * 16, source * 16 + 16), index * 16);
    visible.push(targets[source]!);
  });
  return { modelData: models, instanceCount: eligible.length, targets: visible };
}
