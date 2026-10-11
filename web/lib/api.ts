export type Mode = "Conversation" | "Interview";

export interface Scenario {
  topic?: string | null;
  role?: string | null;
  seniority?: string | null;
  interviewType?: string | null;
  jobDescription?: string | null;
  difficulty?: string | null;
  durationMinutes: number;
}

export interface Session {
  id: string;
  mode: Mode;
  scenario: Scenario;
  startedAt: string;
  endedAt: string | null;
  status: "Active" | "Ended";
}

export interface Levels {
  fluency: string | null;
  grammar: string | null;
  vocabulary: string | null;
  interaction: string | null;
  pronunciation: string | null;
  overall: string | null;
}

export interface SessionSummary extends Session {
  levels: Levels | null;
}

export interface Turn {
  id: number;
  seq: number;
  role: "user" | "tutor";
  text: string;
  startedAt: string;
  wordCount: number;
  accuracyScore: number | null;
  fluencyScore: number | null;
  prosodyScore: number | null;
}

export interface DimensionGrade {
  level: string;
  confidence: "low" | "medium" | "high";
  evidence: string[];
  advice: string;
}

export interface Correction {
  original: string;
  corrected: string;
  explanation: string;
}

export interface Assessment {
  sessionId: string;
  sufficient: boolean;
  reason: string | null;
  levels: Levels;
  metrics: {
    fluency: {
      totalWords: number;
      timedWords: number;
      speakingSeconds: number;
      wordsPerMinute: number | null;
      pauseRatio: number | null;
      longPausesPerMinute: number | null;
      meanRunLength: number | null;
      fillerCount: number;
      fillersPer100Words: number | null;
      azureFluencyScore: number | null;
    };
    pronunciation: {
      assessedWords: number;
      accuracy: number | null;
      prosody: number | null;
      composite: number | null;
      weakestWords: { word: string; score: number; count: number }[];
    };
  };
  detail: {
    fluency: DimensionGrade;
    grammar: DimensionGrade;
    vocabulary: DimensionGrade;
    interaction: DimensionGrade;
    summary: string;
    topCorrections: Correction[];
  } | null;
  model: string | null;
  createdAt: string;
}

export interface SessionDetail {
  session: Session;
  turns: Turn[];
  assessment: Assessment | null;
}

export interface SpeechToken {
  token: string;
  region: string;
  locale: string;
  voice: string;
}

export interface TurnPayload {
  text: string;
  durationMs?: number;
  /** Raw Azure detailed-result objects, one per recognized phrase. */
  phrases?: unknown[];
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  if (!response.ok) {
    throw new Error(await errorMessage(response));
  }
  return (await response.json()) as T;
}

async function errorMessage(response: Response): Promise<string> {
  try {
    const body = await response.json();
    if (typeof body?.error === "string") return body.error;
  } catch {
    // Not JSON; fall through to the status line.
  }
  return response.status >= 500
    ? `The API is unavailable (${response.status}). Is TutorAI.Api running?`
    : `Request failed (${response.status}).`;
}

export const api = {
  createSession: (mode: Mode, scenario: Scenario) =>
    request<Session>("/api/sessions", { method: "POST", body: JSON.stringify({ mode, scenario }) }),
  listSessions: () => request<SessionSummary[]>("/api/sessions"),
  getSession: (id: string) => request<SessionDetail>(`/api/sessions/${id}`),
  endSession: (id: string) => request<Assessment>(`/api/sessions/${id}/end`, { method: "POST" }),
  speechToken: () => request<SpeechToken>("/api/speech/token"),
};

/**
 * Posts the learner's turn (or nothing, to get the opening line) and reads the tutor's reply
 * from the server-sent event stream. Resolves with the full reply once the stream is done.
 */
export async function streamTurn(
  sessionId: string,
  payload: TurnPayload | null,
  onDelta: (text: string) => void,
  signal: AbortSignal,
): Promise<string> {
  const response = await fetch(`/api/sessions/${sessionId}/turns`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
    body: JSON.stringify(payload ?? {}),
    signal,
  });
  if (!response.ok || !response.body) {
    throw new Error(await errorMessage(response));
  }

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";
  let reply = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += value;

    let boundary: number;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      let event = "message";
      let data = "";
      for (const line of block.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) data += line.slice(5).trim();
      }
      if (!data) continue;

      const parsed = JSON.parse(data);
      if (event === "delta") {
        reply += parsed.text;
        onDelta(parsed.text);
      } else if (event === "error") {
        throw new Error(parsed.message);
      }
    }
  }
  return reply;
}
