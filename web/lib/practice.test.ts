import { describe, expect, it } from "vitest";
import { describeDue, rateWord, stepLabel } from "./practice";

describe("rateWord", () => {
  it("rates by accuracy, and omissions as missed", () => {
    expect(rateWord(95, "None")).toBe("good");
    expect(rateWord(80, "None")).toBe("good");
    expect(rateWord(79.9, "Mispronunciation")).toBe("fair");
    expect(rateWord(60, "None")).toBe("fair");
    expect(rateWord(42, "Mispronunciation")).toBe("poor");
    expect(rateWord(null, "Omission")).toBe("missed");
    expect(rateWord(90, "Omission")).toBe("missed");
  });
});

describe("describeDue", () => {
  const now = new Date("2026-10-29T12:00:00Z");
  const after = (ms: number) => new Date(now.getTime() + ms).toISOString();

  it("describes the time until an item is due", () => {
    expect(describeDue(null, now)).toBe("mastered");
    expect(describeDue(after(-1000), now)).toBe("now");
    expect(describeDue(after(60_000), now)).toBe("in 1 minute");
    expect(describeDue(after(45 * 60_000), now)).toBe("in 45 minutes");
    expect(describeDue(after(3 * 3_600_000), now)).toBe("in 3 hours");
    expect(describeDue(after(24 * 3_600_000), now)).toBe("tomorrow");
    expect(describeDue(after(7 * 86_400_000), now)).toBe("in 7 days");
  });
});

describe("stepLabel", () => {
  it("counts boxes from one", () => {
    expect(stepLabel(0)).toBe("Step 1 of 5");
    expect(stepLabel(4)).toBe("Step 5 of 5");
  });
});
