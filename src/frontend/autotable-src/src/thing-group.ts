import { BufferGeometry, Group, InstancedBufferAttribute, InstancedMesh, Material, Matrix4, Mesh, MeshLambertMaterial, Quaternion, Texture, Vector3 } from "three";
import { AssetLoader } from "./asset-loader";
import { ThingType, TileVariant } from "./types";
import { rotEquals } from "./utils";

const TILE_DU = 32 / 256;
const TILE_DV = 40 / 256;
const STICK_DV = 1 / 6;

export interface ThingParams {
  type: ThingType;
  typeIndex: number;
  index: number;
  hidden?: boolean;
}

export abstract class ThingGroup {
  protected assetLoader: AssetLoader;
  protected startIndex: number = 0;
  protected meshes: Array<Mesh> = [];
  protected group: Group;
  protected params: Array<ThingParams> = [];

  abstract createMesh(typeIndex: number): Mesh;

  constructor(assetLoader: AssetLoader, group: Group) {
    this.assetLoader = assetLoader;
    this.group = group;
  }

  canSetSimple(): boolean {
    return false;
  }

  setSimple(index: number, position: Vector3, rotation: Quaternion, scale = 1): void {}

  hide(index: number): void {
    this.meshes[index - this.startIndex].visible = false;
  }

  setCustom(index: number, position: Vector3, rotation: Quaternion, scale = 1): Mesh {
    const i = index - this.startIndex;
    const mesh = this.meshes[i];
    mesh.visible = !this.params[i].hidden;
    if (!mesh.visible) {
      return mesh;
    }
    mesh.position.copy(position);
    mesh.setRotationFromQuaternion(rotation);
    mesh.scale.setScalar(scale);
    return mesh;
  }

  replace(startIndex: number, params: Array<ThingParams>): void {
    for (const mesh of this.meshes) {
      (mesh.material as Material).dispose();
      mesh.geometry.dispose();
      this.group.remove(mesh);
    }
    this.meshes.splice(0);

    for (const p of params) {
      const mesh = this.createMesh(p.typeIndex);
      mesh.matrixAutoUpdate = false;
      mesh.visible = false;
      this.meshes.push(mesh);
      this.group.add(mesh);
    }
    this.startIndex = startIndex;
    this.params = params;
  }
}

export class MarkerThingGroup extends ThingGroup {
  createMesh(typeIndex: number): Mesh {
    return this.assetLoader.makeMarker();
  }
}

abstract class InstancedThingGroup extends ThingGroup {
  protected instancedMesh: InstancedMesh = null!;
  private zero: Matrix4 = new Matrix4().makeScale(0, 0, 0);
  private hiddenInstances: Set<number> = new Set();

  abstract getOriginalMesh(): Mesh;
  abstract getUvChunk(): string;
  abstract getOffset(typeIndex: number): Vector3;

  override canSetSimple(): boolean {
    return true;
  }

  createInstancedMesh(params: Array<ThingParams>): InstancedMesh {
    const origMesh = this.getOriginalMesh();

    const origMaterial = origMesh.material as MeshLambertMaterial;
    const material = new MeshLambertMaterial({
      map: origMaterial.map,
      color: origMaterial.color,
    });

    const paramChunk = `
attribute vec3 offset;
#include <common>
`;
    const uvChunk = this.getUvChunk();
    material.onBeforeCompile = shader => {
      // console.log(shader.vertexShader);
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', paramChunk)
        .replace('#include <uv_vertex>', uvChunk);
    };

    material.customProgramCacheKey = () => origMesh.name;

    // Weird bug: in Firefox Android, the last instance is not being rendered.
    const extra = 1;

    const data = new Float32Array((params.length + extra) * 3);
    for (let i = 0; i < params.length; i++) {
      const v = this.getOffset(params[i].typeIndex);
      data[3 * i] = v.x;
      data[3 * i + 1] = v.y;
      data[3 * i + 2] = v.z;
    }

    const geometry = new BufferGeometry().copy(origMesh.geometry);
    geometry.setAttribute('offset', new InstancedBufferAttribute(data, 3));
    const instancedMesh = new InstancedMesh(geometry, material, params.length + extra);
    instancedMesh.frustumCulled = false;
    for (let i = 0; i < params.length + extra; i++) {
      instancedMesh.setMatrixAt(i, this.zero);
    }
    instancedMesh.instanceMatrix.needsUpdate = true;
    return instancedMesh;
  }

  override replace(startIndex: number, params: Array<ThingParams>): void {
    super.replace(startIndex, params);

    if (this.instancedMesh !== null) {
      (this.instancedMesh.material as Material).dispose();
      this.instancedMesh.geometry.dispose();
      this.group.remove(this.instancedMesh);
    }
    this.instancedMesh = this.createInstancedMesh(params);
    this.hiddenInstances = new Set(params.map((_, i) => i));
    this.group.add(this.instancedMesh);
  }

  override hide(index: number): void {
    const i = index - this.startIndex;
    if (this.hiddenInstances.has(i)) {
      return;
    }
    this.meshes[i].visible = false;
    this.instancedMesh.setMatrixAt(i, this.zero);
    this.instancedMesh.instanceMatrix.needsUpdate = true;
    this.hiddenInstances.add(i);
  }

