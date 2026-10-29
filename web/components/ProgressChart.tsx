"use client";

import { useId, useState } from "react";

export interface ChartPoint {
  /** Shown in the tooltip, e.g. the session date. */
  label: string;
  /** Second tooltip line, e.g. the session type. */
  detail?: string;
  /** Null leaves a gap (the session has no value for this measure). */
  value: number | null;
}

interface Props {
  title: string;
  points: ChartPoint[];
  min: number;
  max: number;
  ticks: { value: number; label: string }[];
  /** Levels are steps (hold until the next session); measurements are lines. */
  step?: boolean;
  format: (value: number) => string;
}

// Fixed drawing size; the SVG scales to its card width.
const W = 340;
const H = 168;
const M = { top: 12, right: 44, bottom: 24, left: 36 };
const PLOT_W = W - M.left - M.right;
const PLOT_H = H - M.top - M.bottom;

/** One measure over the learner's sessions: a single series, so no legend; the title names it. */
export default function ProgressChart({ title, points, min, max, ticks, step = false, format }: Props) {
  const [active, setActive] = useState<number | null>(null);
  const titleId = useId();

  const x = (i: number) => M.left + (points.length === 1 ? PLOT_W / 2 : (i / (points.length - 1)) * PLOT_W);
  const y = (v: number) => M.top + PLOT_H - ((v - min) / (max - min || 1)) * PLOT_H;

  // Runs of consecutive sessions with a value; a missing value breaks the line.
  const paths: string[] = [];
  let d = "";
  points.forEach((p, i) => {
    if (p.value === null) {
      if (d) paths.push(d);
      d = "";
      return;
    }
    const px = x(i);
    const py = y(p.value);
    if (!d) d = `M${px},${py}`;
    else if (step) d += `H${px}V${py}`;
    else d += `L${px},${py}`;
  });
  if (d) paths.push(d);

  const last = points.reduce<number | null>((found, p, i) => (p.value !== null ? i : found), null);
  const shown = active ?? null;

  function pick(clientX: number, rect: DOMRect) {
    const local = ((clientX - rect.left) / rect.width) * W;
    const ratio = points.length === 1 ? 0 : (local - M.left) / PLOT_W;
    setActive(Math.min(points.length - 1, Math.max(0, Math.round(ratio * (points.length - 1)))));
  }

  function onKey(event: React.KeyboardEvent) {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    const from = shown ?? points.length - 1;
    setActive(Math.min(points.length - 1, Math.max(0, from + (event.key === "ArrowRight" ? 1 : -1))));
  }

  const tip = shown !== null ? points[shown] : null;
  const tipLeft = shown !== null ? (x(shown) / W) * 100 : 0;

  return (
    <figure className="viz">
      <figcaption id={titleId}>{title}</figcaption>
      <div className="viz-frame">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          role="img"
          aria-labelledby={titleId}
          aria-describedby={`${titleId}-summary`}
          tabIndex={0}
          onPointerMove={(e) => pick(e.clientX, e.currentTarget.getBoundingClientRect())}
          onPointerLeave={() => setActive(null)}
          onFocus={() => setActive(last)}
          onBlur={() => setActive(null)}
          onKeyDown={onKey}
        >
          {ticks.map((t) => (
            <g key={t.value}>
              <line className="viz-grid" x1={M.left} x2={M.left + PLOT_W} y1={y(t.value)} y2={y(t.value)} />
              <text className="viz-tick" x={M.left - 6} y={y(t.value)} dy="0.32em" textAnchor="end">
                {t.label}
              </text>
            </g>
          ))}

          {points.length > 0 && (
            <>
              <text className="viz-tick" x={x(0)} y={H - 6} textAnchor={points.length === 1 ? "middle" : "start"}>
                {points[0].label}
              </text>
              {points.length > 1 && (
                <text className="viz-tick" x={x(points.length - 1)} y={H - 6} textAnchor="end">
                  {points[points.length - 1].label}
                </text>
              )}
            </>
          )}

          {paths.map((path, i) => (
            <path key={i} className="viz-line" d={path} />
          ))}
          {/* A lone value between gaps has no line; give it a dot. */}
          {points.map((p, i) =>
            p.value !== null &&
            (points[i - 1]?.value ?? null) === null &&
            (points[i + 1]?.value ?? null) === null &&
            i !== last ? (
              <circle key={i} className="viz-dot" cx={x(i)} cy={y(p.value)} r={4} />
            ) : null,
          )}

          {shown !== null && <line className="viz-crosshair" x1={x(shown)} x2={x(shown)} y1={M.top} y2={M.top + PLOT_H} />}

          {last !== null && points[last].value !== null && (
            <>
              <circle className="viz-dot" cx={x(last)} cy={y(points[last].value!)} r={4} />
              <text className="viz-end" x={x(last) + 8} y={y(points[last].value!)} dy="0.32em">
                {format(points[last].value!)}
              </text>
            </>
          )}
          {shown !== null && points[shown].value !== null && shown !== last && (
            <circle className="viz-dot" cx={x(shown)} cy={y(points[shown].value!)} r={4} />
          )}
        </svg>

        {tip && (
          <div className="viz-tip" style={{ left: `${tipLeft}%` }} role="status">
            <strong>{tip.value === null ? "No value" : format(tip.value)}</strong>
            <span>{tip.label}</span>
            {tip.detail && <span>{tip.detail}</span>}
          </div>
        )}
      </div>
      <p id={`${titleId}-summary`} className="sr-only">
        {last === null
          ? `${title}: no values yet.`
          : `${title}: latest ${format(points[last].value!)}, over ${points.length} sessions. Use the arrow keys to read each session.`}
      </p>
    </figure>
  );
}
