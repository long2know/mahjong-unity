// Phase K Wave 15 — WebGL2 renderer hello-world (Hicks, Frontend).
//
// Phase L W1 spike implementation kickoff.  The Phase K W14 doc
// `docs/phase-l-renderer-spike.md` recommended a "Go" on hand-
// rolling a WebGL2 renderer to replace the three.js-based
// `three-renderer-big` chunk (W14 hold-line: 406,635 B).  W15
// stands up the foundation: a build-pipeline-visible chunk that
// renders a single textured quad with NO three.js dependency,
// proving (a) the manualChunks split works, (b) the bundle math
// (180-220 KB target for the eventual full renderer) is achievable
// against a real `npm run build:vite`, and (c) the WebGL2 path
// works end-to-end against a real mahjong-tile texture.
//
// What's HERE in W15:
//   • `createWebgl2Context()`     — guarded WebGL2 context init
//   • `compileProgram()`          — VS+FS shader pipeline scaffold
//   • `createTexturedQuadBuffers()` — single-quad vertex layout
//   • `helloWorld()`              — full E2E "render one tile" smoke
//
// What's NOT here in W15 (Phase L wave-by-wave):
//   • Tile / dice / stick / wall mesh graphs.
//   • Multi-pass renderer (lighting, post-fx).
//   • GLTF asset graph (W14 already split `gltf-loader.<hash>.js`
//     and the Phase L renderer can keep using it).
//   • Picking / raycaster.
//   • Animation scheduler.
//
// The full Phase L W1+ work consumes this module via dynamic
// import (so it lives in its own chunk and the autotable-src
// eager bundle never pays for it on the lobby path).  See
// `docs/phase-l-renderer-implementation.md` for the wave plan.

/** Vertex shader source — pass-through position + UVs. */
export const HELLO_VS = `#version 300 es
precision highp float;

in vec3 a_position;
in vec2 a_uv;

uniform mat4 u_model;
uniform mat4 u_viewProj;

out vec2 v_uv;

void main() {
  v_uv = a_uv;
  gl_Position = u_viewProj * u_model * vec4(a_position, 1.0);
}
`;

/** Fragment shader source — single-texture sample. */
export const HELLO_FS = `#version 300 es
precision highp float;

in vec2 v_uv;

uniform sampler2D u_tex;
uniform float u_opacity;

out vec4 fragColor;

void main() {
  vec4 base = texture(u_tex, v_uv);
  fragColor = vec4(base.rgb, base.a * u_opacity);
}
`;

/**
 * Acquire a WebGL2 context with the autotable's canonical
 * pixel-format flags.  Returns null on browsers without WebGL2
 * (we'll fall back to the existing three-renderer chunk during
 * the Phase L migration).
 */
export function createWebgl2Context(canvas: HTMLCanvasElement): WebGL2RenderingContext | null {
  const gl = canvas.getContext('webgl2', {
    alpha: true,
    antialias: true,
    depth: true,
    premultipliedAlpha: false,
    preserveDrawingBuffer: false,
    stencil: false,
    powerPreference: 'high-performance',
  }) as WebGL2RenderingContext | null;
  if (gl === null) return null;
  // Match the three-renderer defaults so the visual regression
  // smoke specs don't trip on a colour-space swap during Phase L
  // migration.
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
  return gl;
}

/**
 * Compile + link a vertex / fragment shader pair into a WebGL
 * program.  Throws on compile / link errors with the GLSL log
 * inline so the dev-tools console points at the offending shader.
 */
export function compileProgram(
  gl: WebGL2RenderingContext,
  vsSource: string,
  fsSource: string,
): WebGLProgram {
  const vs = compileShader(gl, gl.VERTEX_SHADER, vsSource);
  const fs = compileShader(gl, gl.FRAGMENT_SHADER, fsSource);
  const program = gl.createProgram();
  if (program === null) throw new Error('[webgl2] gl.createProgram() returned null');
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const log = gl.getProgramInfoLog(program) ?? '(no log)';
    gl.deleteProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    throw new Error(`[webgl2] link failed: ${log}`);
  }
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  return program;
}

function compileShader(gl: WebGL2RenderingContext, type: GLenum, source: string): WebGLShader {
  const shader = gl.createShader(type);
  if (shader === null) throw new Error('[webgl2] gl.createShader() returned null');
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(shader) ?? '(no log)';
    gl.deleteShader(shader);
    const kind = type === gl.VERTEX_SHADER ? 'vertex' : 'fragment';
    throw new Error(`[webgl2] ${kind} shader compile failed: ${log}`);
  }
  return shader;
}

/**
 * Single-quad vertex buffer set: position (xyz) + UV (st), unit
 * square centred at the origin in the xy plane facing +z.  Index
 * buffer winds counter-clockwise so back-face culling can be
 * enabled later without flipping anything.
 */
export interface QuadBuffers {
  vao: WebGLVertexArrayObject;
  positionBuffer: WebGLBuffer;
  uvBuffer: WebGLBuffer;
  indexBuffer: WebGLBuffer;
  indexCount: number;
}

