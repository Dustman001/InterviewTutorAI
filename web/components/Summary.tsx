"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api, type Assessment, type DimensionGrade, type SessionDetail } from "@/lib/api";
import { CEFR_LEVELS, formatDate, scenarioTitle } from "@/lib/format";

const DIMENSIONS = ["fluency", "grammar", "vocabulary", "interaction", "pronunciation"] as const;
type Dimension = (typeof DIMENSIONS)[number];

export default function Summary({ id }: { id: string }) {
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scoring, setScoring] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        let loaded = await api.getSession(id);
        // Ended without a stored assessment (e.g. grading failed earlier): try again.
        if (!loaded.assessment && loaded.session.status === "Ended") {
          if (!cancelled) setScoring(true);
          const assessment = await api.endSession(id);
          loaded = { ...loaded, assessment };
        }
        if (!cancelled) setDetail(loaded);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : "Could not load the summary.");
      } finally {
        if (!cancelled) setScoring(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id]);

  if (error) {
    return (
      <main className="shell">
        <Link href="/" className="back">
          ← Sessions
        </Link>
        <div className="banner" role="alert">
          {error}
        </div>
      </main>
    );
  }

  if (!detail) {
    return (
      <main className="shell">
        <p className="hint">{scoring ? "Scoring your session… this can take up to a minute." : "Loading…"}</p>
      </main>
    );
  }

  const { session, turns, assessment } = detail;
  return (
    <main className="shell summary">
      <Link href="/" className="back">
        ← Sessions
      </Link>
      <h1>{scenarioTitle(session)}</h1>
      <p className="meta">
        {formatDate(session.startedAt)}
        {assessment && ` · ${assessment.metrics.fluency.totalWords} words spoken`}
      </p>

      {!assessment && (
        <div className="banner">
          This session is still in progress. <Link href={`/session/${id}`}>Go back to it</Link> and choose “End &amp;
          score”.
        </div>
      )}

      {assessment && !assessment.sufficient && (
        <div className="banner">
          <strong>Not enough speech to grade.</strong> {assessment.reason}
        </div>
      )}

      {assessment?.sufficient && <Graded assessment={assessment} />}

      <details className="card">
        <summary>Transcript</summary>
        <div className="transcript static">
          {turns.map((turn) => (
            <div key={turn.id} className={`line ${turn.role}`}>
              <span className="who">{turn.role === "tutor" ? "Tutor" : "You"}</span>
              <p>{turn.text}</p>
            </div>
          ))}
        </div>
      </details>
    </main>
  );
}

function Graded({ assessment }: { assessment: Assessment }) {
  const { levels, metrics, detail } = assessment;
  const { fluency, pronunciation } = metrics;

  return (
    <>
      <section className="levels">
        {DIMENSIONS.map((dimension) => (
          <LevelCard key={dimension} name={dimension} level={levels[dimension]} />
        ))}
      </section>
      <p className="meta">
        Overall <strong>{levels.overall}</strong> (the middle of the five). These are estimates from one session, not
        an exam result.
      </p>

      {detail && <p className="lead">{detail.summary}</p>}

      {detail && (
        <section className="grid">
          <GradeCard title="Fluency" grade={detail.fluency}>
            {fluency.wordsPerMinute !== null && (
              <dl className="stats">
                <Stat label="Words / min" value={fluency.wordsPerMinute} />
                <Stat label="Time pausing" value={percent(fluency.pauseRatio)} />
                <Stat label="Long pauses / min" value={fluency.longPausesPerMinute} />
                <Stat label="Words per run" value={fluency.meanRunLength} />
              </dl>
            )}
          </GradeCard>
          <GradeCard title="Grammar" grade={detail.grammar} />
          <GradeCard title="Vocabulary" grade={detail.vocabulary} />
          <GradeCard title="Interaction" grade={detail.interaction} />

          <article className="card">
            <h2>
              Pronunciation <span className="chip">{levels.pronunciation ?? "n/a"}</span>
            </h2>
            {pronunciation.composite === null ? (
              <p>No audio was assessed in this session, so pronunciation was not measured.</p>
            ) : (
              <>
                <dl className="stats">
                  <Stat label="Accuracy" value={pronunciation.accuracy} />
                  <Stat label="Prosody" value={pronunciation.prosody} />
                  <Stat label="Words assessed" value={pronunciation.assessedWords} />
                </dl>
                {pronunciation.weakestWords.length > 0 && (
                  <>
                    <h3>Words to practise</h3>
                    <ul className="words">
                      {pronunciation.weakestWords.map((w) => (
                        <li key={w.word}>
                          {w.word} <span>{Math.round(w.score)}</span>
                        </li>
                      ))}
                    </ul>
                  </>
                )}
              </>
            )}
          </article>
        </section>
      )}

      {detail && detail.topCorrections.length > 0 && (
        <section className="card">
          <h2>Corrections</h2>
          <ul className="corrections">
            {detail.topCorrections.map((c, i) => (
              <li key={i}>
                <p className="was">{c.original}</p>
                <p className="now">{c.corrected}</p>
                <p className="why">{c.explanation}</p>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}

function LevelCard({ name, level }: { name: Dimension; level: string | null }) {
  const index = level ? CEFR_LEVELS.indexOf(level) : -1;
  return (
    <div className="level">
      <span className="level-name">{name}</span>
      <span className="level-value">{level ?? "n/a"}</span>
      <span className="scale" aria-hidden>
        {CEFR_LEVELS.map((l, i) => (
          <i key={l} className={i <= index ? "on" : ""} />
        ))}
      </span>
    </div>
  );
}

function GradeCard({ title, grade, children }: { title: string; grade: DimensionGrade; children?: React.ReactNode }) {
  return (
    <article className="card">
      <h2>
        {title} <span className="chip">{grade.level}</span>
        <span className="confidence">{grade.confidence} confidence</span>
      </h2>
      {children}
      <p>{grade.advice}</p>
      {grade.evidence.length > 0 && (
        <ul className="evidence">
          {grade.evidence.map((quote, i) => (
            <li key={i}>“{quote}”</li>
          ))}
        </ul>
      )}
    </article>
  );
}

function Stat({ label, value }: { label: string; value: number | string | null }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value ?? "–"}</dd>
    </div>
  );
}

function percent(ratio: number | null) {
  return ratio === null ? null : `${Math.round(ratio * 100)}%`;
}
