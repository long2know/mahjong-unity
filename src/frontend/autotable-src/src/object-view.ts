import { BufferAttribute, BufferGeometry, Group, InstancedMesh, Mesh, MeshBasicMaterial, MeshLambertMaterial, Object3D, PlaneGeometry, Vector3 } from "three";
// Phase K Wave 8 — Hand-rolled subset of BufferGeometryUtils.mergeGeometries.
// We only need the simple case used in `addStatic()`: merging N geometries
// that share the same attribute layout (the tray prototype cloned, rotated,
// and translated 24 times).  The full `mergeGeometries()` helper carries
// ~30 kB of side modules (mergeAttributes, deepCloneAttribute,
// interleaveAttributes, mergeVertices, etc.) that are not exercised — pulling
// only what we need shaves ~10-15 kB off the heavy three-renderer chunk.
//
// Same contract as the upstream `mergeGeometries(geometries, false)` call:
//   • All inputs share the same attribute name set + per-attribute itemSize.
//   • All inputs are either all indexed or all non-indexed (we only emit
//     the non-indexed path; the upstream tray geometry is non-indexed after
//     the GLTFLoader pass).
//   • Output is a fresh non-indexed BufferGeometry.
function mergeSimpleGeometries(geometries: ReadonlyArray<BufferGeometry>): BufferGeometry {
  if (geometries.length === 0) return new BufferGeometry();
  const first = geometries[0];
  const attrNames = Object.keys(first.attributes);
  const result = new BufferGeometry();
  for (const name of attrNames) {
    const firstAttr = first.attributes[name];
    const itemSize = firstAttr.itemSize;
    const normalized = firstAttr.normalized;
    let total = 0;
    for (const g of geometries) total += g.attributes[name].array.length;
    const ArrayCtor = firstAttr.array.constructor as { new (length: number): ArrayLike<number> & { set(arr: ArrayLike<number>, offset?: number): void } };
    const merged = new ArrayCtor(total);
    let offset = 0;
    for (const g of geometries) {
      const arr = g.attributes[name].array as ArrayLike<number>;
      merged.set(arr, offset);
      offset += arr.length;
    }
    result.setAttribute(name, new BufferAttribute(merged as unknown as ArrayLike<number> & ArrayBufferView as Float32Array, itemSize, normalized));
  }
  return result;
}

import { World } from "./world";
import { Client } from "./client";
import { AssetLoader } from "./asset-loader";
import { Center } from "./center";
import { readVariantFromUrl } from "./client-ui";
import { ThingParams, ThingGroup, TileThingGroup, StickThingGroup, MarkerThingGroup } from "./thing-group";
import { ThingType, Place, TileVariant, GameType } from "./types";
import type { Render } from './renderer/renderer-sink';
export type { Render } from './renderer/renderer-sink';

const MAX_SHADOWS = 300;

export class ObjectView {
  mainGroup: Group;
  private assetLoader: AssetLoader;

  private center: Center;

  // Hicks 2026-06-01 round 2 — store the merged stick-tray mesh so we can
  // toggle visibility per variant.  Changsha has no sticks; the dark gray
  // tray geometry was rendering as the 4 corner "wedges" in Stephen's
  // verification screenshot (`broken-deal-repro-2026-06-01T20-05-35-522Z.png`).
  // Built once, shown only when the active variant uses sticks.
  private trayMesh: Mesh | null = null;

  private thingGroups: Map<ThingType, ThingGroup>;

  private shadowObject: InstancedMesh;
  private dropShadowProto: Mesh;
  private dropShadowObjects: Array<Mesh>;

  selectedObjects: Array<Mesh>;

  // Phase K Wave 9 — Commentary tile-ref highlight outline pool.
  // Filled per-frame by `updateThings` when a Render carries
  // `highlighted: true`; consumed by `MainView` to drive
  // `CustomOutline.setHighlight` (a separate hull pool from the
  // selection ring).  `highlightIntensity` is the sin-wave
  // envelope value the World computes for the active pulse —
  // `MainView` propagates it as the outline-thickness multiplier
  // so the silhouette modulates each frame.
  highlightedObjects: Array<Mesh>;
  highlightIntensity: number;

