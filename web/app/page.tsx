"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { api, signOut, type CurrentUser, type Mode, type SessionSummary } from "@/lib/api";
import { formatDate, scenarioTitle } from "@/lib/format";

const TOPICS = ["Small talk", "Travel", "Work and career", "Technology", "Opinions and debate"];
const INTERVIEW_TYPES = ["Behavioral", "Technical", "HR screening"];
const DIFFICULTIES = ["Easy", "Normal", "Challenging"];
const DURATIONS = [5, 10, 15, 20, 30];

export default function Home() {
  const router = useRouter();
  const [mode, setMode] = useState<Mode>("Conversation");
  const [topic, setTopic] = useState(TOPICS[0]);
  const [customTopic, setCustomTopic] = useState("");
  const [role, setRole] = useState("");
  const [seniority, setSeniority] = useState("");
  const [interviewType, setInterviewType] = useState(INTERVIEW_TYPES[0]);
  const [jobDescription, setJobDescription] = useState("");
  const [difficulty, setDifficulty] = useState("Normal");
  const [duration, setDuration] = useState(10);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sessions, setSessions] = useState<SessionSummary[] | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [user, setUser] = useState<CurrentUser | null>(null);

  useEffect(() => {
    api
      .listSessions()
      .then((page) => {
        setSessions(page.items);
        setNextCursor(page.nextCursor);
      })
      .catch((e: Error) => setError(e.message));
    api
      .me()
      .then(setUser)
      .catch((e: Error) => setError(e.message));
  }, []);

  const sessionsLeft = user?.sessionLimit == null ? null : Math.max(0, user.sessionLimit - user.sessionsUsed);
  const outOfSessions = sessionsLeft === 0;

  async function loadMore() {
    if (!nextCursor) return;
    setLoadingMore(true);
    try {
      const page = await api.listSessions(nextCursor);
      setSessions((current) => [...(current ?? []), ...page.items]);
      setNextCursor(page.nextCursor);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not load more sessions.");
    } finally {
      setLoadingMore(false);
    }
  }

  async function start(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const session = await api.createSession(
        mode,
        mode === "Conversation"
          ? { topic: customTopic.trim() || topic, difficulty, durationMinutes: duration }
          : { role, seniority, interviewType, jobDescription, difficulty, durationMinutes: duration },
      );
      router.push(`/session/${session.id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not start the session.");
    } finally {
      // Also on success: this page can be restored from history with its state intact.
      setBusy(false);
    }
  }

  return (
    <main className="shell">
      {user && (
        <div className="userbar">
          <span>
            Signed in as <strong>{user.username}</strong>
            {sessionsLeft === null
              ? " · no session limit"
              : ` · ${sessionsLeft} of ${user.sessionLimit} sessions left`}
          </span>
          <span className="userbar-actions">
            <Link href="/progress">Progress</Link>
            <Link href="/account">Account</Link>
            {user.role === "Admin" && <Link href="/users">Manage users</Link>}
            <button type="button" onClick={() => void signOut()}>
              Sign out
            </button>
          </span>
        </div>
      )}
      <h1>Speech tutor</h1>
      <p className="lead">Practise speaking English out loud, then see where you stand on the CEFR scale.</p>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      <form className="card setup" onSubmit={start}>
        <div className="segmented" role="tablist">
          {(["Conversation", "Interview"] as Mode[]).map((m) => (
            <button key={m} type="button" role="tab" aria-selected={mode === m} onClick={() => setMode(m)}>
              {m === "Interview" ? "Mock interview" : m}
            </button>
          ))}
        </div>

        {mode === "Conversation" ? (
          <>
            <div className="field">
              <span>Topic</span>
              <div className="chips">
                {TOPICS.map((t) => (
                  <button
                    key={t}
                    type="button"
                    className={!customTopic.trim() && topic === t ? "on" : ""}
                    onClick={() => {
                      setTopic(t);
                      setCustomTopic("");
                    }}
                  >
                    {t}
                  </button>
                ))}
              </div>
            </div>
            <label className="field">
              <span>Or your own topic</span>
              <input
                value={customTopic}
                maxLength={200}
                onChange={(e) => setCustomTopic(e.target.value)}
                placeholder="e.g. the last film I watched"
              />
            </label>
          </>
        ) : (
          <>
            <div className="row">
              <label className="field">
                <span>Role</span>
                <input
                  value={role}
                  maxLength={200}
                  onChange={(e) => setRole(e.target.value)}
                  placeholder="e.g. Backend engineer"
                  required
                />
              </label>
              <label className="field">
                <span>Seniority</span>
                <input value={seniority} maxLength={200} onChange={(e) => setSeniority(e.target.value)} placeholder="e.g. Senior" />
              </label>
            </div>
            <label className="field">
              <span>Interview type</span>
              <select value={interviewType} onChange={(e) => setInterviewType(e.target.value)}>
                {INTERVIEW_TYPES.map((t) => (
                  <option key={t}>{t}</option>
                ))}
              </select>
            </label>
            <label className="field">
              <span>Job description (optional)</span>
              <textarea
                value={jobDescription}
                maxLength={8000}
                onChange={(e) => setJobDescription(e.target.value)}
                rows={4}
                placeholder="Paste the posting to get questions tailored to it"
              />
            </label>
          </>
        )}

        <div className="row">
          <label className="field">
            <span>Tutor&apos;s language</span>
            <select value={difficulty} onChange={(e) => setDifficulty(e.target.value)}>
              {DIFFICULTIES.map((d) => (
                <option key={d}>{d}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Length</span>
            <select value={duration} onChange={(e) => setDuration(Number(e.target.value))}>
              {DURATIONS.map((d) => (
                <option key={d} value={d}>
                  {d} minutes
                </option>
              ))}
            </select>
          </label>
        </div>

        {outOfSessions && (
          <p className="hint">This guest account has used all {user?.sessionLimit} of its sessions.</p>
        )}
        <button className="primary" type="submit" disabled={busy || outOfSessions}>
          {busy ? "Starting…" : "Start session"}
        </button>
      </form>

      <h2 className="section">Past sessions</h2>
      {sessions === null && !error && <p className="hint">Loading…</p>}
      {sessions?.length === 0 && <p className="hint">Nothing yet. Your sessions and their scores will appear here.</p>}
      <ul className="history">
        {sessions?.map((s) => (
          <li key={s.id}>
            <Link href={s.status === "Ended" ? `/session/${s.id}/summary` : `/session/${s.id}`}>
              <span className="history-title">{scenarioTitle(s)}</span>
              <span className="meta">{formatDate(s.startedAt)}</span>
              <span className="history-levels">
                {s.levels ? (
                  <>
                    <span className="chip">{s.levels.overall}</span>
                    <span className="meta">
                      F {s.levels.fluency} · G {s.levels.grammar} · V {s.levels.vocabulary} · I {s.levels.interaction} ·
                      P {s.levels.pronunciation ?? "n/a"}
                    </span>
                  </>
                ) : (
                  <span className="meta">
                    {s.status === "Active"
                      ? "In progress"
                      : s.gradingState === "Pending" || s.gradingState === "Running"
                        ? "Scoring…"
                        : "Not graded"}
                  </span>
                )}
              </span>
            </Link>
          </li>
        ))}
      </ul>
      {nextCursor && (
        <button type="button" className="ghost load-more" onClick={() => void loadMore()} disabled={loadingMore}>
          {loadingMore ? "Loading…" : "Load more"}
        </button>
      )}
    </main>
  );
}
