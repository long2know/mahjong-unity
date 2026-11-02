// Phase K Wave 7 — Asset URL imports.  Parcel used a `url:` prefix
// to ask its `@parcel/packager-raw-url` transformer to copy the file
// + emit a URL string.  Vite's native equivalent is the `?url` query
// suffix which the rollup plugin handles built-in.  Wave 7 is the
// Parcel→Vite swap so the imports now use the Vite form.
import tableJpg from '../img/table.jpg?url';
import tilesLabelsPng from '../img/tiles-labels.auto.png?url';
import glbModels from '../img/models.auto.glb?url';

import { LinearSRGBColorSpace, Material, Mesh, MeshLambertMaterial, PlaneGeometry, RepeatWrapping, Texture, TextureLoader } from 'three';
import type { MeshStandardMaterial } from 'three';
import type { GLTF, GLTFLoader as GLTFLoaderType } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { World } from './world';
import { Size } from './types';

// Phase K Wave 6 — GLTFLoader is a ~114 kB (source) addon that lives
// inside the heavy three-renderer chunk in W5.  We only need it for
// the single `loadModels` call, so split it out via a dynamic import
// — parcel emits it into its own sibling chunk that the renderer
// downloads in parallel with the texture loaders.  Net effect:
// the largest `three-renderer.<hash>.js` chunk drops below 700 kB
// (Wave 6 budget target — see docs/frontend-three-budget.md).
let cachedGltfLoaderCtor: (new () => GLTFLoaderType) | null = null;
async function getGltfLoader(): Promise<GLTFLoaderType> {
  if (cachedGltfLoaderCtor === null) {
    const mod = await import('three/examples/jsm/loaders/GLTFLoader.js');
    cachedGltfLoaderCtor = mod.GLTFLoader;
  }
  return new cachedGltfLoaderCtor();
}


export class AssetLoader {
  textures: Record<string, Texture> = {};
  meshes: Record<string, Mesh> = {};

  makeTable(): Mesh {
    const tableGeometry = new PlaneGeometry(
      World.WIDTH + Size.TILE.y, World.WIDTH + Size.TILE.y);
    const tableMaterial = new MeshLambertMaterial({ map: this.textures.table });
    const tableMesh = new Mesh(tableGeometry, tableMaterial);
    return tableMesh;
  }

  makeCenter(): Mesh {
    return this.cloneMesh(this.meshes.center);
  }

  makeTray(): Mesh {
    const mesh = this.cloneMesh(this.meshes.tray);
    (mesh.material as MeshStandardMaterial).color.set(0.22, 0.22, 0.22);
    return mesh;
  }

  make(what: string): Mesh {
    return this.cloneMesh(this.meshes[what]);
  }

  makeMarker(): Mesh {
    return this.cloneMesh(this.meshes.marker);
  }

  cloneMesh(mesh: Mesh): Mesh {
    const newMesh = mesh.clone();
    if (Array.isArray(mesh.material)) {
      newMesh.material = mesh.material.map(m => m.clone());
    } else {
      newMesh.material = mesh.material.clone();
    }

    return newMesh;
  }

  loadAll(): Promise<void> {
    return Promise.all([
      this.loadTexture(tableJpg, 'table'),
      this.loadTexture(tilesLabelsPng, 'tilesLabels'),
      this.loadModels(glbModels),
      (document as any).fonts.load('40px "Segment7Standard"'),
    ]).then(() => {
      this.textures.table.wrapS = RepeatWrapping;
      this.textures.table.wrapT = RepeatWrapping;
      this.textures.table.repeat.set(3, 3);
    });
  }

  loadTexture(url: string, name: string): Promise<void> {
    const loader = new TextureLoader();
    return new Promise(resolve => {
      loader.load(url, (texture: Texture) => {
        this.textures[name] = this.processTexture(texture);
        resolve();
      });
    });
  }

  async loadModels(url: string): Promise<void> {
    const loader = await getGltfLoader();
    return new Promise(resolve => {
      loader.load(url, (model: GLTF) => {
        for (const obj of model.scene.children) {
          if ((obj as Mesh).isMesh) {
            this.meshes[obj.name] = this.processMesh(obj as Mesh);
          } else {
            // eslint-disable-next-line no-console
            console.warn('unrecognized object', obj);
          }
        }
        resolve();
      });
    });
  }

  processTexture(texture: Texture): Texture {
    texture.flipY = false;
    texture.anisotropy = 4;
    texture.colorSpace = LinearSRGBColorSpace;
    return texture;
  }

  processMesh(mesh: Mesh): Mesh {
    if (Array.isArray(mesh.material)) {
      mesh.material = mesh.material.map(this.processMaterial.bind(this));
    } else {
      mesh.material = this.processMaterial(mesh.material);
    }
    return mesh;
  }

  processMaterial(material: Material): Material {
    const standard = material as MeshStandardMaterial;
    const map = standard.map;
    if (map !== null && map !== undefined) {
      map.colorSpace = LinearSRGBColorSpace;
      map.anisotropy = 4;
      return new MeshLambertMaterial({map});
    }
    // Phase K — playability fix: when the stripped MeshStandardMaterial
    // doesn't expose `map` (Phase L slim three.js shed the property),
    // build an untextured Lambert so the renderer doesn't choke on a
    // null `.image` lookup later in the WebGL upload path.
    return new MeshLambertMaterial();
  }
}