  override setSimple(index: number, position: Vector3, rotation: Quaternion, scale = 1): void {
    const i = index - this.startIndex;
    const mesh = this.meshes[i];
    if (this.params[i].hidden) {
      this.hide(index);
      return;
    }
    // Hiding clears the instance, not its cached transform; revealing at the
    // same position must restore that instance instead of taking the fast path.
    const wasHidden = this.hiddenInstances.delete(i);
    if (!wasHidden && !mesh.visible && mesh.position.equals(position)
      && rotEquals(mesh.quaternion, rotation) && mesh.scale.x === scale) {
      return;
    }
    mesh.position.copy(position);
    mesh.setRotationFromQuaternion(rotation);
    mesh.scale.setScalar(scale);
    mesh.updateMatrix();
    mesh.visible = false;
    this.instancedMesh.setMatrixAt(i, mesh.matrix);
    this.instancedMesh.instanceMatrix.needsUpdate = true;
  }

  override setCustom(index: number, position: Vector3, rotation: Quaternion, scale = 1): Mesh {
    const i = index - this.startIndex;
    const mesh = this.meshes[i];
    if (this.params[i].hidden) {
      this.hide(index);
      return mesh;
    }
    this.hiddenInstances.delete(i);
    mesh.position.copy(position);
    mesh.setRotationFromQuaternion(rotation);
    mesh.scale.setScalar(scale);
    mesh.visible = true;
    this.instancedMesh.setMatrixAt(i, this.zero);
    this.instancedMesh.instanceMatrix.needsUpdate = true;
    return mesh;
  }
}

export class TileThingGroup extends InstancedThingGroup {
  protected name: string = 'tile';

  private textures: Record<TileVariant, Texture>;
  private tileVariant: TileVariant = TileVariant.NO_LABELS;

  constructor(assetLoader: AssetLoader, group: Group) {
    super(assetLoader, group);
    const tileMaterial = this.assetLoader.meshes.tile.material as MeshLambertMaterial;
    this.textures = {
      [TileVariant.NO_LABELS]: tileMaterial.map!,
      [TileVariant.LABELS]: this.assetLoader.textures.tilesLabels,
    };
  }

  setVariant(tileVariant: TileVariant): void {
    if (this.tileVariant === tileVariant) return;
    this.tileVariant = tileVariant;
    this.updateMeshTileVariant();
  }

  override replace(startIndex: number, params: Array<ThingParams>): void {
    super.replace(startIndex, params);
    this.updateMeshTileVariant();
  }

  updateMeshTileVariant(): void {
    const texture = this.textures[this.tileVariant];

    for (const mesh of this.meshes) {
      this.updateTexture(mesh, texture);
    }
    this.updateTexture(this.instancedMesh, texture);
  }

  private updateTexture(mesh: Mesh, texture: Texture): void {
    (mesh.material as MeshLambertMaterial).map = texture;
    (mesh.material as MeshLambertMaterial).needsUpdate = true;
  }

  getOriginalMesh(): Mesh {
    return this.assetLoader.meshes.tile;
  }

  getUvChunk(): string {
    return `
#include <uv_vertex>
#ifdef USE_MAP
if (vMapUv.x <= ${TILE_DU} && vMapUv.y <= ${TILE_DV}) {
  vMapUv += offset.xy;
} else if (vMapUv.y >= ${4*TILE_DV}) {
  vMapUv.y += offset.z;
}
#endif
`;
  }

  getOffset(typeIndex: number): Vector3 {
    const x = (typeIndex % 37) % 8;
    const y = Math.floor((typeIndex % 37) / 8);
    const back = Math.floor(typeIndex / 37);
    return new Vector3(x * TILE_DU, y * TILE_DV, back * TILE_DV);
  }

  createMesh(typeIndex: number): Mesh {
    const mesh = this.assetLoader.make('tile');

    const x = (typeIndex % 37) % 8;
    const y = Math.floor((typeIndex % 37) / 8);
    const back = Math.floor(typeIndex / 37);

    // Clone geometry and modify front face
    const geometry = mesh.geometry.clone() as BufferGeometry;
    mesh.geometry = geometry;
    const uvs: Float32Array = geometry.attributes.uv.array as Float32Array;
    for (let i = 0; i < uvs.length; i += 2) {
      if (uvs[i] <= TILE_DU && uvs[i+1] <= TILE_DV) {
        uvs[i] += x * TILE_DU;
        uvs[i+1] += y * TILE_DV;
      } else if (uvs[i+1] >= 4 * TILE_DV) {
        uvs[i+1] += back * TILE_DV;
      }
    }

    return mesh;
  }
}

export class StickThingGroup extends InstancedThingGroup {
  getOriginalMesh(): Mesh {
    return this.assetLoader.meshes.stick;
  }

  getUvChunk(): string {
    return `
#include <uv_vertex>
#ifdef USE_MAP
vMapUv += offset.xy;
#endif
`;
  }

  getOffset(typeIndex: number): Vector3 {
    return new Vector3(0, typeIndex * STICK_DV, 0);
  }

  createMesh(typeIndex: number): Mesh {
    const mesh = this.assetLoader.make('stick');

    const geometry = mesh.geometry.clone() as BufferGeometry;
    mesh.geometry = geometry;
    const uvs: Float32Array = geometry.attributes.uv.array as Float32Array;
    for (let i = 0; i < uvs.length; i += 2) {
      uvs[i+1] += typeIndex * STICK_DV;
    }

    return mesh;
  }
}
