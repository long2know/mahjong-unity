import type {
  ActionResult, ClearReason, Frame, Layout, Presentation, RendererAction, Viewport,
} from './renderer-contract';

export interface HostHooks {
  action(action: RendererAction): void;
  resized(viewport: Viewport): void;
  resumed(): void;
  failure(): void;
  notice(message: string): void;
  resumeGameplay?(): boolean;
}

export type HostState = 'idle' | 'loading' | 'awaiting-ready' | 'ready' | 'unavailable' | 'unsupported' | 'failed' | 'disposed';

export interface RendererHost {
  readonly instanceId: string;
  readonly canvas: HTMLCanvasElement;
  readonly state: HostState;
  readonly viewport: Viewport | null;
  mount(): Promise<void>;
  clear(sessionId: string, reason: ClearReason): void;
  publish(layout: Layout, frame: Frame): void;
  actionResult(result: ActionResult): void;
  cancelInput(): void;
  setPresentation(presentation: Presentation): void;
  unavailable(message: string): void;
  dispose(): Promise<void>;
}
