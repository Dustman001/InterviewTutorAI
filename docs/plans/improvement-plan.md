# TutorAI improvement plan

One plan per improvement. Each lists what changes, where, the tests, and the evidence that proves it
done. Sizes: **S** ≤ half a day, **M** 1–2 days, **L** 3+ days.

Migration numbers are assigned in the order the work lands. Use the next free `00NN_*.sql`, additive
and idempotent (`IF COL_LENGTH … IS NULL`), never edit an applied script.

## Order

| Wave | Plans | Why this order |
| --- | --- | --- |
| 1 — risk | P1 session caps, P2 login protection, P3 prompt caching, P15 CI | Small and independent. They close the open-ended cost and the brute-force exposure, and CI guards everything after them. |
| 2 — reliability | P4 background grading, P5 observability, P6 deep health check, P16 front-end tests | P4 is the most likely user-facing failure. P5 reuses the token counts P3 starts capturing. P16 comes before the bigger UI work in wave 3. |
| 3 — product | P9 password change, P11 pagination, P7 progress view, P10 accent choice, P12 printable report | Mostly independent features. P7 reads better once P11's index exists. |
| 4 — depth | P8 practice drills, P13 grading calibration, P14 score sanity checks | P8 hooks into P4's grading worker. P13 needs labelled transcripts (see open questions). |

---

## P1. Cap what one session can cost — S

**Problem.** The guest limit counts sessions, but nothing caps a session itself. `PostTurnAsync`
accepts unlimited turns of unlimited size, and every turn resends the whole history to Claude.
Also, `Scenario.DurationMinutes` is clamped to 120 in the prompt (`ScenarioPrompts.Duration`), but
`TutorChat.BuildMessages` uses the raw value. A client that sends `durationMinutes: 100000` never
gets the time-up note.

**Design.**
- New `SessionLimitsOptions` (section `Sessions`):
  `MaxTurnChars` 4000, `MaxPhrasesPerTurn` 100, `MaxUserTurns` 150, `GraceMinutes` 5,
  `MaxRequestBytes` 4 MB, `MaxJobDescriptionChars` 8000, `MaxScenarioFieldChars` 200,
  `TurnsPerMinute` 20 (the values as shipped).
- `POST /api/sessions`: validate `DurationMinutes` in 5–60 (the UI offers 5–30), and the text-field
  lengths. 400 with a clear message otherwise.
- `POST /api/sessions/{id}/turns`, before any Claude call:
  - text over `MaxTurnChars` or too many phrases → 400;
  - `MaxUserTurns` reached → 409 `{ error, code: "turn_limit" }`;
  - elapsed time past planned + grace → 409 `{ error, code: "time_up" }`.
  - Request body limit via `.WithMetadata(new RequestSizeLimitAttribute(...))` on that endpoint only.
- Per-user rate limit on the turns endpoint (ASP.NET Core `AddRateLimiter`, token bucket
  partitioned by user id, about 20 per minute). This guards against a script hammering a session.
- Replace `DateTime.UtcNow` in `BuildMessages` with the injected `TimeProvider`, so the time logic
  is testable. Use one clamped duration helper in both places.
- Front end (`LiveSession.tsx`): on 409 `time_up`/`turn_limit`, end and score the session
  automatically with a short notice. Add `maxLength` to the typed input.

**Tests.**
- Unit tests for `BuildMessages` with a fake `TimeProvider` (time-up note at planned time, hard stop
  at planned + grace, clamped duration).
- Integration tests (`SessionFlowTests`, no Claude needed because rejection happens before
  streaming): oversized text → 400, oversized body → 413, turn limit → 409, expired session → 409,
  bad duration on create → 400.

**Done when.** Those tests pass, and a manual run shows the live page ending itself when time runs
out.

---

## P2. Protect sign-in against password guessing — S

