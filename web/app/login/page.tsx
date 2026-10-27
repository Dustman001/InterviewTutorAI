"use client";

import { useState } from "react";

export default function Login() {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function signIn(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
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
    }
  }

  return (
    <main className="shell login">
      <h1>Speech tutor</h1>
      <p className="lead">Sign in to practise.</p>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}

      <form className="card setup" onSubmit={signIn}>
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
        <button className="primary" type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </main>
  );
}
