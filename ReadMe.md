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
5. The API rejects any request without a valid token (401), except `/api/health` and
   `/api/auth/login`. Each token carries the user's `TokenVersion`, checked against `dbo.Users` on
   every request. That's how disabling a user, resetting a password or changing a role takes effect
   immediately instead of when the token expires.

## How sessions are evaluated

Ending a session (`POST /api/sessions/{id}/end`) grades it on the CEFR scale: A1, A2, A2+, B1, B1+, B2,
B2+, C1, C2. The grade combines measurements taken from the audio with a Claude examiner reading the
transcript. Code: `src/TutorAI.Api/Assessment`.

1. **Evidence collected during the session.** Azure AI Speech transcribes each learner turn and runs
   pronunciation assessment on it: accuracy and prosody scores, plus per-word timings and error types.
   Only the learner's turns are measured and graded.
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
`?regrade=true` to grade it again.

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
# Or, free (one per subscription; pauses when the monthly free amount is used up):
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
Invoke-RestMethod "https://$apiApp.azurewebsites.net/api/health"
# database: True   speechConfigured: True
```

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
3. The summary page should show CEFR levels (sessions under 150 learner words are reported as too
   short to grade).

## Updating

| Change | What to do |
| --- | --- |
| API code | Repeat the publish + `az webapp deploy` in step 8 |
| Web code | Repeat the build, package and deploy block in step 9 |
| New migration script | Re-run step 7 before deploying the API |
| Rotate a key or password | `az keyvault secret set ...` with the new value, then `az webapp restart -n $apiApp -g $rg` |
| Sign everyone out | Rotate `Auth--JwtSigningKey` and restart the API |

## Hardening (optional)

- **Limit sign-in attempts.** There is no lockout or rate limit on `/api/auth/login` yet.
- **Lock the API to the web app.** Add an access restriction on the API so only the web app's
  outbound IPs can call it (`az webapp show -n $webApp -g $rg --query outboundIpAddresses`).
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
