import { OrthographicCamera, PerspectiveCamera, Vector3 } from 'three';
import { Size, type Place } from './types';

const TABLE_CENTER = new Vector3(87, 87, 0);
const UP = new Vector3(0, 0, 1);
const HAND_START = 46;
const HAND_LENGTH = 14 * Size.TILE.x;

export function responsiveHandScale(width: number, height: number): number {
  return width > 0 && height > 0 && (width <= 900 || height <= 520) ? 1.7 : 1;
}

function localHandPosition(position: Vector3, seat: number, scale: number): Vector3 {
  const angle = seat * Math.PI / 2;
  const result = position.clone().sub(TABLE_CENTER).applyAxisAngle(UP, -angle).add(TABLE_CENTER);
  result.x = TABLE_CENTER.x + (result.x - HAND_START - HAND_LENGTH / 2) * scale;
  // Move the enlarged row outward, clear of every seat's wall and meld lanes.
  result.y = (result.y - Size.TILE.y) * scale - 10;
  result.z *= scale;
  return result.sub(TABLE_CENTER).applyAxisAngle(UP, angle).add(TABLE_CENTER);
}

export function presentLocalHand(place: Place, seat: number, scale: number): Place {
  if (scale === 1) return place;
  return {
    ...place,
    position: localHandPosition(place.position, seat, scale),
    size: place.size.clone().multiplyScalar(scale),
    scale,
  };
}

export function localHandFitCorners(seat: number | null, scale: number): Vector3[] {
  if (seat === null || scale === 1) return [];
  const corners = [];
  // Reserve the complete row even before dealing and after discards/melds:
  // changes in hand size must never change the fitted camera.
  for (const x of [HAND_START, HAND_START + HAND_LENGTH]) {
    for (const y of [0, Size.TILE.y]) {
      for (const z of [0, Size.TILE.z]) {
        const position = new Vector3(x, y, z).sub(TABLE_CENTER)
          .applyAxisAngle(UP, seat * Math.PI / 2).add(TABLE_CENTER);
        corners.push(localHandPosition(position, seat, scale));
      }
    }
  }
  return corners;
}

export interface ScreenArea {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/** Fit projected world corners, not the canvas aspect or a flat XY rectangle. */
export function fitTableProjection(
  camera: PerspectiveCamera | OrthographicCamera,
  corners: readonly Vector3[], width: number, height: number, area: ScreenArea,
): void {
  if (width <= 0 || height <= 0 || corners.length === 0) return;
  camera.clearViewOffset();
  camera.updateMatrixWorld(true);
  const projected = corners.map(point => point.clone().project(camera));
  const minX = Math.min(...projected.map(p => p.x)), maxX = Math.max(...projected.map(p => p.x));
  const minY = Math.min(...projected.map(p => p.y)), maxY = Math.max(...projected.map(p => p.y));
  const scale = Math.min(
    (area.right - area.left) * 2 / width / (maxX - minX),
    (area.bottom - area.top) * 2 / height / (maxY - minY),
  );
  if (!Number.isFinite(scale) || scale <= 0) return;
  const x = (area.left + area.right) / width - 1 - scale * (minX + maxX) / 2;
  const y = 1 - (area.top + area.bottom) / height - scale * (minY + maxY) / 2;
  // Store the fit in camera state, not only its derived matrix: selection
  // rebuilds the projection during an ordinary press or box drag.
  camera.setViewOffset(
    width, height,
    (scale - 1 - x) * width / (2 * scale),
    (scale - 1 + y) * height / (2 * scale),
    width / scale, height / scale,
  );
}