**Constraint.** Browsers never call the API directly. `web/app/auth/login/route.ts` calls it from
the web server, so the API sees the web server's IP for every user. A plain per-IP limiter at the
API would throttle everyone at once. A forwarded client-IP header could be spoofed by anyone who
reaches the API directly.

**Design.**
1. **Per-account lockout in the database** (main defence, survives restarts and scale-out).
   Migration: `Users.FailedLoginCount INT NOT NULL DEFAULT 0`, `Users.LockedUntil DATETIME2(3) NULL`.
   After 5 failures, lock for 1 minute, doubling per further failure up to 15 minutes. A success
   resets the counter. A locked account gets 429 with `Retry-After`. The lock is short, so an
   attacker can't keep an admin locked out for long. Admin "Reset password" also clears the lock.
2. **Global limiter on `/api/auth/login`** (`AddRateLimiter`, fixed window, about 30 per minute
   total). This stops password spraying across many usernames. It is fine for an invite-only user
   base.
3. Keep the "Wrong username or password" message for unknown users. A 429 does reveal that an
   account exists; that trade-off is acceptable here.

**Adjacent (not in scope).** In Azure, restrict the API app's inbound traffic to the web app
(App Service access restrictions or VNet). That makes a forwarded client IP trustworthy, after which
a per-IP limit becomes possible.

**Tests.** Integration tests (`AuthFlowTests`): 5 wrong passwords → 6th attempt 429 even with the
right password; the lock expires (fake `TimeProvider`); a success resets the count; admin reset
clears the lock.

**Done when.** The tests pass and the migration applies cleanly to an existing database.

---

## P3. Prompt caching for the tutor conversation — S

**Facts (Claude API docs, checked 2026-10).** Minimum cacheable prefix is 512 tokens on
`claude-sonnet-5-5` and `claude-opus-5-5`. Default cache lifetime is 5 minutes, refreshed by every
read. Writes cost 1.25× input; reads cost $0.20/MTok on both models. Caches are per model, so the
Sonnet conversation and the Opus grader never share entries.

**Design.**
- `TutorChat`: send `System` as a `List<TextBlockParam>` with
  `CacheControl = new CacheControlEphemeral()` on the scenario prompt (a stable read point). Also set
  the top-level `MessageCreateParams.CacheControl` (automatic caching), so the breakpoint follows the
  growing conversation. Learner turns are seconds apart, so the 5-minute lifetime is right; no
  1-hour TTL.
- Keep the prefix byte-stable. The scenario JSON and system prompt don't change during a session, so
  this holds. One exception: the time-up note is appended to the last user message's text, so that
  one turn differs on the next request. Better, move it to a mid-conversation `role: "system"`
  message (supported on Sonnet 5.5) appended after the last user turn, which doesn't alter history.
- Grading (`CefrGrader`): one call per session. A cache only pays off if another grading starts
  within 5 minutes, so leave it off by default. Turn it on through a `Assessment:CacheGraderPrompt`
  option for calibration runs (P13), which grade many transcripts back to back.
- Read usage from the stream: the `message_start` event carries `input_tokens`,
  `cache_creation_input_tokens` and `cache_read_input_tokens`, and `message_delta` carries
  `output_tokens`. Return them from `StreamReplyAsync` (e.g. a final usage callback) for P5 to
  record. Use the compiler to confirm the C# event type names.
