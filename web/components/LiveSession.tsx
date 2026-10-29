"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, streamTurn, TURN_LIMITS, type Session, type TurnPayload } from "@/lib/api";
import { SentenceBuffer, SpeechEngine } from "@/lib/speech";
import { scenarioTitle } from "@/lib/format";
import {
  acceptsSpokenTurn,
  acceptsTypedTurn,
  isLive,
  nextPhase,
  type Phase,
  type PhaseEvent,
} from "@/lib/session-machine";

interface Line {
  key: string;
  role: "user" | "tutor";
  text: string;
}

// Extra silence, on top of the recognizer's own phrase boundary, before the turn is sent.
const PAUSE_OPTIONS = [
  { label: "Short", ms: 700 },
  { label: "Normal", ms: 1500 },
  { label: "Long", ms: 3000 },
];
const PAUSE_STORAGE_KEY = "tutorai.turnPauseMs";
// The API refuses turns past these; a long monologue is sent early instead (leaves room for one more phrase).
const SEND_EARLY_CHARS = TURN_LIMITS.maxChars - 400;
const SEND_EARLY_PHRASES = TURN_LIMITS.maxPhrases - 5;
// The API's codes for a session that can't take more turns; the page ends and scores it.
const SESSION_OVER_CODES = ["time_up", "turn_limit"];