export function createTexturedQuadBuffers(
  gl: WebGL2RenderingContext,
  program: WebGLProgram,
): QuadBuffers {
  // Unit square in the xy plane, centred at origin.  Tile face-up
  // means the face normal is +z so we wind CCW when viewed from
  // the camera (z+ looking toward origin).
  const positions = new Float32Array([
    -0.5, -0.5, 0.0,
     0.5, -0.5, 0.0,
     0.5,  0.5, 0.0,
    -0.5,  0.5, 0.0,
  ]);
  // UV origin at bottom-left (gl.UNPACK_FLIP_Y_WEBGL=true makes
  // the texture's (0,0) sample the bottom-left of the image,
  // matching the three.js convention so tile labels render
  // upright without a manual flip in shader).
  const uvs = new Float32Array([
    0.0, 0.0,
    1.0, 0.0,
    1.0, 1.0,
    0.0, 1.0,
  ]);
  const indices = new Uint16Array([0, 1, 2, 0, 2, 3]);

  const vao = gl.createVertexArray();
  if (vao === null) throw new Error('[webgl2] gl.createVertexArray() returned null');
  gl.bindVertexArray(vao);

  const positionBuffer = createBuffer(gl, gl.ARRAY_BUFFER, positions);
  const aPosition = gl.getAttribLocation(program, 'a_position');
  if (aPosition < 0) throw new Error('[webgl2] attribute "a_position" not found');
  gl.enableVertexAttribArray(aPosition);
  gl.vertexAttribPointer(aPosition, 3, gl.FLOAT, false, 0, 0);

  const uvBuffer = createBuffer(gl, gl.ARRAY_BUFFER, uvs);
  const aUv = gl.getAttribLocation(program, 'a_uv');
  if (aUv < 0) throw new Error('[webgl2] attribute "a_uv" not found');
  gl.enableVertexAttribArray(aUv);
  gl.vertexAttribPointer(aUv, 2, gl.FLOAT, false, 0, 0);

  const indexBuffer = createBuffer(gl, gl.ELEMENT_ARRAY_BUFFER, indices);

  gl.bindVertexArray(null);
  gl.bindBuffer(gl.ARRAY_BUFFER, null);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, null);

  return { vao, positionBuffer, uvBuffer, indexBuffer, indexCount: indices.length };
}

function createBuffer(
  gl: WebGL2RenderingContext,
  target: GLenum,
  data: ArrayBufferView,
): WebGLBuffer {
  const buf = gl.createBuffer();
  if (buf === null) throw new Error('[webgl2] gl.createBuffer() returned null');
  gl.bindBuffer(target, buf);
  gl.bufferData(target, data, gl.STATIC_DRAW);
  return buf;
}

/**
 * Upload an `HTMLImageElement` (or canvas) as a WebGL2 texture.
 * Configured for mahjong tile textures: nearest-neighbour mag,
 * mipmap-linear min, edge-clamp wrapping.
 */
export function createTexture(
  gl: WebGL2RenderingContext,
  source: TexImageSource,
): WebGLTexture {
  const tex = gl.createTexture();
  if (tex === null) throw new Error('[webgl2] gl.createTexture() returned null');
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
  gl.generateMipmap(gl.TEXTURE_2D);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.bindTexture(gl.TEXTURE_2D, null);
  return tex;
}

// Phase K Wave 16 — math helpers split out into `./math.ts` so the
// tile-mesh + camera + this hello-world all share a single set of
// 4×4 matrix utilities.  Re-exported for callers that already
// depend on `identity4` / `perspective4` from this module.
export { identity4, perspective4 } from './math';
import { identity4, perspective4 } from './math';

/**
 * W15 hello-world: render one textured quad into the supplied
 * canvas.  Returns a disposer that releases the GL handles.
 */
export interface HelloWorldHandle {
  dispose(): void;
  redraw(): void;
}

export function helloWorld(
  canvas: HTMLCanvasElement,
  textureSource: TexImageSource,
): HelloWorldHandle {
  const gl = createWebgl2Context(canvas);
  if (gl === null) {
    throw new Error('[webgl2] WebGL2 not available in this browser');
  }
  const program = compileProgram(gl, HELLO_VS, HELLO_FS);
  const quad = createTexturedQuadBuffers(gl, program);
  const tex = createTexture(gl, textureSource);

  const uModel = gl.getUniformLocation(program, 'u_model');
  const uViewProj = gl.getUniformLocation(program, 'u_viewProj');
  const uTex = gl.getUniformLocation(program, 'u_tex');
  const uOpacity = gl.getUniformLocation(program, 'u_opacity');

  const draw = (): void => {
    const width = canvas.clientWidth | 0;
    const height = canvas.clientHeight | 0;
    if (canvas.width !== width) canvas.width = Math.max(1, width);
    if (canvas.height !== height) canvas.height = Math.max(1, height);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.clearColor(0.05, 0.06, 0.08, 1.0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

    gl.useProgram(program);
    gl.uniformMatrix4fv(uModel, false, identity4());
    const aspect = canvas.width / Math.max(1, canvas.height);
    gl.uniformMatrix4fv(uViewProj, false, perspective4(Math.PI / 4, aspect, 0.1, 100));
    gl.uniform1f(uOpacity, 1.0);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.uniform1i(uTex, 0);

    gl.bindVertexArray(quad.vao);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, quad.indexBuffer);
    gl.drawElements(gl.TRIANGLES, quad.indexCount, gl.UNSIGNED_SHORT, 0);
    gl.bindVertexArray(null);
  };

  draw();

  return {
    redraw: draw,
    dispose(): void {
      gl.deleteBuffer(quad.positionBuffer);
      gl.deleteBuffer(quad.uvBuffer);
      gl.deleteBuffer(quad.indexBuffer);
      gl.deleteVertexArray(quad.vao);
      gl.deleteTexture(tex);
      gl.deleteProgram(program);
    },
  };
}