- While here (one line each): enable the server-side refusal fallback the API now offers for these
  models (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`). It would sit in front of
  the existing `RefusalFallback` text.

**Tests.** A gated E2E test (`[E2EFact(claude: true)]`): run three turns of one session; assert that
`cache_read_input_tokens` > 0 on turns 2 and 3 and grows turn over turn.

**Done when.** That test passes and logs show cache reads covering the prior conversation on each
turn.

---

## P4. Grade in the background — M

**Problem.** `POST /api/sessions/{id}/end` calls Opus at high effort and holds the HTTP request
until it answers. Long sessions can approach App Service's ~230 s request timeout. A Claude failure
returns 500 and stores nothing. The summary page then silently retries on every visit.

**Design.**
- Migration: on `dbo.Sessions` add `GradingState NVARCHAR(20) NULL` (`Pending`, `Running`, `Done`,
  `Failed`), `GradingAttempts INT NOT NULL DEFAULT 0`, `GradingError NVARCHAR(400) NULL` and
  `GradingStartedAt DATETIME2(3) NULL`.
- `POST /end`: end the session. Then:
  - assessment already stored and not `regrade` → 200 with it (unchanged);
  - under the word minimum → grade inline (no Claude call), 200;
  - otherwise set `Pending` and wake the worker → **202** `{ state: "Pending" }`.
- `GradingWorker : BackgroundService`:
  - claims work atomically:
    `UPDATE TOP (1) dbo.Sessions WITH (ROWLOCK, UPDLOCK, READPAST) SET GradingState='Running', GradingStartedAt=SYSUTCDATETIME(), GradingAttempts+=1 OUTPUT inserted.Id WHERE GradingState='Pending' OR (GradingState='Running' AND GradingStartedAt < DATEADD(minute,-10,SYSUTCDATETIME()))`.
    That's safe with several API instances, and it recovers work orphaned by a restart.
  - wakes on a `Channel<Guid>` signal, plus a 30 s poll as a safety net;
  - 5-minute timeout per grading. Up to 3 attempts with backoff for transient errors (429, 5xx,
    timeouts). The SDK already retries twice per call. After that, `Failed` with a short error
    message.
- Extract `ICefrGrader` from `CefrGrader`, so integration tests can swap in a fake grader (fast,
  free, can be told to fail).
- `GET /api/sessions/{id}` adds `grading: { state, error }`.
- Front end: `LiveSession.end()` navigates to the summary as soon as the 202 arrives. `Summary.tsx`
  polls every 3 s while `Pending`/`Running` ("Scoring your session…"). On `Failed` it shows the error
  and a **Try again** button (`POST /end?regrade=true`). The silent retry on load goes away.
- Existing sessions that ended without an assessment: a one-off `UPDATE` in the migration marks them
  `Pending`.

**Tests.** Integration tests with the fake grader: `/end` returns 202 in under a second and the
state reaches `Done`; a failing grader ends in `Failed` after 3 attempts, and retry succeeds; a
`Running` row older than 10 minutes is reclaimed; two concurrent claims → only one wins. One gated
E2E test with real Claude grades end to end.

**Done when.** The tests pass, and a real session's summary page shows the polling state followed by
the grades.

---

## P5. Monitoring and cost tracking — M

**Design.**
- **Tracing and metrics.** Add `Azure.Monitor.OpenTelemetry.AspNetCore`. Call `UseAzureMonitor()`
  when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; otherwise nothing runs (local stays quiet).
  An `ActivitySource("TutorAI")` span wraps each Claude call (model, session id, user id, latency,
  token counts, stop reason). A `Meter("TutorAI")` records:
  - `tutorai.claude.tokens` (tags: model, kind = input/output/cache_read/cache_write, route = chat/grade)
  - `tutorai.turn.first_token_ms`
  - `tutorai.grading.duration_ms`, `tutorai.grading.failures`
  - `tutorai.logins.failed`
- **Token usage stored per call**, so cost per user doesn't depend on App Insights retention.
  Migration: `Turns.InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens INT NULL` (tutor
  turns) and the same four on `Assessments`. Usage comes from P3.
- **Admin view.** The Users page gets a "Tokens (30 days)" column with an estimated cost, using
  prices from configuration (`Pricing:<model>:InputPerMTok`, …) rather than hard-coded values.
- Structured log scopes carry `SessionId`/`UserId`.

**Tests.** Unit test for the cost calculation. Integration test: after a turn with the fake chat
client, the usage columns are filled. Local check with the console exporter shows the spans.

**Done when.** Locally, the console exporter prints a span per Claude call with token counts, and the
Users page shows per-user totals. In Azure, a KQL query on `customMetrics` returns
`tutorai.claude.tokens` (confirm after deploying).

---

## P6. Health check that covers Claude — S

**Design.** Keep `/api/health` cheap (database only), because App Service probes it. Add
`/api/health/ready` (anonymous), which also checks:
- Anthropic: `GET /v1/models/{ConversationModel}`, which is free;
- Azure Speech: issue a token.

Cache the result for 60 s, so probes can't generate traffic. Return each dependency's status and
latency. Never return error details that include secrets.

**Done when.** With a wrong Anthropic key the endpoint returns 503 with `anthropic: false`; with good
keys, 200.

---

## P7. Progress over time — M

**Design.**
- `GET /api/progress?mode=&userId=` (`userId` for admins only) returns one point per graded session:
  date, mode, the five levels plus overall, words per minute, pause ratio and pronunciation
  composite. Pulled from `MetricsJson` with `JSON_VALUE`, so no new columns.
- New `/progress` page: one small step chart per criterion (CEFR levels as ordered steps A1…C2),
  plus words-per-minute and pronunciation lines. Filter by mode. A headline compares the average of
  the last 5 sessions with the 5 before. Inline SVG, no chart library; follow the `dataviz` skill.
- Link from the home page user bar and from each summary page ("See your progress").
- Admins can open any user's progress from the Users page.

**Tests.** Integration test for the endpoint (ordering, ownership: a guest can't pass another
`userId`, sessions without grades excluded). Front-end unit test for the CEFR-to-step mapping (P16
tooling).

**Done when.** A user with 3+ graded sessions sees correct trends, and a guest requesting another
user's id gets 403.

---

## P8. Practice drills from feedback — L

**Design.**
- Migration: `dbo.PracticeItems` (`Id`, `UserId`, `SessionId`, `Kind` = `correction`|`word`,
  `Target` (the corrected sentence or the word), `Original`, `Explanation`, `Box` (0–4), `DueAt`,
  `Attempts`, `BestScore`, `CreatedAt`), unique on (`UserId`, `Kind`, normalised `Target`).
- When an assessment is saved (in the P4 worker), add the session's corrections and weakest words as
  items. Duplicates are merged and reset to due.
- Scoring is scripted: the learner reads `Target` aloud, and Azure pronunciation assessment runs
  **with reference text** (today the app only uses unscripted mode). `SpeechEngine` gets a one-shot
  `assessOnce(referenceText)` method.
- `POST /api/practice/{id}/attempts` with the raw Azure result. The server computes the score with
  `AzurePronunciation` (not trusting a client-sent number). A score of 80+ moves the item up a box;
  due dates 1, 3, 7 and 21 days, then mastered. Under 80 goes back to box 0.
- `GET /api/practice/due` and a `/practice` page: one card at a time with the target, the
  explanation and per-word colouring of the attempt. Due count shown on the home page.

**Tests.** Unit tests for box scheduling and for de-duplication. Integration test for item creation
on grading (fake grader) and for the attempt endpoint with a recorded Azure sample
(`tests/TutorAI.Tests/AzureSamples.cs` already holds examples). Manual voice test.

**Done when.** After a graded session, its corrections and weak words show up as due items. A read
attempt is scored and rescheduled.

---

## P9. Change your own password — S

**Design.**
- `POST /api/auth/password` `{ currentPassword, newPassword }`: verify the current password (a
  failure counts toward the P2 lockout), apply the same rules as the admin path, store with
  `signOut: true` (bumps `TokenVersion`, ending other sessions), and return a **fresh token**, so the
  current browser stays signed in.
- Web: a route `web/app/auth/password/route.ts` (like `login/route.ts`) that forwards the call and
  replaces the cookie. An `/account` page linked from the user bar.
- Built-in admin: the same `configPasswordHash` behaviour as the admin reset path, so the change
  survives restarts.

**Tests.** Integration tests: wrong current password → 400 and counted; success → old token rejected
(401), new token works, new password signs in.

**Done when.** The tests pass and a manual change in the browser keeps that tab signed in while
another browser is signed out.

---

## P10. Choose the accent (locale and voice) — M

**Design.**
- `Scenario.Accent`, checked against a server allowlist that maps to locale and voice, e.g.
  `en-US → en-US-AvaMultilingualNeural`, `en-GB → en-GB-SoniaNeural`,
  `en-AU → en-AU-NatashaNeural`, `en-IN → en-IN-NeerjaNeural`. The allowlist lives in configuration.
- `GET /api/speech/token?sessionId=` returns that session's locale and voice. The default stays
  `en-US`.
- **Check first:** Azure prosody assessment has been available for `en-US` only. If that still
  holds, pronunciation for other accents is accuracy-only. Then `PronunciationAggregator` needs an
  accuracy-only composite, and the `CefrMapper` thresholds (tuned with prosody) must be labelled as
  less reliable for those sessions. Verify against current Azure docs before building.
- The tutor prompt mentions the variety ("use British English vocabulary"), so speech and wording
  match.

**Tests.** Unit tests for the allowlist and the accuracy-only composite. Integration test: an
unknown accent → 400; the token endpoint returns the session's voice.

**Done when.** An en-GB session speaks with a British voice, recognises en-GB, and its summary handles
the missing prosody correctly.

---

## P11. Paginate the session list — S

**Design.**
- Migration: index `IX_Sessions_UserId_StartedAt` on (`UserId`, `StartedAt DESC`) including `Status`.
  Today every list query scans the table.
- `GET /api/sessions?limit=50&before=<cursor>`: keyset pagination on (`StartedAt`, `Id`), with an
  opaque base64 cursor. Response `{ items, nextCursor }`. This changes the response shape, so the web
  client changes in the same commit.
- Home page: **Load more** button.

**Tests.** Integration test: 120 sessions → three pages, no duplicates or gaps, stable under inserts
between pages.

**Done when.** The test passes, and the list query plan shows an index seek.

---

## P12. Printable session report — S (phase A), M (phase B)

**Phase A (recommended now).** `@media print` styles for the summary page: hide navigation, expand
the transcript, avoid page breaks inside cards. Add a **Print / Save as PDF** button
(`window.print()`). No server change.

**Phase B: dropped (2026-10-29). Printing is enough.** The design is kept for reference. Read-only share links: table `dbo.SessionShares` (`Token`
random 32 bytes, `SessionId`, `ExpiresAt`, `RevokedAt`), an anonymous `GET /api/share/{token}`
returning the summary without user details, and a `/share/[token]` page that `proxy.ts` lets
through. Expiry is 14 days by default, revocable by the owner.

**Done when (A).** Chrome's print preview of a graded session is clean on A4 and Letter.

---

## P13. Grading calibration set — M (plus labelling time)

**Design.**
- `tests/TutorAI.Tests/Calibration/samples/*.json`: transcript, fluency metrics and pronunciation
  composite, and the **human-assigned** level per criterion, with the rater noted.
- `CalibrationTests` gated by `TUTORAI_CALIBRATION=1` (costly: one Opus call per sample per repeat).
  It grades each sample 3 times and reports:
  - exact agreement and within-one-step agreement per criterion;
  - bias (does the grader run high or low?);
  - consistency across the 3 repeats.

  It writes a Markdown report to the test output and fails below a set bar (start at 85%
  within-one-step, then tighten).
- Add `PromptVersion` to `Assessments` (migration), set from a constant in `CefrGrader`, so stored
  grades can be traced to the prompt that produced them.
- The same data refits the `CefrMapper` pronunciation thresholds: compare composites against human
  pronunciation levels.
- Use P3's grader caching option during these runs.

**Blocker.** It needs labelled samples. Somewhere around 30–50 sessions, spread across A2–C1, rated
by a qualified teacher. The code can be built before the labels exist; the bar can't be set until
they do.

**Done when.** The suite runs on the labelled set and produces a report. The agreement bar and the
pronunciation thresholds are set from measured results.

---

## P14. Sanity-check client-sent speech scores — S

**Problem.** Pronunciation and timing JSON comes from the browser. A learner could inflate their
scores. That's harmless for self-study, but it matters if results ever feed hiring or certification.

**Design (cheap checks now).** In `PostTurnAsync`/`AzurePronunciation`:
- reject a turn whose phrases' text doesn't roughly match `Text`;
- reject scores outside 0–100 and word timings that aren't increasing;
- reject a turn whose word durations add up to more than its `DurationMs` allows.

Flag a rejected turn (store it without scores) instead of failing the conversation.

**Later, only if grades become high-stakes.** Stream audio to the API and run recognition and
assessment server-side. That's a large change: a WebSocket audio relay and server-side Speech SDK
cost. Not planned.

**Tests.** Unit tests using the real samples in `AzureSamples.cs`, plus tampered copies.

**Done when.** Tampered samples lose their scores and real samples pass unchanged.

---

## P15. Continuous integration — S

**Open choice.** The repo has no remote yet. Both options below work; the jobs are the same.
- GitHub Actions: `.github/workflows/ci.yml`.

**Jobs** (on pull requests and pushes to `main`/`master`):
1. **api:** `dotnet restore`, `dotnet build -warnaserror`, `dotnet test` (unit tests; integration
   tests skip themselves).
2. **integration:** a SQL Server 2022 service container, `TUTORAI_E2E=1`, `TUTORAI_E2E_SQL` pointing
   at it. Tests that need Claude or Azure Speech skip unless those secrets are configured in the
   pipeline. Leave them out at first to keep cost at zero.
3. **web:** `npm ci`, `npm run lint`, `npx tsc --noEmit`, `npm run build`. Later also the P16 tests.

Deployment stays manual (ReadMe step 9) for now. Making it a pipeline stage is a separate decision.

**Done when.** A pull request shows all three jobs passing, and a deliberately broken test turns the
pipeline red.

---

## P16. Front-end tests — M

**Design.**
- Add Vitest with jsdom.
- Extract the pure logic so it can be tested without a microphone:
  - the server-sent event parser from `streamTurn` → `lib/sse.ts`;
  - `SentenceBuffer` (already separate in `lib/speech.ts`);
  - the live-session phase transitions → a reducer `lib/session-machine.ts`, used by
    `LiveSession.tsx` through `useReducer`. Behaviour must not change; the tests below pin it.
- Unit tests: SSE parsing (split chunks, multi-line data, error events), sentence splitting, phase
  transitions (interrupt during tutor speech, pause/resume with a pending phrase, typed turn while
  paused, end during streaming).
- Playwright smoke test (gated, needs the real API and database): sign in, start a session with
  voice off, type turns, end, see the summary. Uses the P4 fake grader through a test-only setting,
  so no Claude cost.

**Done when.** `npm test` passes in CI, and the reducer refactor causes no change in a manual voice
session.

---

## Open questions

1. **CI host:** GitHub or Azure DevOps? (P15)
2. **Labelled transcripts:** who rates the calibration sessions, and how many can they do? (P13)
3. **Share links:** decided 2026-10-29, printing is enough; P12 phase B is dropped.
4. **Cost limits:** decided 2026-10-29, keep the shipped defaults (150 learner turns, 4000 characters and
   100 phrases per turn, 5-minute grace, 20 turns a minute). Review them against the Users page's 30-day
   usage once guests have used the app for a couple of weeks; each is a `Sessions:*` setting.
