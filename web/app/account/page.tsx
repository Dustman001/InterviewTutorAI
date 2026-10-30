"use client";

import Link from "next/link";
import { useState } from "react";
import { mcpToken, type McpToken } from "@/lib/api";
import { useHydrated } from "@/lib/hydrated";
import { formatWhen } from "@/lib/prep";

const MIN_PASSWORD_LENGTH = 8;

export default function Account() {
  const hydrated = useHydrated();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [token, setToken] = useState<McpToken | null>(null);
  const [tokenBusy, setTokenBusy] = useState(false);

  async function createMcpToken() {
    setError(null);
    setTokenBusy(true);
    try {
      setToken(await mcpToken());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not create a token.");
    } finally {
      setTokenBusy(false);
    }
  }

  async function changePassword(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setDone(false);
    if (next !== confirm) {
      setError("The new passwords don't match.");
      return;
    }

    setBusy(true);
    try {
      const response = await fetch("/auth/password", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      });
      if (response.status === 401) {
        // eslint-disable-next-line @next/next/no-location-assign-relative-destination
        window.location.assign("/login?next=/account");
        return;
      }
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        setError(typeof body?.error === "string" ? body.error : `Changing the password failed (${response.status}).`);
        return;
      }
      setCurrent("");
      setNext("");
      setConfirm("");
      setDone(true);
    } catch {
      setError("Could not reach the server.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="shell">
      <Link href="/" className="back">
        ← Sessions
      </Link>
      <h1>Account</h1>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}
      {done && (
        <div className="banner ok" role="status">
          Password changed. You stay signed in here; other browsers and devices have been signed out.
        </div>
      )}

      <form className="card setup" onSubmit={changePassword}>
        <h2 className="card-title">Change your password</h2>
        <label className="field">
          <span>Current password</span>
          <input
            type="password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        <label className="field">
          <span>New password</span>
          <input
            type="password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
            autoComplete="new-password"
            minLength={MIN_PASSWORD_LENGTH}
            maxLength={200}
            required
          />
        </label>
        <label className="field">
          <span>Confirm new password</span>
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            autoComplete="new-password"
            minLength={MIN_PASSWORD_LENGTH}
            maxLength={200}
            required
          />
        </label>
        <p className="hint">At least {MIN_PASSWORD_LENGTH} characters. Changing it signs you out everywhere else.</p>
        <button className="primary" type="submit" disabled={!hydrated || busy}>
          {busy ? "Changing…" : "Change password"}
        </button>
      </form>

      <section className="card setup account-mcp" aria-labelledby="mcp-title">
        <h2 className="card-title" id="mcp-title">
          Connect Claude or Copilot (MCP)
        </h2>
        <p className="hint">
          A read-only token lets an MCP client such as Claude Code, Claude Desktop or GitHub Copilot read your progress,
          practice items and sessions. It can&apos;t change anything. It lasts 30 days; changing your password revokes it.
        </p>
        {token ? (
          <>
            <p className="hint" role="status">
              Copy it now; it isn&apos;t shown again. Valid until {formatWhen(token.expiresAt)}.
            </p>
            <p className="mcp-token">{token.token}</p>
            <p className="hint">
              The server is <code>{token.path}</code> on the API&apos;s address. For Claude Code:
            </p>
            <p className="mcp-token">
              claude mcp add --transport http tutorai &lt;API URL&gt;{token.path} --header &quot;Authorization: Bearer{" "}
              {token.token}&quot;
            </p>
          </>
        ) : (
          <button type="button" disabled={!hydrated || tokenBusy} onClick={() => void createMcpToken()}>
            {tokenBusy ? "Creating…" : "Create MCP token"}
          </button>
        )}
      </section>
    </main>
  );
}
