"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { api, users, type AdminUser, type NewUser, type UsageEstimate } from "@/lib/api";
import { formatDate } from "@/lib/format";
import { useHydrated } from "@/lib/hydrated";

const MIN_PASSWORD = 8;

export default function UsersPage() {
  const [list, setList] = useState<AdminUser[] | null>(null);
  const [me, setMe] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      setList(await users.list());
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not load users.");
    }
  }, []);

  useEffect(() => {
    users
      .list()
      .then(setList)
      .catch((e: Error) => setError(e.message));
    api
      .me()
      .then((u) => setMe(u.username))
      .catch(() => {});
  }, []);

  /** Runs an action, shows its outcome, and reloads the list. */
  async function run(action: () => Promise<unknown>, success: string) {
    setError(null);
    setNotice(null);
    try {
      await action();
      setNotice(success);
      await reload();
      return true;
    } catch (e) {
      setError(e instanceof Error ? e.message : "That didn't work.");
      return false;
    }
  }

  return (
    <main className="shell">
      <Link href="/" className="back">
        ← Back to the tutor
      </Link>
      <h1>Users</h1>
      <p className="lead">
        Disabling a user, resetting their password or changing their role signs them out right away.
      </p>

      {error && (
        <div className="banner" role="alert">
          {error}
        </div>
      )}
      {notice && (
        <div className="banner ok" role="status">
          {notice}
        </div>
      )}

      <CreateUser onCreate={(user) => run(() => users.create(user), `Created ${user.username}.`)} />

      <h2 className="section">All users</h2>
      {list === null && !error && <p className="hint">Loading…</p>}
      <ul className="users">
        {list?.map((user) => (
          <UserRow key={user.id} user={user} isMe={user.username === me} run={run} />
        ))}
      </ul>
    </main>
  );
}

function CreateUser({ onCreate }: { onCreate: (user: NewUser) => Promise<boolean> }) {
  const hydrated = useHydrated();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState<"Admin" | "Guest">("Guest");
  const [limit, setLimit] = useState(3);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    const created = await onCreate({ username, password, role, sessionLimit: role === "Guest" ? limit : null });
    setBusy(false);
    if (created) {
      setUsername("");
      setPassword("");
    }
  }

  return (
    <form className="card setup" onSubmit={submit}>
      <h2 className="card-title">Add a user</h2>
      <div className="row">
        <label className="field">
          <span>Username</span>
          <input
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            pattern="[A-Za-z0-9._@\-]{3,100}"
            title="3 to 100 characters: letters, digits, and . _ @ -"
            autoComplete="off"
            required
          />
        </label>
        <label className="field">
          <span>Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            minLength={MIN_PASSWORD}
            autoComplete="new-password"
            required
          />
        </label>
      </div>
      <div className="row">
        <label className="field">
          <span>Role</span>
          <select value={role} onChange={(e) => setRole(e.target.value as "Admin" | "Guest")}>
            <option value="Guest">Guest: limited sessions</option>
            <option value="Admin">Admin: unlimited, manages users</option>
          </select>
        </label>
        {role === "Guest" && (
          <label className="field">
            <span>Session limit</span>
            <input
              type="number"
              min={1}
              max={10000}
              value={limit}
              onChange={(e) => setLimit(Number(e.target.value))}
              required
            />
          </label>
        )}
      </div>
      <button className="primary" type="submit" disabled={!hydrated || busy}>
        {busy ? "Adding…" : "Add user"}
      </button>
    </form>
  );
}

type Run = (action: () => Promise<unknown>, success: string) => Promise<boolean>;

const compact = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });
const usd = new Intl.NumberFormat("en", { style: "currency", currency: "USD", minimumFractionDigits: 2 });

/** e.g. "30 days: 1.2M tokens, ~$0.84". */
function usageText(usage: UsageEstimate) {
  const cost = usage.unpricedModels.length > 0 ? `≥ ${usd.format(usage.estimatedCostUsd)}` : `~${usd.format(usage.estimatedCostUsd)}`;
  return `30 days: ${compact.format(usage.tokens)} tokens, ${cost}`;
}

