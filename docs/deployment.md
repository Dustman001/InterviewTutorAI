# Deploying TutorAI to Azure

The app's design, configuration and local setup are in the [README](../ReadMe.md).

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
# /api/health deliberately doesn't (see step 8). Resuming takes up to a minute: the API keeps retrying the connection
# meanwhile (SqlDatabase.OpenRetry), and the sign-in page tells the user why it's slow.
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
(`claude.chat`, `claude.grade`, with model, session, user and token counts), a span per prep agent run
(`prep.run`, with one child span per model call and per tool call) and these metrics:

| Metric | Tags |
| --- | --- |
| `tutorai.claude.tokens` | `model`, `kind` (input, output, cache_read, cache_write), `route` (chat, grade, prep) |
| `tutorai.turn.first_token_ms` | `model` |
| `tutorai.grading.duration_ms` | `outcome` (done, failed) |
| `tutorai.grading.failures` | `final`, `timed_out` |
| `tutorai.logins.failed` | `reason` (password, locked, unknown_user) |
| `tutorai.requests.rate_limited` | `policy` (login, turns, prep-runs, mcp) |

Without the setting nothing is exported, as when running locally. For example, tokens per model per day:

```kusto
customMetrics
| where name == "tutorai.claude.tokens"
| summarize tokens = sum(valueSum) by model = tostring(customDimensions.model), bin(timestamp, 1d)
```

**Error logs.** Every response with status 400 or above gets one log line (category
`TutorAI.Api.Logging.RequestOutcomeLogging`) with the route template, status, user id and trace id:
401/403/413/429 at Information, 5xx returned without an exception at Warning, other 4xx at Debug (not
exported). An unhandled exception is logged once at Error and answered with a problem+json 500 whose
`traceId` is the `operation_Id` to search for. Sign-in failures on known accounts, lockouts and
disabled-account sign-ins are logged with the user id only. Log entries written while a signed-in request
runs carry `UserId` (and `SessionId` inside a session) as custom dimensions. The unhandled-exception entry
is written after those scopes close, so find its user through the request's `operation_Id` (the outcome
lines and request telemetry share it). Every entry also carries ASP.NET Core's request scope, including
the raw `RequestPath`, as request telemetry does.

```kusto
traces
| where customDimensions.CategoryName == "TutorAI.Api.Logging.RequestOutcomeLogging"
| summarize count() by route = tostring(customDimensions.Route), status = tostring(customDimensions.StatusCode)
```

**Cost per user.** Each tutor turn, assessment and prep run stores its model and token counts (`dbo.Turns`,
`dbo.Assessments`, `dbo.PrepRuns`), so cost doesn't depend on telemetry retention. The Users page shows each user's
tokens and estimated cost over the last 30 days. The estimate uses the `Pricing` section of
`appsettings.json` (USD per million tokens per model), not the bill. Update the prices when they change
or when you switch models. A model with no price is counted but not costed, shown as "≥ $…".

## Hardening (optional)

- **Lock the API to the web app.** Add an access restriction on the API so only the web app's
  outbound IPs can call it (`az webapp show -n $webApp -g $rg --query outboundIpAddresses`). Sign-in
  limits then can't be bypassed by calling the API directly, and per-visitor limits become possible.
  MCP clients call the API directly at `/mcp`, so this also cuts them off unless you allow their
  addresses too.
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
