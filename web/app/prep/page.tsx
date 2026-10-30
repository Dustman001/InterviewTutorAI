"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { prep, type Drill, type PrepAction, type PrepRun, type PrepRunSummary } from "@/lib/api";
import { useHydrated } from "@/lib/hydrated";
import { describeAction, formatWhen, newIdempotencyKey, runNotice, sortDrills } from "@/lib/prep";

const MAX_JOB_DESCRIPTION = 8000;
const MIN_JOB_DESCRIPTION = 30;

export default function Prep() {
  const hydrated = useHydrated();
  const router = useRouter();
  const [jobDescription, setJobDescription] = useState("");
  const [role, setRole] = useState("");
  const [seniority, setSeniority] = useState("");
  const [running, setRunning] = useState(false);
  const [run, setRun] = useState<PrepRun | null>(null);
  const [runs, setRuns] = useState<PrepRunSummary[]>([]);
  const [drills, setDrills] = useState<Drill[]>([]);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // One key per submission: a retry of the same form after a dropped connection gets the same run back.
  const keyRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([prep.runs(), prep.drills()])
      .then(([r, d]) => {
        if (cancelled) return;
        setRuns(r);
        setDrills(d);
      })
      .catch((e: Error) => !cancelled && setError(e.message));
    return () => {
      cancelled = true;
    };
  }, []);

  function edit(setter: (value: string) => void) {
    return (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      keyRef.current = null;
      setter(event.target.value);
    };
  }

  async function refreshLists() {
    try {
      const [r, d] = await Promise.all([prep.runs(), prep.drills()]);
      setRuns(r);
      setDrills(d);
    } catch {
      // The lists are a convenience; the run on screen is what matters.
    }
  }

  async function startRun(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    keyRef.current ??= newIdempotencyKey();
    setRunning(true);
    try {
      setRun(
        await prep.run(
          { jobDescription: jobDescription.trim(), role: role.trim() || undefined, seniority: seniority.trim() || undefined },
          keyRef.current,
        ),
      );
      keyRef.current = null;
      await refreshLists();
    } catch (e) {
      setError(e instanceof Error ? e.message : "The prep run failed.");
    } finally {
      setRunning(false);
    }
  }

  async function openRun(id: string) {
    setError(null);
    try {
      setRun(await prep.get(id));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not load that run.");
    }
  }

  async function decide(action: PrepAction, confirm: boolean) {
    if (!run) return;
    setBusyId(`action-${action.id}`);
    setError(null);
    try {
      const updated = confirm ? await prep.confirm(run.id, action.id) : await prep.reject(run.id, action.id);
      setRun({ ...run, actions: run.actions.map((a) => (a.id === updated.id ? updated : a)) });
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not record your decision.");
    } finally {
      setBusyId(null);
    }
  }

  async function startDrill(drill: Drill) {
    setBusyId(`drill-${drill.id}`);
    setError(null);
    try {
      const { sessionId } = await prep.startDrill(drill.id);
      router.push(`/session/${sessionId}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not start the drill.");
      setBusyId(null);
    }
  }

  async function cancelDrill(drill: Drill) {
    setBusyId(`drill-${drill.id}`);
    setError(null);
    try {
      await prep.cancelDrill(drill.id);
      await refreshLists();
      if (run) setRun(await prep.get(run.id));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not cancel the drill.");
    } finally {
      setBusyId(null);
    }
  }

  const upcoming = sortDrills(drills);
  const notice = run ? runNotice(run) : null;

  return (
    <main className="shell">
      <Link href="/" className="back">
        ← Sessions
      </Link>
      <h1>Interview prep</h1>
      <p className="lead">
        Paste a job description. The prep agent looks at your recent sessions, picks the questions an interviewer would
        ask, adds phrases to your practice list and schedules a mock-interview drill.
      </p>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      <form className="card setup" onSubmit={startRun}>
        <label className="field">
          <span>Job description</span>
          <textarea
            value={jobDescription}
            onChange={edit(setJobDescription)}
            rows={8}
            minLength={MIN_JOB_DESCRIPTION}
            maxLength={MAX_JOB_DESCRIPTION}
            placeholder="Paste the job posting"
            required
          />
        </label>
        <div className="row">
          <label className="field">
            <span>Role (optional)</span>
            <input value={role} onChange={edit(setRole)} maxLength={200} placeholder="Backend engineer" />
          </label>
          <label className="field">
            <span>Seniority (optional)</span>
            <input value={seniority} onChange={edit(setSeniority)} maxLength={200} placeholder="Senior" />
          </label>
        </div>
        <button className="primary" type="submit" disabled={!hydrated || running}>
          {running ? "Planning…" : "Plan my prep"}
        </button>
        {running && (
          <p className="hint" role="status">
            The agent is working. This can take up to two minutes.
          </p>
        )}
      </form>

      {run && (
        <section className="card prep-run" aria-labelledby="prep-run-title">
          <h2 className="card-title" id="prep-run-title">
            {run.role ?? "Your prep"}
            {run.seniority && ` · ${run.seniority}`}
          </h2>
          <p className="meta">
            {formatWhen(run.createdAt)} · {run.steps} model call{run.steps === 1 ? "" : "s"},{" "}
            {(run.inputTokens + run.outputTokens).toLocaleString()} tokens
          </p>
          {notice && (
            <div className="banner" role="status">
              {notice}
            </div>
          )}
          {run.summary && <p>{run.summary}</p>}

          {run.questions.length > 0 && (
            <>
              <h3 className="prep-heading">Questions</h3>
              <ol className="prep-questions">
                {run.questions.map((q) => (
                  <li key={q.question}>
                    <p>{q.question}</p>
                    <p className="hint">
                      <span className="chip">{q.focus}</span> {q.why}
                    </p>
                  </li>
                ))}
              </ol>
            </>
          )}

          {run.actions.length > 0 && (
            <>
              <h3 className="prep-heading">What the agent did</h3>
              <ul className="prep-actions">
                {run.actions.map((action) => {
                  const view = describeAction(action);
                  return (
                    <li key={action.id} className={view.pending ? "pending" : undefined}>
                      <p>{view.title}</p>
                      {view.details.length > 0 && (
                        <ul className="hint">
                          {view.details.map((d) => (
                            <li key={d}>{d}</li>
                          ))}
                        </ul>
                      )}
                      {view.pending && (
                        <div className="buttons">
                          <button type="button" disabled={busyId !== null} onClick={() => void decide(action, false)}>
                            Keep my progress
                          </button>
                          <button
                            type="button"
                            className="danger"
                            disabled={busyId !== null}
                            onClick={() => void decide(action, true)}
                          >
                            Restart it
                          </button>
                        </div>
                      )}
                    </li>
                  );
                })}
              </ul>
            </>
          )}
        </section>
      )}

      <h2 className="section">Drills</h2>
      {upcoming.length === 0 ? (
        <p className="hint">No drills yet. Plan your prep to get one.</p>
      ) : (
        <ul className="prep-drills">
          {upcoming.map((drill) => (
            <li key={drill.id} className="card">
              <p>
                <strong>{formatWhen(drill.scheduledFor)}</strong> · {drill.scenario.durationMinutes} minutes ·{" "}
                {drill.scenario.interviewType ?? "mixed"} · {drill.scenario.questions?.length ?? 0} questions
              </p>
              <p className="hint">{drill.scenario.role ?? "Interview"}</p>
              {drill.status === "Scheduled" ? (
                <div className="buttons">
                  <button
                    type="button"
                    className="primary"
                    disabled={busyId !== null}
                    onClick={() => void startDrill(drill)}
                  >
                    Start now
                  </button>
                  <button type="button" disabled={busyId !== null} onClick={() => void cancelDrill(drill)}>
                    Cancel
                  </button>
                </div>
              ) : drill.sessionId ? (
                <Link href={`/session/${drill.sessionId}/summary`}>Started · see the session</Link>
              ) : (
                <p className="hint">{drill.status}</p>
              )}
            </li>
          ))}
        </ul>
      )}

      {runs.length > 0 && (
        <>
          <h2 className="section">Past runs</h2>
          <ul className="prep-runs">
            {runs.map((r) => (
              <li key={r.id}>
                <button type="button" onClick={() => void openRun(r.id)}>
                  {formatWhen(r.createdAt)} · {r.role ?? "Job description"} · {r.status}
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </main>
  );
}
