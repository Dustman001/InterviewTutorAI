import { describe, expect, it } from "vitest";
import { compareRecent, describeChange, levelIndex, nearestLevel, niceScale } from "./progress";

describe("levelIndex", () => {
  it("orders the CEFR levels as steps, plus levels included", () => {
    expect(["A1", "A2", "A2+", "B1", "B1+", "B2", "B2+", "C1", "C2"].map(levelIndex)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8]);
  });

  it("has no position for a missing or unknown level", () => {
    expect([null, undefined, "", "D4"].map(levelIndex)).toEqual([null, null, null, null]);
  });

  it("maps a position back to the nearest level, clamped to the scale", () => {
    expect([3.4, 3.5, -2, 12].map(nearestLevel)).toEqual(["B1", "B1+", "A1", "C2"]);
  });
});

describe("compareRecent", () => {
  it("compares the last five sessions with the five before", () => {
    const levels = ["A1", "A1", "A2", "A2", "A2", "A2", "B1", "B1", "B1", "B1", "B1"];
    // The last five are all B1 (3). The five before them are A1, A2, A2, A2, A2: (0 + 1 + 1 + 1 + 1) / 5.
    const result = compareRecent(levels)!;
    expect(result.window).toBe(5);
    expect(result.recent).toBeCloseTo(3);
    expect(result.previous).toBeCloseTo(0.8);
    expect(result.change).toBeCloseTo(2.2);
  });

  it("uses half the history when there are fewer than ten graded sessions", () => {
    expect(compareRecent(["A2", "B1", "B1+"])).toEqual({ window: 1, recent: 4, previous: 3, change: 1 });
    expect(compareRecent(["B1", null, "B2", "B2"])?.window).toBe(1);
  });

  it("needs at least two graded sessions", () => {
    expect(compareRecent([])).toBeNull();
    expect(compareRecent(["B1", null])).toBeNull();
  });
});

describe("describeChange", () => {
  it("says which way the level moved, and by how many steps", () => {
    expect(describeChange({ window: 5, recent: 4, previous: 3, change: 1 })).toBe(
      "Your last 5 sessions average B1+, up 1 step from B1 in the 5 sessions before.",
    );
    expect(describeChange({ window: 1, recent: 2, previous: 3.5, change: -1.5 })).toBe(
      "Your last session average A2+, down 1.5 steps from B1+ in the session before.",
    );
    expect(describeChange({ window: 3, recent: 3.2, previous: 3, change: 0.2 })).toBe(
      "Your last 3 sessions average B1, level with the 3 sessions before.",
    );
  });
});

describe("niceScale", () => {
  it("rounds the axis out to clean ticks from zero", () => {
    // About four intervals over 0-112: steps of 50.
    expect(niceScale([85.5, 101, 112.25])).toEqual({ min: 0, max: 150, ticks: [0, 50, 100, 150] });
    expect(niceScale([62, 78.5, 91])).toEqual({ min: 0, max: 100, ticks: [0, 25, 50, 75, 100] });
  });

  it("copes with no values", () => {
    expect(niceScale([])).toEqual({ min: 0, max: 1, ticks: [0, 0.25, 0.5, 0.75, 1] });
  });
});
