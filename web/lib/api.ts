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

export type GradingState = "Pending" | "Running" | "Done" | "Failed";

export interface SessionDetail {
  session: Session;
  turns: Turn[];
  assessment: Assessment | null;
  /** state is null when the session was never queued for grading; error is set only when Failed. */
  grading: { state: GradingState | null; error: string | null };
}

export interface SpeechToken {
  token: string;
  region: string;
  locale: string;
  voice: string;
}

export interface CurrentUser {
  username: string;
  role: "Admin" | "Guest";
  /** null when the user has no limit. */
  sessionLimit: number | null;
  sessionsUsed: number;
}

export interface UsageEstimate {
  tokens: number;
  /** From the API's configured prices; unpricedModels lists models it has no price for. */
  estimatedCostUsd: number;
  unpricedModels: string[];
}

export interface AdminUser {
  id: number;
  username: string;
  role: "Admin" | "Guest";
  sessionLimit: number | null;
  sessionsUsed: number;
  isDisabled: boolean;
  createdAt: string;
  /** Claude usage over the last 30 days; only in the user list. */
  usage30Days: UsageEstimate | null;
}

export interface NewUser {
  username: string;
  password: string;
  role: "Admin" | "Guest";
  sessionLimit: number | null;
}

export interface TurnPayload {
  text: string;
  durationMs?: number;
  /** Raw Azure detailed-result objects, one per recognized phrase. */
  phrases?: unknown[];
}

/** Mirrors the API's Sessions limits (appsettings.json); the live page sends a turn before reaching them. */
export const TURN_LIMITS = { maxChars: 4000, maxPhrases: 100 };

/** An API error with the status and, when the API gives one, a machine-readable code such as "time_up". */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** The sign-in expired or is missing: go to the sign-in page and come back here afterwards. */
function redirectToLogin(): never {
  const here = window.location.pathname + window.location.search;
  // A full page load on purpose: nothing from the signed-out state should survive.
  // eslint-disable-next-line @next/next/no-location-assign-relative-destination
  window.location.assign(`/login?next=${encodeURIComponent(here)}`);
  throw new Error("Your sign-in has expired.");
}

export async function signOut(): Promise<void> {
  await fetch("/auth/logout", { method: "POST" });
  // eslint-disable-next-line @next/next/no-location-assign-relative-destination
  window.location.assign("/login");
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  if (response.status === 401) redirectToLogin();
  if (!response.ok) {
    throw await apiError(response);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

async function apiError(response: Response): Promise<ApiError> {
  try {
    const body = await response.json();
    if (typeof body?.error === "string") {
      return new ApiError(body.error, response.status, typeof body.code === "string" ? body.code : null);
    }
  } catch {
    // Not JSON; fall through to the status line.
  }
  const message =
    response.status >= 500
      ? `The API is unavailable (${response.status}). Is TutorAI.Api running?`
      : `Request failed (${response.status}).`;
  return new ApiError(message, response.status, null);
}

export const api = {
  createSession: (mode: Mode, scenario: Scenario) =>
    request<Session>("/api/sessions", { method: "POST", body: JSON.stringify({ mode, scenario }) }),
  listSessions: () => request<SessionSummary[]>("/api/sessions"),
  getSession: (id: string) => request<SessionDetail>(`/api/sessions/${id}`),
  /** The assessment when it's ready (200), otherwise the grading state (202): poll getSession until it's done. */
  endSession: (id: string) =>
    request<Assessment | { state: GradingState }>(`/api/sessions/${id}/end`, { method: "POST" }),
  speechToken: () => request<SpeechToken>("/api/speech/token"),
  me: () => request<CurrentUser>("/api/auth/me"),
};

/** Admin-only user management. */
export const users = {
  list: () => request<AdminUser[]>("/api/users"),
  create: (user: NewUser) => request<AdminUser>("/api/users", { method: "POST", body: JSON.stringify(user) }),
  update: (id: number, changes: { role?: string; sessionLimit?: number | null; isDisabled?: boolean }) =>
    request<AdminUser>(`/api/users/${id}`, { method: "PUT", body: JSON.stringify(changes) }),
  setPassword: (id: number, password: string) =>
    request<void>(`/api/users/${id}/password`, { method: "POST", body: JSON.stringify({ password }) }),
  resetSessions: (id: number) => request<AdminUser>(`/api/users/${id}/reset-sessions`, { method: "POST" }),
  remove: (id: number) => request<void>(`/api/users/${id}`, { method: "DELETE" }),
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
  if (response.status === 401) redirectToLogin();
  if (!response.ok || !response.body) {
    throw await apiError(response);
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
