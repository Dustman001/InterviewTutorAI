import { describe, expect, it } from "vitest";
import type { Drill, PrepAction, PrepRun } from "./api";
import { describeAction, formatWhen, runNotice, sortDrills } from "./prep";

function action(partial: Partial<PrepAction>): PrepAction {
  return { id: 1, tool: "", status: "Applied", args: {}, result: null, createdAt: "", decidedAt: null, ...partial };
}

describe("describeAction", () => {
  it("lists the phrases added and the ones already present", () => {
    const view = describeAction(
      action({
        tool: "add_practice_items",
        result: { added: [{ id: 3, target: "I led the reviews." }], skippedAlreadyPresent: [{ id: 1, target: "trade-offs" }] },
      }),
    );
    expect(view.title).toBe("Added 1 phrase to your practice list");
    expect(view.details).toEqual(["I led the reviews.", "Already on your list: trade-offs"]);
    expect(view.pending).toBe(false);
  });

  it("says so when nothing new was added", () => {
    expect(describeAction(action({ tool: "add_practice_items", result: { added: [] } })).title).toBe(
      "No new practice phrases (you already had them)",
    );
  });

  it("describes a drill from its result", () => {
    const view = describeAction(
      action({ tool: "schedule_drill", result: { durationMinutes: 15, interviewType: "technical", scheduledFor: "not a date" } }),
    );
    expect(view.title).toBe("Scheduled a 15-minute technical drill for not a date");
  });

  it("warns that a pending restart discards progress", () => {
    const view = describeAction(
      action({
        tool: "restart_practice_item",
        status: "PendingConfirmation",
        args: { target: "throughput", step: 4, mastered: true, reason: "Needed for this job." },
      }),
    );
    expect(view.pending).toBe(true);
    expect(view.title).toBe("Restart “throughput” from step 1?");
    expect(view.details).toEqual(["Needed for this job.", "It is mastered; restarting discards that progress and can't be undone."]);
  });

  it("reports a decided restart without the warning", () => {
    const view = describeAction(action({ tool: "restart_practice_item", status: "Rejected", args: { target: "x", step: 2 } }));
    expect(view.title).toBe("Kept “x” as it was");
    expect(view.details).toEqual([]);
  });
});

describe("formatWhen", () => {
  it("returns the raw value when it isn't a date", () => {
    expect(formatWhen("")).toBe("");
    expect(formatWhen("soon")).toBe("soon");
  });

  it("formats a date", () => {
    expect(formatWhen("2026-11-03T15:30:00Z", "en-GB")).toMatch(/3 Nov/);
  });
});

describe("runNotice", () => {
  const run = { status: "Completed", stopReason: null } as PrepRun;
  it("is null for a completed run and explains a stopped one", () => {
    expect(runNotice(run)).toBeNull();
    expect(runNotice({ ...run, status: "Stopped", stopReason: "Stopped at the limit of 8 model calls per run." })).toBe(
      "Stopped at the limit of 8 model calls per run. What it did before stopping is below.",
    );
  });
});

describe("sortDrills", () => {
  it("puts scheduled drills first, soonest first", () => {
    const drill = (id: string, status: Drill["status"], scheduledFor: string) =>
      ({ id, status, scheduledFor }) as Drill;
    const sorted = sortDrills([
      drill("a", "Started", "2026-11-01T00:00:00Z"),
      drill("b", "Scheduled", "2026-11-05T00:00:00Z"),
      drill("c", "Scheduled", "2026-11-03T00:00:00Z"),
    ]);
    expect(sorted.map((d) => d.id)).toEqual(["c", "b", "a"]);
  });
});
