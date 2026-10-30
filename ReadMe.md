# TutorAI

A speaking tutor: the learner talks with Claude in a conversation or mock interview, Azure AI Speech
handles speech-to-text, text-to-speech and pronunciation scoring, and at the end the session is graded
on the CEFR scale. An interview-prep agent plans practice from a job description, and an MCP server
lets Claude or Copilot read the learner's progress.

| Part | Path | Tech |
| --- | --- | --- |
| API | `src/TutorAI.Api` | ASP.NET Core minimal API, .NET 10 |
| Database migrations | `src/TutorAI.Migrations` | DbUp console app (journals to `dbo.SchemaVersions`) |
| Web front end | `web` | Next.js 16 (proxies `/api/*` to the API) |
| Tests | `tests/TutorAI.Tests` | xUnit (integration tests gated by `TUTORAI_E2E=1`) |

## Design decisions

- **Model vs code.** Claude makes the judgement calls: the tutor's replies, the CEFR levels for fluency,
  grammar, vocabulary and interaction, and the prep agent's choice of questions, phrases and tool calls.
  Code does everything that has to be exact, repeatable or safe: the pronunciation level and the overall
  median ([CefrMapper](src/TutorAI.Api/Assessment/CefrMapper.cs)), practice scoring and scheduling
  ([Practice.cs](src/TutorAI.Api/Practice/Practice.cs)), checking browser-sent scores
  ([TurnScoreCheck](src/TutorAI.Api/Assessment/TurnScoreCheck.cs)), and every write the agent makes. The
  agent's tools validate their arguments, touch only the caller's own data, and return errors to the
  model so it can correct itself ([PrepTools](src/TutorAI.Api/Prep/PrepTools.cs)).
- **Failure handling.** Grading runs in a durable background queue with retries and recovers jobs from a
  dead instance ([GradingWorker](src/TutorAI.Api/Assessment/GradingWorker.cs)). Every agent write is
  recorded under an idempotency key, so a repeated call writes once; a retried `POST /api/prep/runs`
  with the same `Idempotency-Key` returns the first run. Irreversible actions are only proposed, and
  run after the learner confirms them ([PrepService](src/TutorAI.Api/Prep/PrepService.cs)). A run that
  fails or hits a limit is still recorded, with what it did before it stopped
  ([PrepAgent](src/TutorAI.Api/Prep/PrepAgent.cs)).
- **Limits.** Each session has a length, turn count, turn size and turn rate cap
  ([SessionRules](src/TutorAI.Api/Conversation/SessionRules.cs)), and guests have a session allowance.
  Each agent run has a step limit, a token budget and a time limit
  ([AgentBudget](src/TutorAI.Api/Prep/AgentBudget.cs)); guests get 3 runs a day. Sign-in has lockout and
  rate limits ([Program.cs](src/TutorAI.Api/Program.cs)).
- **Evals.** The grader is checked against teachers' ratings
  ([Calibration](tests/TutorAI.Tests/Calibration)). The prep agent has 25 eval cases, from ordinary
  postings to prompt injection and non-job input, run against Claude on demand locally or in CI
  ([AgentEvals](tests/TutorAI.Tests/AgentEvals), [agent-evals.yml](.github/workflows/agent-evals.yml)).
  Its guards are unit-tested with a scripted model, so CI checks them on every push
  ([PrepAgentTests](tests/TutorAI.Tests/Services/PrepAgentTests.cs)).
- **Tracing.** OpenTelemetry to Application Insights: a span per Claude call, a span per agent run with
  one per model call and per tool call, and token metrics by model and route
  ([Telemetry](src/TutorAI.Api/Telemetry.cs)). Token counts are also stored with every turn, assessment
  and agent run, so cost per user doesn't depend on telemetry retention.

The Azure deployment guide is in [docs/deployment.md](docs/deployment.md).

## Configuration and secrets

The API reads these settings. In Azure the five secrets live in **Azure Key Vault**; nothing secret
goes in `appsettings.json` or App Service app settings.

