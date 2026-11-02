import { assertDto, CONTRACT_VERSION, type Viewport } from './renderer-contract';

export interface ViewportMeasurement {
  width: number;
  height: number;
  dpr: number;
  insets: Viewport['insetsCssPx'];
  separateHand: boolean;
}

export function rendererViewport(instanceId: string, revision: number, input: ViewportMeasurement): Viewport {
  const renderPixelRatio = Math.min(input.dpr, 2);
  const usableHeight = Math.max(0, input.height - input.insets.top - input.insets.bottom);
  const value: Viewport = {
    contractVersion: CONTRACT_VERSION, instanceId, type: 'viewport', resizeRevision: revision,
    widthCssPx: input.width, heightCssPx: input.height, devicePixelRatio: input.dpr, renderPixelRatio,
    widthPx: Math.max(1, Math.round(input.width * renderPixelRatio)),
    heightPx: Math.max(1, Math.round(input.height * renderPixelRatio)),
    insetsCssPx: { ...input.insets },
    handBandCssPx: input.separateHand ? Math.min(180, usableHeight * 0.3, Math.max(148, input.width * 0.24)) : 0,
  };
  assertDto<Viewport>('Viewport', value);
  return value;
}
