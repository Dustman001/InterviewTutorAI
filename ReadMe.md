# TutorAI

A speaking tutor: the learner talks with Claude in a conversation or mock interview, Azure AI Speech
handles speech-to-text, text-to-speech and pronunciation scoring, and at the end the session is graded
on the CEFR scale.

| Part | Path | Tech |
| --- | --- | --- |
| API | `src/TutorAI.Api` | ASP.NET Core minimal API, .NET 10 |
| Database migrations | `src/TutorAI.Migrations` | DbUp console app (journals to `dbo.SchemaVersions`) |
| Web front end | `web` | Next.js 16 (proxies `/api/*` to the API) |
| Tests | `tests/TutorAI.Tests` | xUnit (integration tests gated by `TUTORAI_E2E=1`) |

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
tutor turn and assessment (see [Monitoring and cost](#monitoring-and-cost)).

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

## Deploy to Azure

### Target layout

```
Browser ──HTTPS──> Web App (Next.js, Node 24) ──/api/*──> Web App (TutorAI.Api, .NET 10)
                                                              │  managed identity
                                                              ├──> Azure Key Vault (3 secrets)
                                                              ├──> Azure SQL Database
                                                              ├──> Azure AI Speech (token exchange)
                                                              └──> Anthropic API
Browser ──WebSocket──> Azure AI Speech (short-lived token issued by the API)
```

All resources go in one resource group, so tearing down is a single command.

### Prerequisites

- An Azure subscription where you can create resources and assign roles (Owner, or Contributor +
  User Access Administrator).
- Azure CLI 2.60+ (`az login` done), .NET 10 SDK, Node.js 22+.
- An Anthropic API key (<https://console.anthropic.com>).

The commands below are PowerShell, run from the repository root.

### 1. Pick names

Key Vault, SQL server and web app names must be globally unique. Change the suffix.

```powershell
$suffix      = "ip01"                       # make this unique
$loc         = "eastus"                     # also used as the Speech region
$rg          = "rg-tutorai"
$kv          = "kv-tutorai-$suffix"
$speech      = "speech-tutorai-$suffix"
$sqlServer   = "sql-tutorai-$suffix"
$sqlAdmin    = "tutoraiadmin"
$sqlPassword = "<strong password>"          # 8+ chars, upper, lower, digit, symbol
$plan        = "plan-tutorai"
$apiApp      = "tutorai-api-$suffix"
$webApp      = "tutorai-web-$suffix"
```

### 2. Resource group

```powershell
az group create -n $rg -l $loc
```

### 3. Key Vault

The vault uses Azure RBAC. Give yourself **Key Vault Secrets Officer** so you can write secrets.

```powershell
az keyvault create -n $kv -g $rg -l $loc --enable-rbac-authorization true
$kvId = az keyvault show -n $kv -g $rg --query id -o tsv
$me   = az ad signed-in-user show --query id -o tsv
az role assignment create --role "Key Vault Secrets Officer" `
  --assignee-object-id $me --assignee-principal-type User --scope $kvId
```

Role assignments can take a minute or two to take effect. If the next `secret set` returns
`Forbidden`, wait and retry.

### 4. Azure AI Speech

```powershell
az cognitiveservices account create -n $speech -g $rg -l $loc --kind SpeechServices --sku S0 --yes
$speechKey = az cognitiveservices account keys list -n $speech -g $rg --query key1 -o tsv
az keyvault secret set --vault-name $kv -n "AzureSpeech--Key" --value $speechKey | Out-Null
```

### 5. Azure SQL Database

If `az sql server create` fails with `RegionDoesNotAllowProvisioning`, that region isn't taking new SQL
servers. Pick another (for example `centralus`), with a **new server name**, since the failed name stays
reserved for a while. Put the App Service plan in the same region as the database.

Create the database explicitly. The migrator creates the database if it's missing, but on Azure SQL a
bare `CREATE DATABASE` gets a default General Purpose tier that costs far more than this app needs.

```powershell
az sql server create -n $sqlServer -g $rg -l $loc --admin-user $sqlAdmin --admin-password $sqlPassword
az sql db create -g $rg -s $sqlServer -n TutorAI --service-objective Basic
# Or, free (up to 10 per subscription, each with its own monthly free amount; pauses when that is used up). The free amount
# lasts only if the database auto-pauses when idle, so nothing may query it on a timer.
# /api/health deliberately doesn't (see step 8).
# az sql db create -g $rg -s $sqlServer -n TutorAI --edition GeneralPurpose --compute-model Serverless `
#   --family Gen5 --capacity 2 --use-free-limit --free-limit-exhaustion-behavior AutoPause

# Let Azure services (the API web app) reach the server
az sql server firewall-rule create -g $rg -s $sqlServer -n AllowAzureServices `
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

$conn = "Server=tcp:$sqlServer.database.windows.net,1433;Database=TutorAI;User ID=$sqlAdmin;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
az keyvault secret set --vault-name $kv -n "ConnectionStrings--TutorAI" --value $conn | Out-Null
```

### 6. Anthropic API key and sign-in secrets

```powershell
az keyvault secret set --vault-name $kv -n "Anthropic--ApiKey" --value "<anthropic-key>" | Out-Null

# A random JWT signing key; nobody needs to know it.
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
az keyvault secret set --vault-name $kv -n "Auth--JwtSigningKey" --value ([Convert]::ToBase64String($bytes)) | Out-Null

az keyvault secret set --vault-name $kv -n "Auth--AdminPassword" --value "<admin password>" | Out-Null
```

Check the vault now holds all five secrets:

```powershell
az keyvault secret list --vault-name $kv --query "[].name" -o tsv
# Anthropic--ApiKey, Auth--AdminPassword, Auth--JwtSigningKey, AzureSpeech--Key,
# ConnectionStrings--TutorAI
```

### 7. Run the database migrations

Run the migrator from your machine. Open the SQL firewall to your IP first.

```powershell
$myIp = (Invoke-RestMethod https://api.ipify.org)
az sql server firewall-rule create -g $rg -s $sqlServer -n "deploy-$($env:USERNAME)" `
  --start-ip-address $myIp --end-ip-address $myIp

dotnet run --project src/TutorAI.Migrations -- "$conn"
# "Applied N script(s)."  Running it again prints "Database is up to date."
```

Run this step again whenever a new script is added under `src/TutorAI.Migrations/Scripts`. You can
remove your IP rule afterwards:
`az sql server firewall-rule delete -g $rg -s $sqlServer -n "deploy-$($env:USERNAME)"`.

### 8. App Service plan and API web app

```powershell
az appservice plan create -n $plan -g $rg -l $loc --is-linux --sku B1
az webapp create -n $apiApp -g $rg -p $plan --runtime "DOTNETCORE:10.0"

# Managed identity, allowed to read secrets
$apiPrincipal = az webapp identity assign -n $apiApp -g $rg --query principalId -o tsv
az role assignment create --role "Key Vault Secrets User" `
  --assignee-object-id $apiPrincipal --assignee-principal-type ServicePrincipal --scope $kvId

az webapp config appsettings set -n $apiApp -g $rg --settings `
  "KeyVault__Uri=https://$kv.vault.azure.net/" `
  "AzureSpeech__Region=$loc" `
  "ASPNETCORE_ENVIRONMENT=Production"

az webapp config set -n $apiApp -g $rg --always-on true --https-only true
'{"healthCheckPath": "/api/health"}' | Set-Content -Encoding ascii healthcheck.json
az webapp config set -n $apiApp -g $rg --generic-configurations "@healthcheck.json"
Remove-Item healthcheck.json
```

Deploy the API:

```powershell
dotnet publish src/TutorAI.Api -c Release -o publish/api
tar.exe -a -c -f publish/api.zip -C publish/api .
az webapp deploy -g $rg -n $apiApp --src-path publish/api.zip --type zip
```

Check it:

```powershell
Invoke-RestMethod "https://$apiApp.azurewebsites.net/api/health/ready"
# ready: True   database, anthropic and speech each { ok: True, latencyMs, detail }
```

`/api/health` is what App Service probes about once a minute. It only says the process is up, and it
never touches the database: a query every minute would keep a serverless database awake around the
clock. `/api/health/ready` checks the database, the Anthropic API (a free model lookup) and Azure
Speech. It answers 503 if any of them fails and caches its result for 60 seconds.

If the API fails to start, it's usually Key Vault access: the role assignment hasn't propagated yet
(restart the app after a couple of minutes) or `KeyVault__Uri` is wrong. See the log stream:
`az webapp log tail -n $apiApp -g $rg`.

### 9. Web front end

The Next.js app forwards `/api/*` to the API at the address in `TUTORAI_API_URL`, read at request
time (changing it needs a restart, not a redeploy).

The app is built **on your machine** and deployed as a self-contained `standalone` server
(`output: "standalone"` in `web/next.config.ts`). Don't let App Service build it: on a B1 plan,
`npm install` plus `next build` takes over 15 minutes and gets killed.

Create and configure the app first. Settings changes restart the deployment service, so let them settle
for a minute before deploying.

```powershell
az webapp create -n $webApp -g $rg -p $plan --runtime "NODE:24-lts"
az webapp config appsettings set -n $webApp -g $rg --settings `
  "TUTORAI_API_URL=https://$apiApp.azurewebsites.net" `
  "SCM_DO_BUILD_DURING_DEPLOYMENT=false" "HOSTNAME=0.0.0.0" "NODE_ENV=production"
az webapp config set -n $webApp -g $rg --startup-file "node server.js" --always-on true
az webapp update -n $webApp -g $rg --https-only true
```

Build, package and deploy:

```powershell
cd web; npm ci; npm run build; cd ..
Remove-Item -Recurse -Force publish/web -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force publish/web | Out-Null
Copy-Item -Recurse web/.next/standalone/* publish/web/
Copy-Item -Recurse web/.next/static publish/web/.next/static
Copy-Item -Recurse web/public publish/web/public
tar.exe -a -c -f publish/web.zip -C publish/web .
az webapp deploy -g $rg -n $webApp --src-path publish/web.zip --type zip --clean true --track-status false
```

The deploy itself takes under a minute, and the site starts about 20 seconds later. If the CLI reports
a 502 or 504, check `https://<webApp>.scm.azurewebsites.net/api/deployments/latest`; the deployment
often finishes anyway.

### 10. Smoke test

1. Open `https://<webApp>.azurewebsites.net`. You should land on the sign-in page. The site is served
   over HTTPS, which the browser requires for microphone access.
2. Sign in as `admin` with the `Auth--AdminPassword` value. Start a short conversation, allow the microphone, speak a few turns, and end the session.
3. The summary page should show "Scoring your session…", then the CEFR levels (sessions under 150
   learner words are reported as too short to grade).

## Updating

| Change | What to do |
| --- | --- |
| API code | Repeat the publish + `az webapp deploy` in step 8 |
| Web code | Repeat the build, package and deploy block in step 9 |
| New migration script | Re-run step 7 before deploying the API |
| Rotate a key or password | `az keyvault secret set ...` with the new value, then `az webapp restart -n $apiApp -g $rg` |
| Sign everyone out | Rotate `Auth--JwtSigningKey` and restart the API |

## Monitoring and cost

**Application Insights (optional).** Attach Application Insights to the API web app (portal: the
app's **Application Insights** blade), or set the `APPLICATIONINSIGHTS_CONNECTION_STRING` app setting
yourself. When the setting is present, the API exports requests, logs, a span per Claude call
(`claude.chat`, `claude.grade`, with model, session, user and token counts) and these metrics:

| Metric | Tags |
| --- | --- |
| `tutorai.claude.tokens` | `model`, `kind` (input, output, cache_read, cache_write), `route` (chat, grade) |
| `tutorai.turn.first_token_ms` | `model` |
| `tutorai.grading.duration_ms` | `outcome` (done, failed) |
| `tutorai.grading.failures` | `final`, `timed_out` |
| `tutorai.logins.failed` | `reason` (password, locked, unknown_user) |

Without the setting nothing is exported, as when running locally. For example, tokens per model per day:

```kusto
customMetrics
| where name == "tutorai.claude.tokens"
| summarize tokens = sum(valueSum) by model = tostring(customDimensions.model), bin(timestamp, 1d)
```

**Cost per user.** Each tutor turn and assessment stores its model and token counts (`dbo.Turns`,
`dbo.Assessments`), so cost doesn't depend on telemetry retention. The Users page shows each user's
tokens and estimated cost over the last 30 days. The estimate uses the `Pricing` section of
`appsettings.json` (USD per million tokens per model), not the bill. Update the prices when they change
or when you switch models. A model with no price is counted but not costed, shown as "≥ $…".

## Hardening (optional)

- **Lock the API to the web app.** Add an access restriction on the API so only the web app's
  outbound IPs can call it (`az webapp show -n $webApp -g $rg --query outboundIpAddresses`). Sign-in
  limits then can't be bypassed by calling the API directly, and per-visitor limits become possible.
- **Passwordless SQL.** Set a Microsoft Entra admin on the SQL server, create a database user for the
  API's managed identity, and change the `ConnectionStrings--TutorAI` secret to use
  `Authentication=Active Directory Managed Identity` with no password.
- **Private networking.** Put Key Vault and SQL behind private endpoints and enable VNet integration
  on the API (needs a Basic plan or higher for VNet integration; Standard+ recommended).

## Tear down

Deletes everything created above. Key Vault is soft-deleted; purge it if you want to reuse the name.

```powershell
az group delete -n $rg --yes
az keyvault purge -n $kv
```
