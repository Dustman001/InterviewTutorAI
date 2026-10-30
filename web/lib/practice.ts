import type { PracticeKind } from "./api";

/** Mirrors the API's PracticeSchedule.PassScore. */
export const PASS_SCORE = 80;

export type WordRating = "good" | "fair" | "poor" | "missed";

/** How a read word is shown: omitted words are "missed"; otherwise by accuracy. */
export function rateWord(accuracy: number | null, errorType: string): WordRating {
  if (errorType === "Omission" || accuracy === null) return "missed";
  if (accuracy >= PASS_SCORE) return "good";
  if (accuracy >= 60) return "fair";
  return "poor";
}

export const RATING_LABELS: Record<WordRating, string> = {
  good: "clear",
  fair: "nearly",
  poor: "unclear",
  missed: "left out",
};

/** When the next item falls due, relative to now: "in 5 minutes", "tomorrow", "in 3 days". */
export function describeDue(dueAt: string | null, now: Date = new Date()): string {
  if (!dueAt) return "mastered";
  const ms = new Date(dueAt).getTime() - now.getTime();
  if (ms <= 0) return "now";
  const minutes = Math.ceil(ms / 60_000);
  if (minutes < 60) return `in ${minutes} minute${minutes === 1 ? "" : "s"}`;
  const hours = Math.round(ms / 3_600_000);
  if (hours < 20) return `in ${hours} hour${hours === 1 ? "" : "s"}`;
  const days = Math.round(ms / 86_400_000);
  return days <= 1 ? "tomorrow" : `in ${days} days`;
}

/** The step an item is on, for "Step 2 of 5". Box 0-4; mastered after passing in box 4. */
export function stepLabel(box: number): string {
  return `Step ${box + 1} of 5`;
}

export const KIND_LABELS: Record<PracticeKind, string> = {
  correction: "Correction",
  word: "Word",
  phrase: "Interview phrase",
};