  constructor(mainGroup: Group, assetLoader: AssetLoader, client: Client) {
    this.mainGroup = mainGroup;
    this.assetLoader = assetLoader;

    this.center = new Center(this.assetLoader, client);
    this.center.mesh.position.set(World.WIDTH / 2, World.WIDTH / 2, 0.75);
    this.dropShadowObjects = [];
    this.selectedObjects = [];
    this.highlightedObjects = [];
    this.highlightIntensity = 0;

    this.thingGroups = new Map();
    this.thingGroups.set(ThingType.TILE, new TileThingGroup(this.assetLoader, this.mainGroup));
    this.thingGroups.set(ThingType.STICK, new StickThingGroup(this.assetLoader, this.mainGroup));
    this.thingGroups.set(ThingType.MARKER, new MarkerThingGroup(this.assetLoader, this.mainGroup));

    const plane = new PlaneGeometry(1, 1, 1);
    let material = new MeshBasicMaterial({
      transparent: true,
      opacity: 0.1,
      color: 0,
      depthWrite: false,
    });
    this.shadowObject = new InstancedMesh(plane, material, MAX_SHADOWS);
    this.shadowObject.visible = true;
    this.mainGroup.add(this.shadowObject);

    material = material.clone();
    material.opacity = 0.2;

    this.dropShadowProto = new Mesh(plane, material);
    this.dropShadowProto.name = 'dropShadow';

    this.addStatic();

    // Hicks 2026-06-01 round 2 — gate Riichi-only static scenery (point
    // sticks + central score readout) based on the URL-declared variant
    // at boot.  World.updateConditions re-calls setVariant whenever the
    // backend / dropdown flips us into / out of CHANGSHA, so this initial
    // pass just makes sure the first paint matches the URL intent (so
    // Stephen never sees the corner wedges flash on for Changsha).
    const urlVariant = readVariantFromUrl();
    if (urlVariant !== null) {
      this.setVariant(urlVariant as GameType);
    }
  }

  /**
   * Hicks 2026-06-01 round 2 — variant-aware static scenery toggle.
   * Changsha hides the upstream stick trays (gray corner wedges) and
   * the central score readout mesh (the "Seat 0" floating HUD).  Riichi
   * variants restore both.  Idempotent; safe to call repeatedly from
   * `World.updateConditions`.
   */
  setVariant(gameType: GameType): void {
    const isChangsha = gameType === GameType.CHANGSHA;
    if (this.trayMesh !== null) {
      this.trayMesh.visible = !isChangsha;
    }
    this.center.mesh.visible = !isChangsha;
  }

  replaceThings(params: Map<number, ThingParams>): void {
    for (const type of [ThingType.TILE, ThingType.STICK, ThingType.MARKER]) {
      const typeParams = [...params.values()].filter(p => p.type === type);
      typeParams.sort((a, b) => a.index - b.index);

      const startIndex = typeParams[0]?.index ?? 0;
      const thingGroup = this.thingGroups.get(type)!;
      thingGroup.replace(startIndex, typeParams);
    }
  }

  replaceShadows(places: Array<Place>): void {
    const dummy = new Object3D();

    this.shadowObject.count = 0;
    for (const place of places) {
      dummy.position.set(place.position.x, place.position.y, 0.1);
      dummy.scale.set(place.size.x, place.size.y, 1);
      dummy.updateMatrix();

      const idx = this.shadowObject.count++;
      this.shadowObject.setMatrixAt(idx, dummy.matrix);
    }
    this.shadowObject.instanceMatrix.needsUpdate = true;
  }

  private addStatic(): void {
    const tableMesh = this.assetLoader.makeTable();
    tableMesh.name = 'table';
    tableMesh.position.set(World.WIDTH / 2, World.WIDTH / 2, 0);
    this.mainGroup.add(tableMesh);
    this.mainGroup.add(this.center.mesh);

    tableMesh.updateMatrixWorld();
    this.center.mesh.updateMatrixWorld();

    // Hicks 2026-06-01 round 2 — skip building the merged stick-tray
    // geometry entirely when the URL declares a stickless variant
    // (Changsha).  Just hiding the mesh wasn't enough: THREE still ran
    // `computeBoundingSphere()` on the merged geometry once per frame
    // (frustum cull), and the underlying GLB tray prototype has a
    // degenerate vertex that produces a NaN bounding radius — the exact
    // `Computed radius is NaN` log Vasquez captured.  Skipping the merge
    // removes both the visible wedge artefact AND the console error.
    const urlVariant = readVariantFromUrl();
    if (urlVariant === GameType.CHANGSHA) {
      this.trayMesh = null;
      return;
    }

    const tray = this.assetLoader.makeTray();
    tray.updateMatrixWorld();
    const geometries: Array<BufferGeometry> = [];
    for (let i = 0; i < 4; i++) {
      for (let j = 0; j < 6; j++) {
        const trayPos = new Vector3(
          25 + 24 * j - World.WIDTH / 2,
          -33 - World.WIDTH / 2,
          0
        );
        trayPos.applyAxisAngle(new Vector3(0, 0, 1), Math.PI * i / 2);

        const geometry = tray.geometry.clone();

        geometry.rotateZ(Math.PI * i / 2);
        geometry.translate(
          trayPos.x + World.WIDTH / 2,
          trayPos.y + World.WIDTH / 2,
          0
        );

        geometries.push(geometry);
      }
    }
    tray.geometry = mergeSimpleGeometries(geometries);
    tray.position.set(0, 0, 0);
    this.mainGroup.add(tray);
    tray.updateMatrixWorld();
    this.trayMesh = tray;
  }

