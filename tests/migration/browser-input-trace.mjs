import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

export const probeFiles = [
  'tests/migration/browser-prefix.mjs',
  'tests/migration/browser-input-trace.mjs',
  'tests/migration/browser-input-trace.test.mjs',
  'tests/migration/container_proxy_smoke.py',
  'tests/migration/container_smoke.py',
  'tests/migration/runtime_probe.py',
  'tests/migration/test_ghcr_publish.py',
  '.github/workflows/migration-build.yml',
];

export function probeProvenance(root) {
  const probeSourceSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  const hash = data => createHash('sha256').update(data).digest('hex');
  const files = Object.fromEntries(probeFiles.map(file => {
    const actual = hash(fs.readFileSync(path.join(root, file)));
    const committed = hash(execFileSync('git', ['show', `${probeSourceSha}:${file}`], { cwd: root }));
    if (actual !== committed) throw new Error(`Uncommitted probe file: ${file}`);
    return [file, actual];
  }));
  return { probeSourceSha, probeFiles: files };
}

// Serialized by Playwright before application listeners are registered. No application calls are made to sample state.
export function installInputTrace({ capacity = 256 } = {}) {
  const records = [], frames = [], calls = [], eventIds = new WeakMap();
  let attached = null, pickedId = null, serial = 0, dropped = 0, observerErrors = 0;
  let observerMilliseconds = 0, maxObserverMilliseconds = 0;
  const fields = (value, names) => value == null ? null
    : Object.fromEntries(names.map(name => [name, ['string', 'number', 'boolean'].includes(typeof value[name]) ? value[name] : null]));
  const target = value => fields(value, ['kind', 'id']);
  const entity = (frame, id) => {
    const value = frame?.entities?.find(item => item.entityId === id);
    return value ? { entityId: value.entityId, seat: value.seat, group: value.group,
      interaction: fields(value.interaction, ['canDiscard', 'canSelect', 'canDrag', 'canFlip', 'canPickup']),
      display: fields(value.display, ['selected', 'hovered', 'drawn', 'held']),
      showFace: value.appearance?.showFace ?? null } : null;
  };
  const frame = value => value ? {
    ...fields(value, ['instanceId', 'sessionId', 'frameId', 'layoutRevision']),
    session: fields(value.session, ['mode', 'connected', 'snapshotReady', 'actorSeat', 'viewSeat', 'viewerRevision']),
    turn: fields(value.turn, ['activeSeat', 'phase', 'awaitingDiscard']),
    entityCount: value.entities?.length ?? null,
  } : null;
  const viewport = value => value ? {
    ...fields(value, ['resizeRevision', 'widthCssPx', 'heightCssPx', 'widthPx', 'heightPx',
      'devicePixelRatio', 'renderPixelRatio', 'handBandCssPx']),
    insetsCssPx: fields(value.insetsCssPx, ['top', 'right', 'bottom', 'left']),
  } : null;
  const intent = value => value ? { ...fields(value, ['kind', 'entityId', 'triggerEntityId', 'anchorEntityId',
    'gestureId', 'slotName', 'commit', 'direction', 'animated']), entityIds: value.entityIds?.slice(0, 32) ?? null } : null;
  const native = (event, now) => ({
    eventId: eventIds.get(event), type: event.type, eventTimeStamp: event.timeStamp, processedAt: now,
    dispatchAgeMilliseconds: now - event.timeStamp, targetId: event.target?.id ?? null,
    ...fields(event, ['isTrusted', 'detail', 'clickCount', 'pointerId', 'pointerType', 'isPrimary', 'button', 'buttons',
      'altKey', 'ctrlKey', 'metaKey', 'shiftKey', 'defaultPrevented', 'clientX', 'clientY']),
  });
  const state = now => {
    const game = window.game, host = game?.renderer, scene = host?.scene, input = host?.input, camera = game?.tableCamera;
    const contact = input?.contact, click = input?.click, commit = scene?.committed;
    const rect = host?.canvas?.getBoundingClientRect();
    return {
      ...fields(host, ['state', 'instanceId', 'sessionId', 'inputBlocked', 'suspended', 'lastRendered', 'disposed']),
      document: { hidden: document.hidden, focused: document.hasFocus(), activeId: document.activeElement?.id ?? null },
      canvas: fields(rect, ['x', 'y', 'left', 'top', 'width', 'height']),
      contact: contact ? { ...fields(contact, ['pointer', 'x', 'y', 'start', 'touch', 'right', 'moved', 'drag', 'slot']),
        ageMilliseconds: now - contact.start, target: target(contact.target),
        captured: host.canvas.hasPointerCapture(contact.pointer) } : null,
      click: click ? { ...fields(click, ['id', 'at']), ageMilliseconds: now - click.at } : null,
      panning: fields(input?.panning, ['pointer', 'x', 'y']),
      camera: { ...fields(camera, ['blocksGameplay', 'consumed', 'frame']), manipulating: camera?.manipulating ?? null,
        contacts: camera?.contacts ? [...camera.contacts].slice(0, 4).map(([pointer, value]) => ({
          pointer, ...fields(value, ['x', 'y', 'board', 'touch']),
        })) : [],
        gesture: fields(camera?.gesture, ['mouse', 'turn', 'tilt', 'zoom', 'roll', 'rollOnly']),
        pan: scene?.pan?.slice() ?? null,
        presentation: fields(commit?.frame?.presentation, ['viewMode', 'boardLayout', 'handLayout', 'lookDown', 'zoom']),
        view: fields(commit?.frame?.presentation?.camera, ['scale', 'yaw', 'pitch', 'roll']),
        pickingMatrices: scene?.picking?.matrices?.map(matrix => Array.from(matrix)) ?? null },
      committed: frame(commit?.frame), pending: frame(scene?.pending?.commit?.frame), queued: frame(host?.queue?.frame),
      inFlight: host?.inFlight ? { frame: frame(host.inFlight.frame),
        ...fields(host.inFlight, ['viewport', 'started']) } : null,
      viewport: viewport(host?.viewport), committedViewport: viewport(commit?.viewport),
      pendingViewport: viewport(scene?.pending?.commit?.viewport),
      authority: { adapterSessionId: game?.adapter?.sessionId ?? null, actionEpoch: game?.actions?.epoch ?? null,
        latest: frame(game?.adapter?.latest) },
      contactEntity: entity(commit?.frame, contact?.target?.id), clickEntity: entity(commit?.frame, click?.id),
    };
  };
  const observe = (kind, data = {}, withState = false) => {
    const began = performance.now();
    try {
      if (records.length >= capacity) { dropped++; return; }
      const event = window.event;
      const value = { sequence: ++serial, kind, processedAt: began,
        eventId: event && eventIds.get(event) || null, callChain: calls.slice(),
        ...(typeof data === 'function' ? data(began) : data) };
      if (withState) value.state = state(began);
      value.observerMilliseconds = performance.now() - began;
      records.push(value);
    } catch { observerErrors++; }
    finally {
      const cost = performance.now() - began;
      observerMilliseconds += cost; maxObserverMilliseconds = Math.max(maxObserverMilliseconds, cost);
    }
  };
  const wrap = (object, key, label, summarize, withState = false) => {
    const original = object?.[key];
    if (typeof original !== 'function') throw new Error(`Missing trace surface: ${label}`);
    object[key] = function (...args) {
      const calledAt = summarize || withState ? performance.now() : null;
      calls.push(label);
      if (withState) observe(label + ':before', {
        calledAt, reason: label === 'host.clear' ? args[1] ?? null : label === 'input.rejected' ? args[0] ?? null : null,
      }, true);
      try {
        const result = Reflect.apply(original, this, args);
        if (summarize) observe(label + ':return', () => ({ calledAt, ...summarize(args, result) }), withState);
        else if (withState) observe(label + ':return', { calledAt }, true);
        return result;
      } catch (error) {
        observe(label + ':throw', { calledAt, errorName: typeof error?.name === 'string' ? error.name : null }, withState);
        throw error;
      } finally { calls.pop(); }
    };
  };
  const eventTypes = ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'lostpointercapture', 'gotpointercapture',
    'mousedown', 'mouseup', 'click', 'dblclick', 'blur', 'focus', 'visibilitychange', 'resize'];
  for (const type of eventTypes) {
    window.addEventListener(type, event => {
      if (!attached || type.startsWith('pointer') && event.pointerType !== 'mouse') return;
      if (type === 'pointerdown' || type === 'pointerup') pickedId = null;
      eventIds.set(event, ++serial);
      observe('native:before', now => ({ event: native(event, now) }), true);
    }, { capture: true, passive: true });
  }
  window.addEventListener('mahjong:renderer-frame', event => {
    const began = performance.now();
    try {
      const value = { ...fields(event.detail?.receipt, ['frameId', 'layoutRevision', 'sessionId']),
        processedAt: began, drawMilliseconds: event.detail?.drawMilliseconds ?? null };
      if (frames.length === 12) frames.shift();
      frames.push(value);
    } catch { observerErrors++; }
    finally {
      const cost = performance.now() - began;
      observerMilliseconds += cost; maxObserverMilliseconds = Math.max(maxObserverMilliseconds, cost);
    }
  }, { passive: true });
  window.__runtimeInputTrace = {
    attach() {
      const game = window.game, host = game?.renderer, input = host?.input;
      if (attached === input && input) return;
      if (!input) throw new Error('Input trace requires the existing ready input instance.');
      if (attached) throw new Error('Input instance changed during the observed gesture.');
      attached = input;
      const gate = () => ({ ...fields(host, ['state', 'inputBlocked', 'suspended', 'disposed', 'sessionId']),
        viewportRevision: host.viewport?.resizeRevision ?? null,
        committedViewportRevision: host.scene?.committed?.viewport?.resizeRevision ?? null,
        committedSessionId: host.scene?.committed?.frame?.sessionId ?? null });
      wrap(input.hooks, 'frame', 'hooks.frame', (_args, result) => ({
        returnedFrame: frame(result), gate: gate(),
        returnedEntity: entity(result, pickedId ?? input.contact?.target?.id ?? input.click?.id),
      }));
      wrap(input.hooks, 'pick', 'hooks.pick', (args, result) => {
        pickedId = result?.id ?? null;
        return { x: args[0], y: args[1], touch: args[2], gate: gate(),
          returnedTarget: target(result), pickedEntity: entity(host.scene?.committed?.frame, result?.id) };
      });
      wrap(host.scene, 'pick', 'scene.pick', (args, result) => ({ x: args[0], y: args[1], touch: args[2],
        returnedTarget: target(result) }));
      wrap(input.hooks, 'intent', 'hooks.intent', args => ({ intent: intent(args[0]), gate: gate() }));
      for (const key of ['cancel', 'cancelContact', 'rejected']) wrap(input, key, 'input.' + key, null, true);
      for (const key of ['measure', 'publish']) wrap(host, key, 'host.' + key);
      for (const key of ['cancelInput', 'clear']) wrap(host, key, 'host.' + key,
        args => ({ reason: key === 'clear' ? args[1] : null }), true);
      for (const key of ['cancel', 'block', 'resumeGameplay']) wrap(game.tableCamera, key, 'camera.' + key,
        (_args, result) => ({ returned: typeof result === 'boolean' ? result : null }), true);
      wrap(game.actions, 'handle', 'actions.handle', (args, result) => ({
        action: { ...fields(args[0], ['actionId', 'instanceId', 'sessionId', 'contextFrameId']), intent: intent(args[0]?.intent) },
        result: fields(result, ['actionId', 'instanceId', 'sessionId', 'status', 'reason']),
      }));
      for (const type of eventTypes) window.addEventListener(type, event => {
        if (eventIds.has(event)) observe('native:after', now => ({ event: native(event, now) }), true);
      }, { passive: true });
      observe('attached', {}, true);
    },
    mark(name, detail) {
      observe('probe:' + name, fields(detail, ['id', 'x', 'y', 'separate', 'delay']), true);
    },
    snapshot() {
      return { schemaVersion: 1, attached: attached !== null, timeOrigin: performance.timeOrigin,
        capacity, dropped, observerErrors, observerMilliseconds, maxObserverMilliseconds,
        observerCostScope: 'Native/method state sampling and frame-ring updates, excluding wrapper dispatch plumbing; no hot-path serialization.',
        records, recentFrames: frames, sampledAt: performance.now(),
        scope: 'TEST-only passive native-event snapshots and exactly-once pass-through hooks; no extra frame/pick/project/GL calls. '
          + 'No application camera epoch exists: frame/layout/viewport IDs, matrices and presentation are recorded instead.' };
    },
  };
}