| Setting | Secret? | Key Vault secret name | Where it's set in Azure |
| --- | --- | --- | --- |
| `Anthropic:ApiKey` | yes | `Anthropic--ApiKey` | Key Vault |
| `AzureSpeech:Key` | yes | `AzureSpeech--Key` | Key Vault |
| `ConnectionStrings:TutorAI` | yes | `ConnectionStrings--TutorAI` | Key Vault |
| `Auth:JwtSigningKey` | yes | `Auth--JwtSigningKey` | Key Vault (32+ random characters) |
| `Auth:AdminPassword` | yes | `Auth--AdminPassword` | Key Vault |
| `KeyVault:Uri` | no | – | App setting `KeyVault__Uri` |
| `AzureSpeech:Region` | no | – | App setting `AzureSpeech__Region` |
| `Anthropic:ConversationModel` / `GradingModel` | no | – | `appsettings.json` (override with app settings if needed) |
| `Auth:AdminUsername` / `TokenLifetimeMinutes` | no | – | `appsettings.json` (`admin`, `15`) |
| `Auth:McpTokenDays` | no | – | `appsettings.json` default `30` (see [MCP server](#mcp-server)) |
| `Prep:Model`, `MaxSteps`, `MaxTokens`, `RunTimeoutSeconds`, `RunsPerDay` | no | – | `appsettings.json` (see [Interview-prep agent](#interview-prep-agent)) |
| `AzureSpeech:Accents` | no | – | `appsettings.json` (see [Accents](#accents)) |
| `Assessment:CacheGraderPrompt` | no | – | Off. Caches the grader's prompt; only worth it for back-to-back gradings (see [Grading calibration](#grading-calibration)) |

How it works: when `KeyVault:Uri` is set, `Program.cs` adds Key Vault as a configuration source and
signs in with `DefaultAzureCredential` (the App Service managed identity in Azure, your `az login`
locally). Key Vault secret names can't contain `:`, so `--` stands in for it
(`Anthropic--ApiKey` → `Anthropic:ApiKey`). When `KeyVault:Uri` is empty, Key Vault is skipped and
local user-secrets are used.

Secrets are read once at startup. After rotating a secret, restart the API web app.

## Sign-in and users

Every page needs a sign-in. On first startup the API creates one built-in account, `admin`, from
`Auth:AdminUsername` and `Auth:AdminPassword`. Everyone else, guests included, is added by an admin on
the Users page.

| Role | Sessions |
| --- | --- |
| Admin | Unlimited. Sees every user's sessions and manages users. |
| Guest | Up to the limit set for that user (each **Start session** counts, finished or not). Sees only their own sessions. |

### Managing users

Admins get a **Manage users** link (`/users`) where they can:

- **Add a user** with a role (Admin or Guest) and, for guests, a session limit.
- **Disable / Enable** a user. A disabled user can't sign in and is signed out on their next request.
- **Reset password.** The user is signed out everywhere.
- **Change limit** and **Reset sessions** (guests). A reset gives back the full allowance; past
  sessions stay in their history.
- **Delete** a user. Their past sessions are kept, without an owner, and only admins see them.

Admins can't disable, demote or delete their own account, and the last enabled admin can't be removed.
Changing a role also signs the user out.

Every user can change their own password on the **Account** page (`/account`, linked from the user
bar; `POST /api/auth/password`). It needs the current password, and a wrong one counts toward the
sign-in lockout. The change signs the user out on every other browser and device but keeps the current
one signed in.

Passwords are stored hashed (ASP.NET Core Identity's PBKDF2 hasher) in `dbo.Users`, at least 8
characters. After the first startup the Users page owns the built-in admin too: a password set on the
page survives restarts. Changing `Auth:AdminPassword` and restarting the API still resets the admin to
the new value. That's the way back in if the admin password is lost.

### Tokens

1. The `/login` page posts to the web app (`/auth/login`), which calls the API's `POST /api/auth/login`.
2. The API checks the password and returns a JWT signed with `Auth:JwtSigningKey` (HS256) that expires
   after **15 minutes** (`Auth:TokenLifetimeMinutes`).
3. The web app stores the JWT in an httpOnly cookie, so browser scripts can't read it.
4. Every `/api/*` call goes through `web/app/api/[...path]/route.ts`, which forwards it to the API with
   `Authorization: Bearer <jwt>`. Once a token has less than 10 minutes left, the web app swaps in a
   fresh one (`POST /api/auth/refresh`), so an active user stays signed in and **15 minutes of
   inactivity signs the user out**. `web/proxy.ts` does the same for page loads, sends signed-out
   visitors to `/login`, and keeps non-admins out of `/users`.
5. The API rejects any request without a valid token (401), except `/api/health`,
   `/api/health/ready` and `/api/auth/login`. Each token carries the user's `TokenVersion`, checked against `dbo.Users` on
   every request. That's how disabling a user, resetting a password or changing a role takes effect
   immediately instead of when the token expires.

### Sign-in protection

- **Account lockout.** After 5 wrong passwords in a row (`Auth:LockoutThreshold`) the account is
  locked for 1 minute, even against the right password. Each further wrong password doubles the lock,
  up to 15 minutes (`Auth:LockoutBaseSeconds`, `Auth:LockoutMaxSeconds`). A locked sign-in gets 429
  with a `Retry-After` header. A successful sign-in or an admin **Reset password** clears the count.
  Unknown usernames aren't counted and get the same "Wrong username or password" message.
- **Overall limit.** At most 30 sign-in attempts per minute across all users
  (`Auth:LoginAttemptsPerMinute`), so many usernames can't be tried at once. The API sees every
  attempt from the web server's address, so this limit is global rather than per visitor.

## Session limits

These bound what a single session can cost. Settings are in the `Sessions` section of
`appsettings.json`.

| Limit | Default | When it's reached |
| --- | --- | --- |
| Planned length | 5–60 minutes | Creating a session with another value gets 400. |
| Time | planned length + 5 minutes (`GraceMinutes`) | The tutor wraps up at the planned time. After the grace period, turns get 409 `time_up` and the live page ends and scores the session. |
| Learner turns | 150 (`MaxUserTurns`) | 409 `turn_limit`; the live page ends and scores the session. |
| Turn length | 4000 characters, 100 recognized phrases | 400 `turn_too_long`. The live page sends a long spoken turn early, before it gets there. |
| Request size | 4 MB (`MaxRequestBytes`) | 413. Applies to every API request. |
| Turn rate | 20 per user per minute (`TurnsPerMinute`) | 429. |
| Scenario text | 200 characters per field, 8000 for the job description | 400. |

The tutor's replies use prompt caching. Each turn reads the scenario prompt and the conversation so
far from Claude's cache (5-minute lifetime, renewed by every turn), so only the newest messages are
billed at the full input price. Token counts, including cache reads and writes, are stored with each
tutor turn and assessment (see [Monitoring and cost](docs/deployment.md#monitoring-and-cost)).

## How sessions are evaluated

Ending a session (`POST /api/sessions/{id}/end`) grades it on the CEFR scale: A1, A2, A2+, B1, B1+, B2,
B2+, C1, C2. The grade combines measurements taken from the audio with a Claude examiner reading the
transcript. Code: `src/TutorAI.Api/Assessment`.

1. **Evidence collected during the session.** Azure AI Speech transcribes each learner turn and runs
   pronunciation assessment on it: accuracy and prosody scores, plus per-word timings and error types.
   Only the learner's turns are measured and graded.

   Recognition runs in the browser, so the API checks each turn's results before storing them
   (`TurnScoreCheck`). Every score must be within 0-100, and word timings must be in order. The words
   must last no longer than the turn did, give or take a few seconds. At least 70% of the assessed
   words must appear in the turn's text. A turn that fails is kept, but without scores
   (`Turns.ScoresRejected` says why), so it counts toward the transcript and not toward pronunciation
   or fluency. These checks catch edited or mismatched results, not a careful forgery. That would need
   recognition on the server, which isn't planned while grades are for self-study.
2. **Minimum amount of speech.** Below 150 learner words (`Assessment:MinUserWords`), the session is
   reported as too short and no levels are given.
3. **Measurements computed in code.**
   - Fluency (`FluencyMetrics`), from word timings: words per minute, pause ratio (gaps of 0.25 s or
     more), long pauses (1 s or more) per minute, mean words between pauses, fillers ("um", "uh", ...)
     per 100 words, and Azure's fluency score.
   - Pronunciation (`PronunciationAggregator`): composite = 0.6 × accuracy + 0.4 × prosody, plus up to
     10 weakest words (accuracy below 60 or flagged as mispronounced).
4. **Grading.**
   - **Fluency, Grammar, Vocabulary and Interaction** are graded by Claude (`Anthropic:GradingModel`,
     `claude-opus-5-5` by default, high effort) acting as a CEFR oral examiner (`CefrGrader`). The
     prompt:
     - gives it the CEFR descriptors for each criterion and the fluency measurements;
     - treats speaking rate as a rough guide only (about 90 wpm or less suggests A2-B1, 110-140 B2,
       140+ C1-C2);
     - tells it to ignore punctuation from speech recognition and likely mis-recognitions;
     - in interview mode, also asks whether answers address the question and are developed.

     The reply is held to a JSON schema. For each criterion it gives a level, a confidence (low, medium
     or high), 2-4 verbatim learner quotes as evidence, and advice for reaching the next level. It also
     gives a short summary and up to 6 corrections.
   - **Pronunciation** is mapped from the composite score (`CefrMapper`): 95+ C2, 90+ C1, 85+ B2+,
     80+ B2, 74+ B1+, 68+ B1, 60+ A2+, 50+ A2, otherwise A1. These thresholds are a heuristic; Azure
     publishes no CEFR mapping. Sessions with no assessed audio (typed input only) get no pronunciation
     level.
5. **Overall level.** The median of the available levels, taking the lower middle for an even count.
   A single outlying criterion doesn't swing the result.

The result, with the measurements and Claude's detailed output, is stored in `dbo.Assessments` and
shown on the session summary page. Ending the same session again returns the stored result. Pass
`?regrade=true` to grade it again. **Print / Save as PDF** on the summary page prints a clean report
with the transcript expanded.

**Progress** (`/progress`, from the user bar) charts each graded session's levels, speaking rate and
pronunciation score over time, and compares the last 5 sessions with the 5 before. Admins can open
any user's progress from the Users page (`GET /api/progress?userId=`).

### Practice drills

Grading a session adds its corrections (the corrected sentence) and its weakest words to the learner's
**Practice** list (`/practice`, linked from the user bar; the home page shows how many are due). A target
the learner already has is merged into the existing item, which starts again from the first step.

The learner reads each item aloud. The browser runs Azure's pronunciation assessment **with the target as
reference text**, and the API scores the raw result itself (`ScriptedAssessment`). The score is the mean
word accuracy, with left-out words counting zero. A result that doesn't cover the target (another
sentence was read) is refused. A score of 80 or more moves the item up a step: the next review is 1, 3,
7 and then 21 days later, and passing the fifth step masters it. A lower score sends it back to the first
step, due again at once. Items are practised in the accent of the session they came from.

API: `GET /api/practice/due`, `POST /api/practice/{id}/attempts` with `{ phrases }` (Azure's detailed
results), `DELETE /api/practice/{id}`. Items live in `dbo.PracticeItems` and are deleted with their
user.

### Accents

Learners pick the English they practise when they start a session: American (the default), British,
Australian or Indian English. The accent sets the speech recognition locale, the tutor's voice and a
note in the tutor's prompt (for example British vocabulary and spelling). The allowlist is
`AzureSpeech:Accents` in `appsettings.json`, keyed by locale, each with a `Label`, a `Voice` (an Azure
neural voice name) and an optional `PromptNote`.

Azure scores **prosody** (rhythm and intonation) for `en-US` only. Other accents are assessed on
accuracy and fluency, so their pronunciation level comes from accuracy alone. That runs higher than the
blended score and is less reliable, and the summary page says so.

### Grading runs in the background

Grading a long session takes a while, so `/end` doesn't wait for it:

- A session too short to grade gets its result at once (`200`, no Claude call).
- Otherwise `/end` answers `202 { "state": "Pending" }` and a background worker (`GradingWorker`)
  grades it. `GET /api/sessions/{id}` reports `grading: { state, error }`, with state `Pending`,
  `Running`, `Done` or `Failed`. The summary page polls every 3 seconds and shows the levels when
  they're ready.
- A failed attempt is retried after 15 s, then 60 s (`Assessment:RetryDelaySeconds`), 3 attempts in
  all (`Assessment:MaxGradingAttempts`), each limited to 5 minutes. After the last one the summary
  page shows the error and a **Try again** button, which calls `/end` again.
- Jobs live in `dbo.Sessions`, so they survive restarts. A job left `Running` for more than 10 minutes
  (an instance died mid-grading) is picked up again. With several API instances, each job goes to
  exactly one of them.
- The worker reads the queue only when there may be work: at startup, when a session ends, and when a
  retry falls due. It doesn't poll while idle, so a serverless database can still pause.

### Grading calibration

`tests/TutorAI.Tests/Calibration` checks the grader against human ratings. Each sample in
`Calibration/samples/*.json` is one session rated by a qualified teacher:

```jsonc
{
  "id": "travel-b1-01",
  "rater": "Name, qualification",
  "mode": "Conversation",                      // or Interview
  "scenario": { "topic": "Travel", "durationMinutes": 10 },
  "turns": [ { "role": "tutor", "text": "..." }, { "role": "learner", "text": "..." } ],
  "fluency": { ... },                          // optional: the session's stored metrics.fluency
  "pronunciationComposite": 74.2,              // optional: metrics.pronunciation.composite
  "levels": { "fluency": "B1", "grammar": "A2+", "vocabulary": "B1", "interaction": "B1",
              "pronunciation": "B1" }          // pronunciation optional
}
```

Run it with `$env:TUTORAI_CALIBRATION = "1"; dotnet test --filter Category=Calibration`. It grades each
sample 3 times (`TUTORAI_CALIBRATION_REPEATS`) with the real grader and writes a Markdown report to the
test output and to `bin/.../TestResults/calibration/`. The report covers each criterion's exact and
within-one-step agreement, its bias and how consistent the repeats are. It also gives suggested
pronunciation thresholds from the rated samples. The run fails if any criterion is within one step in
fewer than 85% of gradings (`TUTORAI_CALIBRATION_BAR`). It costs one Opus call per sample per repeat,
about $0.05 each, with the grader prompt cached. `TUTORAI_CALIBRATION_DIR` points it at another folder.

No rated samples are checked in yet; around 30-50 sessions spread across A2-C1 are needed. Each
assessment stores the grader's `PromptVersion` (`CefrGrader.CurrentPromptVersion`). Change that constant
whenever the prompt changes, so stored grades can be traced and calibration results compared.

## Interview-prep agent

**Interview prep** (`/prep`, from the user bar) takes a job description and runs an agent built with
Microsoft Agent Framework over Claude (`Prep:Model`, Sonnet). Code: `src/TutorAI.Api/Prep`.

The agent reads the learner's weak areas, picks the questions an interviewer for that job would ask,
adds phrases to the practice list and schedules a mock-interview drill. Its tools:

| Tool | Effect |
| --- | --- |
| `get_weak_areas` | Reads the latest graded sessions (levels, advice, corrections) and the practice items |
| `plan_questions` | Stores 3-8 questions for the run |
| `add_practice_items` | Adds `phrase` practice items; ones the learner already has are skipped, never merged |
| `schedule_drill` | Schedules an interview drill that asks the planned questions |
| `restart_practice_item` | Proposes sending a mastered or advanced item back to step 1. It can't be undone, so it waits for the learner's **Restart it** / **Keep my progress** |

Starting a drill creates an interview session (it counts toward a guest's allowance) whose tutor works
through the planned questions.

Guards, all in code:

- **Idempotency.** Each write is stored in `dbo.PrepActions` under a hash of the tool and its normalised
  arguments, unique per run, so the same call twice writes once. `POST /api/prep/runs` accepts an
  `Idempotency-Key` header: a retry returns the first run instead of starting (and paying for) another.
- **Budget.** At most `Prep:MaxSteps` (8) model calls, `Prep:MaxTokens` (60 000) tokens and
  `Prep:RunTimeoutSeconds` (120) per run. A run that hits one ends `Stopped`, keeping what it did.
- **Confirmation.** `restart_practice_item` only records a pending action;
  `POST /api/prep/runs/{id}/actions/{actionId}/confirm` (or `/reject`) applies it once, without a model call.
- **Cost.** Guests get `Prep:RunsPerDay` (3) runs in any 24 hours, and everyone gets 2 run requests a
  minute. Prep tokens show on the Users page with the session costs.
- **Untrusted input.** The job description is passed as fenced data, and the tools can only touch the
  caller's own items.

API: `POST /api/prep/runs` (`{ jobDescription, role?, seniority? }`), `GET /api/prep/runs`,
`GET /api/prep/runs/{id}`, the confirm/reject calls above, `GET /api/prep/drills`,
`POST /api/prep/drills/{id}/start` and `/cancel`.

### Agent evals

`tests/TutorAI.Tests/AgentEvals` runs the agent against Claude on 25 cases in `AgentEvals/cases/*.json`.
Each case gives a job description, the learner's sessions and practice items (in memory, no database),
and what a good run does: which tools it calls or must not call, how many questions and phrases, words
the questions should mention, whether a drill or a restart is expected. Cases include prompt injection
in the job description, input that isn't a job description, and a request for other users' data.
Every run is also checked for completion, the step limit, at most one drill and no prompt leak.

Run it with `$env:TUTORAI_AGENT_EVALS = "1"; dotnet test --filter Category=AgentEval`. It writes a
Markdown report to the test output and `bin/.../TestResults/agent-evals/`, and fails when fewer than 90%
of checks pass (`TUTORAI_AGENT_EVALS_BAR`). `TUTORAI_AGENT_EVALS_FILTER` runs only matching case ids and
`TUTORAI_AGENT_EVALS_REPEATS` runs each case more than once. A full run costs about $1.25 with Sonnet.
In GitHub, run the **Agent evals** workflow by hand; it needs the `ANTHROPIC_API_KEY` secret and uploads
the report. The cases' format and the scoring are unit-tested on every push, without Claude.

## MCP server

The API serves a read-only [MCP](https://modelcontextprotocol.io) server at `/mcp` (streamable HTTP,
stateless), so Claude Code, Claude Desktop or GitHub Copilot can query the learner's own data. Code:
`src/TutorAI.Api/Mcp`.

| Tool | Returns |
| --- | --- |
| `get_progress` | Graded sessions over time: levels per criterion, speaking rate, pause ratio, pronunciation score |
| `list_practice_items` | Practice items, weakest first, with step, due date and scores, plus due/active/mastered counts |
| `list_sessions` | Sessions, newest first, with scenario and levels |
| `get_session` | One session's transcript, levels, summary, advice and corrections |

Access uses its own token: **Create MCP token** on the Account page (`POST /api/auth/mcp-token`) issues
a read-only token (audience `TutorAI.Mcp`, scope `mcp.read`) that lasts `Auth:McpTokenDays` (30) days.
`/mcp` accepts only that token, and the rest of the API rejects it. Changing the password, or an admin
disabling or deleting the user, revokes it at once. Tools always read the token's own user, admins
included. Calls are limited to 60 a minute per user.

```powershell
claude mcp add --transport http tutorai http://localhost:5080/mcp --header "Authorization: Bearer <token>"
```

In Azure, use the API's address (`https://<api-app>.azurewebsites.net/mcp`).

## Run locally

```powershell
docker compose up -d                                   # SQL Server on localhost:14333
dotnet run --project src/TutorAI.Migrations            # create the database and apply scripts

cd src/TutorAI.Api
dotnet user-secrets set "Anthropic:ApiKey"   "<anthropic-key>"
dotnet user-secrets set "AzureSpeech:Key"    "<speech-key>"
dotnet user-secrets set "AzureSpeech:Region" "<speech-region, e.g. eastus>"
dotnet user-secrets set "Auth:JwtSigningKey" "<32+ random characters>"
dotnet user-secrets set "Auth:AdminPassword" "<admin password>"
dotnet run                                             # http://localhost:5080
cd ../..

cd web
npm install
npm run dev                                            # http://localhost:3000
```

Tests: `dotnet test` runs the unit tests, including the turn and grading services against in-memory fakes of
the repository interfaces (`tests/TutorAI.Tests/Services`). With the SQL container up, `$env:TUTORAI_E2E = "1"`
also runs the integration tests against throwaway databases. Tests that call Claude or Azure Speech run only when
those keys are in user-secrets. CI (`.github/workflows/ci.yml`) runs the unit and integration tests
against a SQL Server service container, without Claude or Speech keys. It also lints, type-checks,
unit-tests and builds the web app.

Web tests: `npm test` (in `web/`) runs the unit tests (Vitest): the event-stream parser, sentence
splitting for speech, the live session's phase changes, and the progress and practice helpers. `npm run test:e2e` runs a browser smoke
test (Playwright, using the installed Chrome) against a running stack. It signs in, types a
conversation, ends it and waits for the grades. It runs only when `TUTORAI_E2E_WEB` (e.g.
`http://localhost:3000`), `TUTORAI_E2E_USER` and `TUTORAI_E2E_PASSWORD` are set. It calls Claude for
real (about $0.10 a run).

To run locally against the Azure Key Vault instead of user-secrets: `az login`, give your account the
**Key Vault Secrets User** role on the vault, and set `dotnet user-secrets set "KeyVault:Uri" "https://<vault>.vault.azure.net/"`.
The launch profile sets `AZURE_TOKEN_CREDENTIALS=dev`, so locally the API signs in with developer
credentials only (Azure CLI, Visual Studio, ...). Without it, the managed-identity probe, which only
works inside Azure, adds about two minutes to every startup.

## Deploy

See [docs/deployment.md](docs/deployment.md) for the Azure deployment, updates, monitoring and cost,
hardening and tear-down.
