import type { Drill, PrepAction, PrepRun } from "./api";

/** What one of the agent's actions means for the learner, in plain words. */
export interface ActionView {
  title: string;
  /** Extra lines, e.g. the phrases added. */
  details: string[];
  /** Waiting for the learner to confirm or reject. */
  pending: boolean;
}

function asArray(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.filter((v): v is Record<string, unknown> => typeof v === "object" && v !== null) : [];
}

function text(value: unknown): string {
  return typeof value === "string" ? value : "";
}

export function describeAction(action: PrepAction): ActionView {
  const pending = action.status === "PendingConfirmation";
  switch (action.tool) {
    case "add_practice_items": {
      const added = asArray(action.result?.added).map((a) => text(a.target));
      const skipped = asArray(action.result?.skippedAlreadyPresent).map((s) => text(s.target));
      return {
        title:
          added.length === 0
            ? "No new practice phrases (you already had them)"
            : `Added ${added.length} phrase${added.length === 1 ? "" : "s"} to your practice list`,
        details: [...added, ...skipped.map((s) => `Already on your list: ${s}`)],
        pending,
      };
    }
    case "schedule_drill": {
      const minutes = Number(action.result?.durationMinutes ?? action.args.durationMinutes);
      const type = text(action.result?.interviewType ?? action.args.interviewType) || "mixed";
      const when = text(action.result?.scheduledFor ?? action.args.scheduledFor);
      return { title: `Scheduled a ${minutes}-minute ${type} drill for ${formatWhen(when)}`, details: [], pending };
    }
    case "restart_practice_item": {
      const target = text(action.args.target);
      const progress = action.args.mastered === true ? "mastered" : `on step ${Number(action.args.step) + 1} of 5`;
      const title =
        action.status === "Confirmed"
          ? `Restarted “${target}” from step 1`
          : action.status === "Rejected"
            ? `Kept “${target}” as it was`
            : `Restart “${target}” from step 1?`;
      return {
        title,
        details: [
          ...(text(action.args.reason) ? [text(action.args.reason)] : []),
          ...(pending ? [`It is ${progress}; restarting discards that progress and can't be undone.`] : []),
        ],
        pending,
      };
    }
    default:
      return { title: action.tool, details: [], pending };
  }
}

/** "Tue 3 Nov, 15:30" in the viewer's time zone; the raw value when it isn't a date. */
export function formatWhen(iso: string, locale?: string): string {
  const date = new Date(iso);
  if (!iso || Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(locale, { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/** A line under the run's status, or null for a run that completed normally. */
export function runNotice(run: PrepRun): string | null {
  switch (run.status) {
    case "Running":
      return "Still planning. Refresh in a moment.";
    case "Stopped":
      return `${run.stopReason ?? "Stopped at a limit."} What it did before stopping is below.`;
    case "Failed":
      return run.stopReason ?? "The agent failed. Try again later.";
    default:
      return null;
  }
}

/** Scheduled drills first, soonest first; then the rest. */
export function sortDrills(drills: Drill[]): Drill[] {
  return [...drills].sort((a, b) => {
    const rank = (d: Drill) => (d.status === "Scheduled" ? 0 : 1);
    return rank(a) - rank(b) || a.scheduledFor.localeCompare(b.scheduledFor);
  });
}

/** A fresh Idempotency-Key for one submission, reused if the same submission is retried. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}