function UserRow({ user, isMe, run }: { user: AdminUser; isMe: boolean; run: Run }) {
  const [panel, setPanel] = useState<"none" | "password" | "limit" | "delete">("none");
  const [password, setPassword] = useState("");
  const [limit, setLimit] = useState(user.sessionLimit ?? 3);
  const isGuest = user.role === "Guest";

  async function act(action: () => Promise<unknown>, success: string) {
    if (await run(action, success)) {
      setPanel("none");
      setPassword("");
    }
  }

  function toggle(next: typeof panel) {
    setPanel(panel === next ? "none" : next);
  }

  return (
    <li className={user.isDisabled ? "user disabled" : "user"}>
      <div className="user-head">
        <span className="user-name">
          {user.username}
          {isMe && <span className="meta"> (you)</span>}
        </span>
        <span className="chip">{user.role}</span>
        {user.isDisabled && <span className="chip off">Disabled</span>}
        <span className="meta">
          {user.sessionLimit === null
            ? `${user.sessionsUsed} sessions · no limit`
            : `${user.sessionsUsed} of ${user.sessionLimit} sessions used`}{" "}
          · added {formatDate(user.createdAt)}
          {user.usage30Days && user.usage30Days.tokens > 0 && <> · {usageText(user.usage30Days)}</>}
        </span>
      </div>

      <div className="user-actions">
        {!isMe && (
          <button
            type="button"
            onClick={() =>
              act(
                () => users.update(user.id, { isDisabled: !user.isDisabled }),
                user.isDisabled ? `Enabled ${user.username}.` : `Disabled ${user.username}; they were signed out.`,
              )
            }
          >
            {user.isDisabled ? "Enable" : "Disable"}
          </button>
        )}
        <Link className="button-link" href={`/progress?user=${user.id}`}>
          Progress
        </Link>
        <button type="button" aria-expanded={panel === "password"} onClick={() => toggle("password")}>
          Reset password
        </button>
        {isGuest && (
          <>
            <button type="button" aria-expanded={panel === "limit"} onClick={() => toggle("limit")}>
              Change limit
            </button>
            <button
              type="button"
              disabled={user.sessionsUsed === 0}
              onClick={() => act(() => users.resetSessions(user.id), `${user.username} has all ${user.sessionLimit} sessions again.`)}
            >
              Reset sessions
            </button>
          </>
        )}
        {!isMe && (
          <button
            type="button"
            className="danger"
            aria-expanded={panel === "delete"}
            onClick={() => toggle("delete")}
          >
            Delete
          </button>
        )}
      </div>

      {panel === "password" && (
        <form
          className="user-panel"
          onSubmit={(e) => {
            e.preventDefault();
            void act(
              () => users.setPassword(user.id, password),
              isMe ? "Your password was changed. Sign in again." : `New password set for ${user.username}; they were signed out.`,
            );
          }}
        >
          <input
            type="password"
            placeholder={`New password (${MIN_PASSWORD}+ characters)`}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            minLength={MIN_PASSWORD}
            autoComplete="new-password"
            required
            autoFocus
          />
          <button type="submit">Save password</button>
        </form>
      )}

      {panel === "limit" && (
        <form
          className="user-panel"
          onSubmit={(e) => {
            e.preventDefault();
            void act(() => users.update(user.id, { sessionLimit: limit }), `${user.username} can now start ${limit} sessions.`);
          }}
        >
          <input
            type="number"
            min={1}
            max={10000}
            value={limit}
            onChange={(e) => setLimit(Number(e.target.value))}
            aria-label="Session limit"
            required
            autoFocus
          />
          <button type="submit">Save limit</button>
        </form>
      )}

      {panel === "delete" && (
        <div className="user-panel">
          <span className="hint">
            Delete {user.username}? Their past sessions are kept and stay visible to admins.
          </span>
          <button type="button" className="danger" onClick={() => act(() => users.remove(user.id), `Deleted ${user.username}.`)}>
            Delete for good
          </button>
          <button type="button" onClick={() => setPanel("none")}>
            Cancel
          </button>
        </div>
      )}
    </li>
  );
}
