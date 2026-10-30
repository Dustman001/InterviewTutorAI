"use client";

import { useState } from "react";
import { useHydrated } from "@/lib/hydrated";

// After this long, explain the wait: the database pauses when idle and takes up to a minute to resume.
const SLOW_SIGN_IN_MS = 4000;

export default function Login() {
  const hydrated = useHydrated();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [slow, setSlow] = useState(false);

  async function signIn(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const slowTimer = setTimeout(() => setSlow(true), SLOW_SIGN_IN_MS);
    try {
      const response = await fetch("/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        setError(typeof body?.error === "string" ? body.error : `Sign-in failed (${response.status}).`);
        setBusy(false);
        return;
      }
      // Only follow same-site paths. "//host" and "/\host" are treated by browsers as other sites.
      const next = new URLSearchParams(window.location.search).get("next");
      const safe = next && /^\/(?![/\\])/.test(next) ? next : "/";
      window.location.assign(safe);
    } catch {
      setError("Could not reach the server.");
      setBusy(false);
    } finally {
      clearTimeout(slowTimer);
      setSlow(false);
    }
  }

  return (
    <main className="shell login">
      <div className="Centered">
        <form className="card setup" onSubmit={signIn}>
          <h1 className="Centered">AI Speech tutor</h1>
          {error && (
            <div className="banner" role="alert">
              {error}
            </div>
          )}
          <label className="field">
            <span>Username</span>
            <input
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              autoFocus
              required
            />
          </label>
          <label className="field">
            <span>Password</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          <button className="primary" type="submit" disabled={!hydrated || busy}>
            {busy ? "Signing in…" : "Sign in"}
          </button>
          {slow && (
            <p className="hint" role="status">
              Still signing in. The server pauses when nobody has used it for a while, and it can take up to a minute to
              start again.
            </p>
          )}
        </form>
      </div>

    </main>
  );
}
