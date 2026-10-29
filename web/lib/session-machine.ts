/**
 * The live session's phases and the moves between them, kept free of React and the microphone so they can be
 * tested. LiveSession applies `nextPhase` to every event; side effects (microphone, timers, requests) stay there.
 */
export type Phase = "loading" | "ready" | "starting" | "tutor" | "listening" | "paused" | "ending";

export type PhaseEvent =
  /** The session loaded. */
  | { type: "loaded" }
  /** The learner pressed Start. */
  | { type: "start" }
  /** The microphone is set up and the session already has turns: the learner speaks first. */
  | { type: "listen" }
  /** A tutor reply was requested: the opening line, or the answer to a spoken or typed turn. */
  | { type: "tutorTurn" }
  /** The tutor's reply finished, failed or was interrupted. */
  | { type: "tutorFinished" }
  | { type: "pause" }
  | { type: "resume" }
  | { type: "end" };

/** Returns the phase after the event. An event that doesn't apply in the current phase changes nothing. */
export function nextPhase(phase: Phase, event: PhaseEvent): Phase {
  if (phase === "ending") return phase; // Final: the page is on its way to the summary.

  switch (event.type) {
    case "loaded":
      return phase === "loading" ? "ready" : phase;
    case "start":
      return phase === "ready" ? "starting" : phase;
    case "listen":
      return phase === "starting" ? "listening" : phase;
    case "tutorTurn":
      return phase === "starting" || phase === "listening" || phase === "paused" || phase === "tutor" ? "tutor" : phase;
    case "tutorFinished":
      // Only a reply that is still the current activity hands the turn back; after Pause or End it doesn't.
      return phase === "tutor" ? "listening" : phase;
    case "pause":
      return phase === "listening" ? "paused" : phase;
    case "resume":
      return phase === "paused" ? "listening" : phase;
    case "end":
      return "ending";
  }
}

/** A spoken turn is sent only while listening; while paused, recognized phrases wait for Resume. */
export const acceptsSpokenTurn = (phase: Phase) => phase === "listening";

/** A typed turn can be sent while listening or paused (sending it resumes). */
export const acceptsTypedTurn = (phase: Phase) => phase === "listening" || phase === "paused";

/** The session is under way: the clock, Pause and End apply. */
export const isLive = (phase: Phase) => phase === "tutor" || phase === "listening" || phase === "paused";
