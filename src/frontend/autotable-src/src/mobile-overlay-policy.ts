export const COMPACT_VIEW_QUERY = '(max-width: 900px), (max-height: 520px)';
export type MobileInfoPanel = 'none' | 'move-log' | 'chat';

export function mobileInfoPanel(value: unknown): MobileInfoPanel {
  return value === 'move-log' || value === 'chat' ? value : 'none';
}

export function toggleMobilePanel(
  current: MobileInfoPanel, target: Exclude<MobileInfoPanel, 'none'>, open: boolean,
): MobileInfoPanel {
  return open ? target : current === target ? 'none' : current;
}
