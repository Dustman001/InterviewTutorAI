import { describe, expect, it } from "vitest";
import { acceptsSpokenTurn, acceptsTypedTurn, isLive, nextPhase, type Phase, type PhaseEvent } from "./session-machine";

function run(start: Phase, events: PhaseEvent["type"][]) {
  return events.reduce<Phase>((phase, type) => nextPhase(phase, { type } as PhaseEvent), start);
}

describe("nextPhase", () => {
  it("goes from loading to the tutor's opening line, then to the learner", () => {
    expect(run("loading", ["loaded"])).toBe("ready");
    expect(run("loading", ["loaded", "start"])).toBe("starting");
    expect(run("loading", ["loaded", "start", "tutorTurn"])).toBe("tutor");
    expect(run("loading", ["loaded", "start", "tutorTurn", "tutorFinished"])).toBe("listening");
  });

  it("resumes a session with turns by listening first", () => {
    expect(run("ready", ["start", "listen"])).toBe("listening");
  });

  it("hands the turn back when the learner interrupts the tutor", () => {
    // Interrupting aborts the reply; the reply's end is the tutorFinished event.
    expect(run("tutor", ["tutorFinished"])).toBe("listening");
  });

  it("pauses and resumes only while it's the learner's turn", () => {
    expect(run("listening", ["pause"])).toBe("paused");
    expect(run("listening", ["pause", "resume"])).toBe("listening");
    expect(run("tutor", ["pause"])).toBe("tutor");
    expect(run("ready", ["resume"])).toBe("ready");
  });

  it("holds a spoken turn while paused, until Resume", () => {
    expect(acceptsSpokenTurn("paused")).toBe(false);
    expect(acceptsSpokenTurn(run("paused", ["resume"]))).toBe(true);
  });

  it("sends a typed turn while paused by resuming first", () => {
    expect(acceptsTypedTurn("paused")).toBe(true);
    expect(run("paused", ["resume", "tutorTurn"])).toBe("tutor");
    expect(acceptsTypedTurn("tutor")).toBe(false);
  });

  it("ends from any phase, and nothing leaves ending", () => {
    for (const phase of ["loading", "ready", "starting", "tutor", "listening", "paused"] as Phase[]) {
      expect(nextPhase(phase, { type: "end" })).toBe("ending");
    }
    // End during streaming: the aborted reply finishing afterwards must not hand the turn back.
    expect(run("tutor", ["end", "tutorFinished"])).toBe("ending");
    expect(run("ending", ["tutorTurn", "resume", "loaded"])).toBe("ending");
  });

  it("ignores events that don't apply", () => {
    expect(run("loading", ["start", "tutorTurn", "tutorFinished"])).toBe("loading");
    expect(run("listening", ["loaded", "listen", "tutorFinished"])).toBe("listening");
  });
});

describe("isLive", () => {
  it("is true only while the conversation is running", () => {
    expect((["tutor", "listening", "paused"] as Phase[]).every(isLive)).toBe(true);
    expect((["loading", "ready", "starting", "ending"] as Phase[]).some(isLive)).toBe(false);
  });
});
