// Phase K Wave 7 — Ambient module declaration for hls.js's light
// build.  The package's `exports` field whitelists `./dist/*` so the
// import path resolves at runtime + at bundle time, but the package's
// TypeScript types only cover the root entry.  We type the light
// build as a re-export of the root types (the constructor surface
// we use — `new(config?: unknown)` + `isSupported()` + the prototype
// methods on `HlsLike` — is identical between the full and light
// builds).

declare module 'hls.js/dist/hls.light.mjs' {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const Hls: any;
  export default Hls;
}
