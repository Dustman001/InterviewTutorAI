import { CEFR_LEVELS } from "./format";

/** The position of a CEFR level on the ordered scale (A1 = 0 … C2 = 8), or null when there is no level. */
export function levelIndex(level: string | null | undefined): number | null {
  if (!level) return null;
  const index = CEFR_LEVELS.indexOf(level);
  return index >= 0 ? index : null;
}

/** The level nearest a position on the scale, e.g. an average. */
export function nearestLevel(index: number): string {
  return CEFR_LEVELS[Math.min(CEFR_LEVELS.length - 1, Math.max(0, Math.round(index)))];
}

function mean(values: number[]): number | null {
  return values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
}

export interface RecentChange {
  /** How many sessions each side covers: up to 5, and half the history when there are fewer than 10. */
  window: number;
  recent: number;
  previous: number;
  /** recent - previous, in steps of the scale (A2 to A2+ is one step). */
  change: number;
}

/**
 * Compares the average overall level of the most recent sessions with the same number of sessions before
 * them. `levels` is oldest first. Null when there are fewer than 2 graded sessions.
 */
export function compareRecent(levels: (string | null)[]): RecentChange | null {
  const indexes = levels.map(levelIndex).filter((i): i is number => i !== null);
  const window = Math.min(5, Math.floor(indexes.length / 2));
  if (window < 1) return null;
  const recent = mean(indexes.slice(-window))!;
  const previous = mean(indexes.slice(-2 * window, -window))!;
  return { window, recent, previous, change: recent - previous };
}

/** One sentence for the top of the progress page. */
export function describeChange(result: RecentChange): string {
  const sessions = result.window === 1 ? "session" : `${result.window} sessions`;
  const now = nearestLevel(result.recent);
  const before = nearestLevel(result.previous);
  const steps = Math.abs(result.change);
  if (steps < 0.5) {
    return `Your last ${sessions} average ${now}, level with the ${sessions} before.`;
  }
  const amount = `${Math.round(steps * 10) / 10} ${Math.round(steps * 10) / 10 === 1 ? "step" : "steps"}`;
  const direction = result.change > 0 ? "up" : "down";
  return `Your last ${sessions} average ${now}, ${direction} ${amount} from ${before} in the ${sessions} before.`;
}

/** Round axis bounds and up to five tick values for a numeric chart. */
export function niceScale(values: number[], floor = 0): { min: number; max: number; ticks: number[] } {
  const present = values.filter((v) => Number.isFinite(v));
  const high = present.length ? Math.max(...present) : 1;
  const low = Math.min(floor, ...present);
  const span = Math.max(high - low, 1);
  const rough = span / 4;
  const magnitude = 10 ** Math.floor(Math.log10(rough));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= rough)!;
  const min = Math.floor(low / step) * step;
  const max = Math.ceil(high / step) * step;
  const ticks: number[] = [];
  for (let t = min; t <= max + step / 1000; t += step) ticks.push(Math.round(t * 1000) / 1000);
  return { min, max, ticks };
}
