import { WebglHost } from '../../src/frontend/autotable-src/src/renderer/webgl-host';
import type { Frame, Layout } from '../../src/frontend/autotable-src/src/renderer/renderer-contract';

declare const __PACING_SOURCE__: string;
declare global {
  interface Window { pacingFixture: { host: WebglHost; source: string; framesProduced: number; stop(): void } }
}

const snapshot = await (await fetch('./snapshot.json')).json() as { layout: Layout; frame: Frame };
const host = new WebglHost(document.getElementById('main')!, {
  action() {}, resized() {}, resumed() {}, notice() {},
  failure() { throw Error('Controlled renderer failed'); },
});
const sessionId = 'controlled-pacing-session';
const layout = { ...snapshot.layout, instanceId: host.instanceId, sessionId };
const base = { ...snapshot.frame, instanceId: host.instanceId, sessionId };
const moving = base.entities.findIndex(entity => entity.appearance.showFace);
let frameId = 0, animation = 0;
const started = performance.now();
host.clear(sessionId, 'join');
host.setPresentation(base.presentation);
function produce(time: number): void {
  const phase = (time - started) / 1000;
  const frame: Frame = {
    ...base, frameId: ++frameId,
    presentation: { ...base.presentation, lookDown: .3 + .2 * Math.sin(phase * 2), zoom: .05 * Math.cos(phase * 2) },
    entities: base.entities.map((entity, index) => index !== moving ? entity : {
      ...entity, pose: { ...entity.pose, position: {
        ...entity.pose.position, z: entity.pose.position.z + 3 * (1 + Math.sin(phase * 3)),
      } },
    }),
  };
  host.publish(layout, frame);
  window.pacingFixture.framesProduced = frameId;
  animation = requestAnimationFrame(produce);
}
window.pacingFixture = { host, source: __PACING_SOURCE__, framesProduced: 0,
  stop() { cancelAnimationFrame(animation); } };
await host.mount();
animation = requestAnimationFrame(produce);