  updateScores(scores: Array<number | null>): void {
    // Hicks 2026-06-01 round 2 — defense in depth.  When the variant hides
    // the center mesh (Changsha), skip the canvas paint to avoid burning
    // CPU on a texture nothing renders, and to keep the cached canvas in
    // its initial blank/transparent state if the variant ever flips back.
    if (!this.center.mesh.visible) {
      this.center.setScores(scores);
      return;
    }
    this.center.setScores(scores);
    this.center.draw();
  }

  updateThings(things: Array<Render>): void {
    this.selectedObjects.splice(0);
    this.highlightedObjects.splice(0);
    for (const thing of things) {
      const thingGroup = this.thingGroups.get(thing.type)!;
      if (thing.hidden) {
        thingGroup.hide(thing.thingIndex);
        continue;
      }
      const custom = thing.hovered || thing.selected || thing.held || thing.bottom || thing.highlighted || thing.drawn;
      if (!custom && thingGroup.canSetSimple()) {
        thingGroup.setSimple(thing.thingIndex, thing.place.position, thing.place.rotation, thing.place.scale);
        continue;
      }

      const obj = thingGroup.setCustom(
        thing.thingIndex, thing.place.position, thing.place.rotation, thing.place.scale);

      const material = obj.material as MeshLambertMaterial;
      const wasTransparent = material.transparent;

      material.color.set(1.0, 1.0, 1.0);
      material.emissive.set(0.0, 0.0, 0.0);
      material.transparent = false;
      material.depthTest = true;
      obj.renderOrder = 0;

      if (thing.hovered) {
        material.emissive.set(0.05, 0.05, 0.05);
      }
      if (thing.drawn) material.emissive.set(0.02, 0.14, 0.2);

      if (thing.bottom) {
        material.color.set(0.8, 0.8, 0.8);
      }

      if (thing.selected) {
        this.selectedObjects.push(obj);
      }

      if (thing.highlighted) {
        // Subtle warm emissive lift on the mesh face itself so the
        // outline pulse reads as "this is the referenced tile"
        // rather than just a floating ring.  The outline hull
        // carries the dominant signal; this is cosmetic glue.
        material.emissive.set(0.15, 0.08, 0.0);
        this.highlightedObjects.push(obj);
      }

      if (thing.held) {
        material.transparent = true;
        material.opacity = thing.temporary ? 0.7 : 1;
        material.depthTest = false;
        obj.position.z += 1;
        obj.renderOrder = 1;
      }

      if (material.transparent !== wasTransparent) {
        material.needsUpdate = true;
      }

      obj.updateMatrix();
      obj.updateMatrixWorld();
    }
  }

  updateDropShadows(places: Array<Place>): void {
    for (const obj of this.dropShadowObjects) {
      this.mainGroup.remove(obj);
    }
    this.dropShadowObjects.splice(0);

    for (const place of places) {
      const obj = this.dropShadowProto.clone();
      obj.position.set(
        place.position.x,
        place.position.y,
        place.position.z - place.size.z/2 + 0.2);
      obj.scale.set(place.size.x, place.size.y, 1);
      this.dropShadowObjects.push(obj);
      this.mainGroup.add(obj);
      obj.updateMatrixWorld();
    }
  }

  setTileVariant(tileVariant: TileVariant): void {
    const tileThingGroup = this.thingGroups.get(ThingType.TILE) as TileThingGroup;
    tileThingGroup.setVariant(tileVariant);
  }
}
