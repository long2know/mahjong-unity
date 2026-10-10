export const DEFAULT_BASE_UNIT = 1;
// Creation bound shared with ChangshaBaseUnit.MaxValue; scores stay server-owned.
export const MAX_BASE_UNIT = 11_184_810;

export function parseBaseUnit(value: unknown): number | null {
  if (typeof value === 'string') {
    if (!/^\d+$/.test(value.trim())) return null;
    value = Number(value.trim());
  }
  return typeof value === 'number' && Number.isSafeInteger(value)
    && value >= DEFAULT_BASE_UNIT && value <= MAX_BASE_UNIT ? value : null;
}
