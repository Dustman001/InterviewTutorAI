"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { practice, type PracticeAttempt, type PracticeDue, type PracticeItem } from "@/lib/api";
import { describeDue, KIND_LABELS, RATING_LABELS, rateWord, stepLabel, type WordRating } from "@/lib/practice";
import type { ReadAloud } from "@/lib/read-aloud";

type Phase = "idle" | "playing" | "recording" | "scoring";

export default function Practice() {
  const [due, setDue] = useState<PracticeDue | null>(null);
  const [index, setIndex] = useState(0);
  const [phase, setPhase] = useState<Phase>("idle");
  const [attempt, setAttempt] = useState<PracticeAttempt | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const speechRef = useRef<{ locale: string | null; engine: ReadAloud } | null>(null);

  const load = useCallback(async () => {
    try {
      setDue(await practice.due());
      setIndex(0);
      setAttempt(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not load your practice items.");
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    practice
      .due()
      .then((result) => !cancelled && setDue(result))
      .catch((e: Error) => !cancelled && setError(e.message));
    return () => {
      cancelled = true;
      speechRef.current?.engine.dispose();
      speechRef.current = null;
    };
  }, []);

  const item: PracticeItem | undefined = due?.items[index];

  // After an attempt or a removal, take the counts from the server rather than guessing them here.
  async function refreshCounts() {
    try {
      const counts = await practice.due(0);
      setDue((current) => current && { ...counts, items: current.items });
    } catch {
      // The counts are a convenience; the queue still works.
    }
  }

  async function engineFor(locale: string | null): Promise<ReadAloud> {
    if (speechRef.current?.locale === locale) return speechRef.current.engine;
    speechRef.current?.engine.dispose();
    const { ReadAloud } = await import("@/lib/read-aloud");
    const engine = await ReadAloud.create(locale);
    speechRef.current = { locale, engine };
    return engine;
  }

  async function listen() {
    if (!item) return;
    setError(null);
    setPhase("playing");
    try {
      await (await engineFor(item.locale)).listen(item.target);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not play the text.");
    } finally {
      setPhase("idle");
    }
  }

  async function read() {
    if (!item) return;
    setError(null);
    setNotice(null);
    setAttempt(null);
    setPhase("recording");
    try {
      const phrases = await (await engineFor(item.locale)).record(item.target);
      if (phrases.length === 0) {
        setNotice("The text wasn't heard. Check your microphone and read it aloud.");
        return;
      }
      setPhase("scoring");
      const result = await practice.attempt(item.id, phrases);
      setAttempt(result);
      void refreshCounts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not score the reading.");
    } finally {
      setPhase("idle");
    }
  }

  function next() {
    setAttempt(null);
    setNotice(null);
    setError(null);
    if (due && index + 1 < due.items.length) setIndex(index + 1);
    else void load();
  }

  async function remove() {
    if (!item) return;
    try {
      await practice.remove(item.id);
      setDue((current) => current && { ...current, items: current.items.filter((i) => i.id !== item.id) });
      setAttempt(null);
      setNotice(null);
      void refreshCounts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not remove the item.");
    }
  }

  const busy = phase !== "idle";

  return (
    <main className="shell">
      <Link href="/" className="back">
        ← Sessions
      </Link>
      <h1>Practice</h1>
      <p className="lead">
        Read each sentence or word aloud. Get it right and it comes back after longer and longer breaks, until
        it&apos;s mastered; miss it and it comes back soon.
      </p>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      {!due && !error && <p className="meta">Loading…</p>}

      {due && (
        <p className="meta practice-counts">
          {due.dueCount} due · {due.activeCount} in practice · {due.masteredCount} mastered
        </p>
      )}

      {due && !item && (
        <section className="card">
          <h2>Nothing to practise right now</h2>
          <p>
            {due.activeCount === 0
              ? "Corrections and words to work on appear here after a graded session."
              : `The next item is due ${describeDue(due.nextDueAt)}.`}
          </p>
          <Link href="/">Start a session</Link>
        </section>
      )}

      {item && (
        <section className="card practice-card" aria-live="polite">
          <p className="meta">
            {KIND_LABELS[item.kind]} · {stepLabel(item.box)}
            {item.bestScore !== null && ` · best ${Math.round(item.bestScore)}`}
          </p>
          {item.original && (
            <p className="was">
              <span className="sr-only">You said: </span>
              {item.original}
            </p>
          )}
          <p className={item.kind === "word" ? "practice-target word" : "practice-target"} lang={item.locale ?? "en-US"}>
            {item.target}
          </p>
          {item.explanation && <p className="why">{item.explanation}</p>}

          {attempt ? (
            <AttemptResult attempt={attempt} />
          ) : (
            <p className="hint" role="status">
              {phase === "recording"
                ? "Listening… read the text aloud, then pause."
                : phase === "scoring"
                  ? "Scoring…"
                  : phase === "playing"
                    ? "Playing…"
                    : (notice ?? "Listen first if you like, then read it aloud.")}
            </p>
          )}

          <div className="buttons">
            <button type="button" onClick={() => void listen()} disabled={busy}>
              Listen
            </button>
            {attempt ? (
              <>
                <button type="button" onClick={() => void read()} disabled={busy}>
                  Try again
                </button>
                <button type="button" className="primary" onClick={next} disabled={busy}>
                  Next
                </button>
              </>
            ) : (
              <button type="button" className="primary" onClick={() => void read()} disabled={busy}>
                {phase === "recording" ? "Listening…" : "Read aloud"}
              </button>
            )}
            <button type="button" className="practice-remove" onClick={() => void remove()} disabled={busy}>
              Remove
            </button>
          </div>
        </section>
      )}
    </main>
  );
}

function AttemptResult({ attempt }: { attempt: PracticeAttempt }) {
  const ratings = attempt.words.map((w) => rateWord(w.accuracy, w.errorType));
  const used = (Object.keys(RATING_LABELS) as WordRating[]).filter((r) => ratings.includes(r));
  return (
    <div className="practice-result">
      <p className="practice-score">
        <strong>{Math.round(attempt.score)}</strong> / 100 ·{" "}
        {attempt.passed
          ? attempt.item.dueAt
            ? `Well done. Next review ${describeDue(attempt.item.dueAt)}.`
            : "Mastered."
          : "Not yet. It comes back soon; try again now if you like."}
      </p>
      <p className="practice-words">
        {attempt.words.map((w, i) => (
          <span
            key={i}
            className={`rated ${ratings[i]}`}
            title={`${RATING_LABELS[ratings[i]]}${w.accuracy === null ? "" : `: ${Math.round(w.accuracy)}`}`}
          >
            {w.word}
            <small>{w.accuracy === null ? "–" : Math.round(w.accuracy)}</small>
          </span>
        ))}
      </p>
      <p className="hint practice-legend">
        {used.map((r) => (
          <span key={r} className={`rated ${r}`}>
            {RATING_LABELS[r]}
          </span>
        ))}
      </p>
    </div>
  );
}