export default function LiveSession({ id }: { id: string }) {
  const router = useRouter();
  const [session, setSession] = useState<Session | null>(null);
  const [phase, setPhaseState] = useState<Phase>("loading");
  const [lines, setLines] = useState<Line[]>([]);
  const [draft, setDraft] = useState("");
  const [interim, setInterim] = useState("");
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [voiceOff, setVoiceOff] = useState(false);
  const [pauseMs, setPauseMs] = useState(1500);
  const [elapsed, setElapsed] = useState(0);

  const phaseRef = useRef<Phase>("loading");
  const engineRef = useRef<SpeechEngine | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingRef = useRef<{ texts: string[]; phrases: unknown[]; startedAt: number | null }>({
    texts: [],
    phrases: [],
    startedAt: null,
  });
  const pauseRef = useRef(pauseMs);
  const transcriptRef = useRef<HTMLDivElement>(null);

  // Applied to the ref and the state together: the speech callbacks read the phase synchronously.
  const transition = useCallback((event: PhaseEvent) => {
    const next = nextPhase(phaseRef.current, event);
    phaseRef.current = next;
    setPhaseState(next);
    return next;
  }, []);

  const clearTurnTimer = () => {
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = null;
  };

  const runTutorTurn = useCallback(
    async (payload: TurnPayload | null) => {
      const engine = engineRef.current;
      if (transition({ type: "tutorTurn" }) !== "tutor") return;
      engine?.setListening(false);

      const controller = new AbortController();
      abortRef.current = controller;
      const key = `tutor-${Date.now()}`;
      setLines((all) => [...all, { key, role: "tutor", text: "" }]);
      const sentences = new SentenceBuffer((sentence) => engine?.speak(sentence));

      try {
        await streamTurn(
          id,
          payload,
          (delta) => {
            setLines((all) => all.map((line) => (line.key === key ? { ...line, text: line.text + delta } : line)));
            sentences.push(delta);
          },
          controller.signal,
        );
        sentences.flush();
        await engine?.whenSpeechDone();
      } catch (e) {
        if (e instanceof ApiError && e.code && SESSION_OVER_CODES.includes(e.code)) {
          setLines((all) => all.filter((line) => line.key !== key));
          setError(`${e.message} Scoring the session now.`);
          void handlersRef.current.end();
          return;
        }
        if (!controller.signal.aborted) {
          setError(e instanceof Error ? e.message : "The tutor could not reply.");
        }
      }

      setLines((all) => all.filter((line) => line.key !== key || line.text.trim()));
      if (abortRef.current === controller) {
        abortRef.current = null;
        const before = phaseRef.current;
        if (transition({ type: "tutorFinished" }) !== before) {
          engineRef.current?.setListening(true);
        }
      }
    },
    [id, transition],
  );

  const sendTurn = useCallback(
    (text: string, phrases: unknown[], durationMs?: number) => {
      clearTurnTimer();
      pendingRef.current = { texts: [], phrases: [], startedAt: null };
      setDraft("");
      setInterim("");
      setLines((all) => [...all, { key: `user-${Date.now()}`, role: "user", text }]);
      void runTutorTurn({ text, durationMs, phrases: phrases.length ? phrases : undefined });
    },
    [runTutorTurn],
  );

  const finalizeSpokenTurn = useCallback(() => {
    const { texts, phrases, startedAt } = pendingRef.current;
    if (texts.length === 0 || !acceptsSpokenTurn(phaseRef.current)) return;
    sendTurn(texts.join(" "), phrases, startedAt ? Date.now() - startedAt : undefined);
  }, [sendTurn]);

  // The speech callbacks outlive renders, so they reach the current handlers through a ref.
  const handlersRef = useRef({ finalizeSpokenTurn, end: async () => {} });
  useEffect(() => {
    handlersRef.current = { finalizeSpokenTurn, end };
    pauseRef.current = pauseMs;
  });

  useEffect(() => {
    let cancelled = false;
    api
      .getSession(id)
      .then((detail) => {
        if (cancelled) return;
        if (detail.session.status === "Ended") {
          router.replace(`/session/${id}/summary`);
          return;
        }
        setSession(detail.session);
        setLines(detail.turns.map((t) => ({ key: `turn-${t.id}`, role: t.role, text: t.text })));
        const stored = Number(window.localStorage.getItem(PAUSE_STORAGE_KEY));
        if (PAUSE_OPTIONS.some((o) => o.ms === stored)) setPauseMs(stored);
        transition({ type: "loaded" });
      })
      .catch((e: Error) => !cancelled && setError(e.message));

    return () => {
      cancelled = true;
      clearTurnTimer();
      abortRef.current?.abort();
      engineRef.current?.dispose();
      engineRef.current = null;
    };
  }, [id, router, transition]);

  useEffect(() => {
    if (!session) return;
    const started = new Date(session.startedAt).getTime();
    const tick = () => setElapsed(Math.max(0, Math.floor((Date.now() - started) / 1000)));
    tick();
    const interval = setInterval(tick, 1000);
    return () => clearInterval(interval);
  }, [session]);

  useEffect(() => {
    transcriptRef.current?.scrollTo({ top: transcriptRef.current.scrollHeight, behavior: "smooth" });
  }, [lines, draft, interim]);

  async function start() {
    transition({ type: "start" });
    setError(null);
    try {
      engineRef.current = await SpeechEngine.create({
        onInterim: (text) => {
          clearTurnTimer();
          pendingRef.current.startedAt ??= Date.now();
          setInterim(text);
        },
        onPhrase: (text, detail) => {
          const pending = pendingRef.current;
          pending.startedAt ??= Date.now();
          pending.texts.push(text);
          if (detail) pending.phrases.push(detail);
          const joined = pending.texts.join(" ");
          setDraft(joined);
          setInterim("");
          clearTurnTimer();
          if (joined.length >= SEND_EARLY_CHARS || pending.phrases.length >= SEND_EARLY_PHRASES) {
            handlersRef.current.finalizeSpokenTurn();
            return;
          }
          timerRef.current = setTimeout(() => handlersRef.current.finalizeSpokenTurn(), pauseRef.current);
        },
        onError: setError,
      }, id);
    } catch (e) {
      setVoiceOff(true);
      setError(
        `Voice is unavailable (${e instanceof Error ? e.message : "unknown error"}). You can still type your turns, but fluency and pronunciation will not be measured.`,
      );
    }

    if (lines.length === 0) {
      await runTutorTurn(null);
    } else {
      transition({ type: "listen" });
      engineRef.current?.setListening(true);
    }
  }

  function interrupt() {
    abortRef.current?.abort();
    engineRef.current?.stopSpeaking();
  }

  function togglePause() {
    if (phase === "listening") {
      clearTurnTimer();
      engineRef.current?.setListening(false);
      transition({ type: "pause" });
    } else if (phase === "paused") {
      transition({ type: "resume" });
      engineRef.current?.setListening(true);
      if (pendingRef.current.texts.length > 0) {
        timerRef.current = setTimeout(() => handlersRef.current.finalizeSpokenTurn(), pauseRef.current);
      }
    }
  }

  function submitTyped(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    const text = typed.trim();
    if (!text || !acceptsTypedTurn(phase)) return;
    setTyped("");
    transition({ type: "resume" });
    sendTurn(text, []);
  }

  async function end() {
    clearTurnTimer();
    abortRef.current?.abort();
    engineRef.current?.dispose();
    engineRef.current = null;
    transition({ type: "end" });
    try {
      await api.endSession(id);
      router.push(`/session/${id}/summary`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not end the session.");
      router.push(`/session/${id}/summary`);
    }
  }

  function changePause(ms: number) {
    setPauseMs(ms);
    window.localStorage.setItem(PAUSE_STORAGE_KEY, String(ms));
  }

  const planned = (session?.scenario.durationMinutes ?? 10) * 60;
  const live = isLive(phase);
  const status = {
    loading: "Loading…",
    ready: lines.length ? "Ready to continue" : "Ready when you are",
    starting: "Connecting microphone…",
    tutor: "Tutor is speaking",
    listening: voiceOff ? "Your turn — type below" : "Listening — your turn",
    paused: "Paused",
    ending: "Ending the session…",
  }[phase];

  return (
    <main className="shell session">
      <header className="session-head">
        <div>
          <Link href="/" className="back">
            ← Sessions
          </Link>
          <h1>{session ? scenarioTitle(session) : "Session"}</h1>
        </div>
        <div className={`clock ${elapsed >= planned ? "over" : ""}`}>
          {formatClock(elapsed)} <span>/ {formatClock(planned)}</span>
        </div>
      </header>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      <div className="transcript" ref={transcriptRef}>
        {lines.length === 0 && phase === "ready" && (
          <p className="hint">
            Press Start and allow the microphone. The tutor speaks first; answer out loud and pause when you are done.
            Headphones avoid the tutor&apos;s voice being picked up by your microphone.
          </p>
        )}
        {lines.map((line) => (
          <div key={line.key} className={`line ${line.role}`}>
            <span className="who">{line.role === "tutor" ? "Tutor" : "You"}</span>
            <p>{line.text || "…"}</p>
          </div>
        ))}
        {(draft || interim) && (
          <div className="line user draft">
            <span className="who">You</span>
            <p>
              {draft} <em>{interim}</em>
            </p>
          </div>
        )}
      </div>

      <footer className="controls">
        <div className={`status ${phase}`}>
          <span className="dot" />
          {status}
        </div>

        {live && (
          <form className="typed" onSubmit={submitTyped}>
            <input
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              placeholder="Or type your turn…"
              maxLength={TURN_LIMITS.maxChars}
              disabled={phase === "tutor"}
              aria-label="Type your turn"
            />
            <button type="submit" className="ghost" disabled={phase === "tutor" || !typed.trim()}>
              Send
            </button>
          </form>
        )}

        <div className="buttons">
          {phase === "ready" && (
            <button className="primary" onClick={start}>
              {lines.length ? "Resume" : "Start"}
            </button>
          )}
          {phase === "tutor" && (
            <button className="ghost" onClick={interrupt}>
              Interrupt
            </button>
          )}
          {phase === "listening" && draft && (
            <button className="ghost" onClick={finalizeSpokenTurn}>
              I&apos;m done
            </button>
          )}
          {(phase === "listening" || phase === "paused") && !voiceOff && (
            <button className="ghost" onClick={togglePause}>
              {phase === "paused" ? "Resume mic" : "Pause mic"}
            </button>
          )}
          {live && (
            <label className="pause-select">
              Wait before replying
              <select value={pauseMs} onChange={(e) => changePause(Number(e.target.value))}>
                {PAUSE_OPTIONS.map((o) => (
                  <option key={o.ms} value={o.ms}>
                    {o.label}
                  </option>
                ))}
              </select>
            </label>
          )}
          {(live || (phase === "ready" && lines.length > 0)) && (
            <button className="danger" onClick={end}>
              End &amp; score
            </button>
          )}
        </div>
      </footer>
    </main>
  );
}

function formatClock(totalSeconds: number) {
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}
