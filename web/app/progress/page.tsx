"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import ProgressChart, { type ChartPoint } from "@/components/ProgressChart";
import { api, type Mode, type Progress, type ProgressPoint } from "@/lib/api";
import { CEFR_LEVELS, formatDate } from "@/lib/format";
import { compareRecent, describeChange, levelIndex, niceScale } from "@/lib/progress";

type Filter = "All" | Mode;
const FILTERS: Filter[] = ["All", "Conversation", "Interview"];

const LEVEL_CHARTS = [
  { key: "overall", title: "Overall level" },
  { key: "fluency", title: "Fluency" },
  { key: "grammar", title: "Grammar" },
  { key: "vocabulary", title: "Vocabulary" },
  { key: "interaction", title: "Interaction" },
  { key: "pronunciation", title: "Pronunciation level" },
] as const;

// Gridlines on the main levels only; the plus levels sit between them.
const LEVEL_TICKS = ["A1", "A2", "B1", "B2", "C1", "C2"].map((label) => ({ value: levelIndex(label)!, label }));

const shortDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });

export default function ProgressPage() {
  return (
    <Suspense fallback={<main className="shell"><p className="hint">Loading…</p></main>}>
      <ProgressView />
    </Suspense>
  );
}

function ProgressView() {
  // ?user=<id> lets an admin open someone else's progress from the Users page.
  const requested = Number(useSearchParams().get("user"));
  const userId = Number.isInteger(requested) && requested > 0 ? requested : null;
  const [filter, setFilter] = useState<Filter>("All");
  const [loaded, setLoaded] = useState<{ key: string; data: Progress } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const key = `${filter}|${userId ?? ""}`;
  const data = loaded?.data ?? null;
  // The previous result stays on screen, dimmed, while a new filter loads.
  const loading = loaded?.key !== key;

  useEffect(() => {
    let cancelled = false;
    api
      .progress(filter === "All" ? undefined : filter, userId ?? undefined)
      .then((result) => {
        if (!cancelled) {
          setLoaded({ key, data: result });
          setError(null);
        }
      })
      .catch((e: Error) => !cancelled && setError(e.message));
    return () => {
      cancelled = true;
    };
  }, [filter, userId, key]);

  const points = data?.points ?? [];
  const change = compareRecent(points.map((p) => p.overall));
  const chartPoints = (value: (p: ProgressPoint) => number | null): ChartPoint[] =>
    points.map((p) => ({
      label: shortDate.format(new Date(p.startedAt)),
      detail: p.mode === "Interview" ? "Mock interview" : "Conversation",
      value: value(p),
    }));
  const wpm = niceScale(points.map((p) => p.wordsPerMinute ?? NaN));

  return (
    <main className="shell progress">
      <Link href={userId ? "/users" : "/"} className="back">
        ← {userId ? "Users" : "Sessions"}
      </Link>
      <h1>{userId && data ? `${data.username}'s progress` : "Your progress"}</h1>

      <div className="segmented" role="tablist" aria-label="Session type">
        {FILTERS.map((f) => (
          <button key={f} type="button" role="tab" aria-selected={filter === f} onClick={() => setFilter(f)}>
            {f === "Interview" ? "Mock interviews" : f === "Conversation" ? "Conversations" : "All sessions"}
          </button>
        ))}
      </div>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      {!data && !error && <p className="hint">Loading…</p>}

      {data && points.length === 0 && (
        <p className="hint">
          No graded sessions yet. Sessions with at least 150 spoken words get CEFR levels, and they appear here.
        </p>
      )}

      {data && points.length > 0 && (
        // Keep the previous charts on screen while a new filter loads.
        <div className={loading ? "refreshing" : undefined}>
          <p className="lead">
            {change ? describeChange(change) : `One graded session so far, at ${points[0].overall}.`}{" "}
            <span className="meta">
              {points.length} graded {points.length === 1 ? "session" : "sessions"}, from{" "}
              {formatDate(points[0].startedAt)}.
            </span>
          </p>

          <section className="viz-grid-layout">
            {LEVEL_CHARTS.map(({ key, title }) => (
              <ProgressChart
                key={key}
                title={title}
                points={chartPoints((p) => levelIndex(p[key]))}
                min={0}
                max={CEFR_LEVELS.length - 1}
                ticks={LEVEL_TICKS}
                step
                format={(v) => CEFR_LEVELS[Math.round(v)]}
              />
            ))}
            <ProgressChart
              title="Words per minute"
              points={chartPoints((p) => p.wordsPerMinute)}
              min={wpm.min}
              max={wpm.max}
              ticks={wpm.ticks.map((t) => ({ value: t, label: String(t) }))}
              format={(v) => String(Math.round(v))}
            />
            <ProgressChart
              title="Pronunciation score (0-100)"
              points={chartPoints((p) => p.pronunciationScore)}
              min={0}
              max={100}
              ticks={[0, 25, 50, 75, 100].map((t) => ({ value: t, label: String(t) }))}
              format={(v) => String(Math.round(v))}
            />
          </section>

          <details className="card">
            <summary>Show as a table</summary>
            <div className="table-scroll">
              <table className="viz-table">
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Type</th>
                    <th>Overall</th>
                    <th>Fluency</th>
                    <th>Grammar</th>
                    <th>Vocabulary</th>
                    <th>Interaction</th>
                    <th>Pronunciation</th>
                    <th>Words / min</th>
                    <th>Pronunciation score</th>
                  </tr>
                </thead>
                <tbody>
                  {[...points].reverse().map((p) => (
                    <tr key={p.sessionId}>
                      <td>
                        <Link href={`/session/${p.sessionId}/summary`}>{formatDate(p.startedAt)}</Link>
                      </td>
                      <td>{p.mode === "Interview" ? "Mock interview" : "Conversation"}</td>
                      <td>{p.overall}</td>
                      <td>{p.fluency ?? "–"}</td>
                      <td>{p.grammar ?? "–"}</td>
                      <td>{p.vocabulary ?? "–"}</td>
                      <td>{p.interaction ?? "–"}</td>
                      <td>{p.pronunciation ?? "–"}</td>
                      <td>{p.wordsPerMinute === null ? "–" : Math.round(p.wordsPerMinute)}</td>
                      <td>{p.pronunciationScore === null ? "–" : Math.round(p.pronunciationScore)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </details>
        </div>
      )}
    </main>
  );
}
