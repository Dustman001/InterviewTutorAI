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

The API reads these settings. In Azure the three secrets live in **Azure Key Vault**; nothing secret
goes in `appsettings.json` or App Service app settings.

| Setting | Secret? | Key Vault secret name | Where it's set in Azure |
| --- | --- | --- | --- |
| `Anthropic:ApiKey` | yes | `Anthropic--ApiKey` | Key Vault |
| `AzureSpeech:Key` | yes | `AzureSpeech--Key` | Key Vault |
| `ConnectionStrings:TutorAI` | yes | `ConnectionStrings--TutorAI` | Key Vault |
| `KeyVault:Uri` | no | – | App setting `KeyVault__Uri` |
| `AzureSpeech:Region` | no | – | App setting `AzureSpeech__Region` |
| `Anthropic:ConversationModel` / `GradingModel` | no | – | `appsettings.json` (override with app settings if needed) |

How it works: when `KeyVault:Uri` is set, `Program.cs` adds Key Vault as a configuration source and
signs in with `DefaultAzureCredential` (the App Service managed identity in Azure, your `az login`
locally). Key Vault secret names can't contain `:`, so `--` stands in for it
(`Anthropic--ApiKey` → `Anthropic:ApiKey`). When `KeyVault:Uri` is empty, Key Vault is skipped and
local user-secrets are used.

Secrets are read once at startup. After rotating a secret, restart the API web app.

## Run locally

```powershell
docker compose up -d                                   # SQL Server on localhost:14333
dotnet run --project src/TutorAI.Migrations            # create the database and apply scripts

cd src/TutorAI.Api
dotnet user-secrets set "Anthropic:ApiKey"   "<anthropic-key>"
dotnet user-secrets set "AzureSpeech:Key"    "<speech-key>"
dotnet user-secrets set "AzureSpeech:Region" "<speech-region, e.g. eastus>"
dotnet run                                             # http://localhost:5080
cd ../..

cd web
npm install
npm run dev                                            # http://localhost:3000
```

To run locally against the Azure Key Vault instead of user-secrets: `az login`, give your account the
**Key Vault Secrets User** role on the vault, and set `dotnet user-secrets set "KeyVault:Uri" "https://<vault>.vault.azure.net/"`.

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

Create the database explicitly. The migrator creates the database if it's missing, but on Azure SQL a
bare `CREATE DATABASE` gets a default General Purpose tier that costs far more than this app needs.

```powershell
az sql server create -n $sqlServer -g $rg -l $loc --admin-user $sqlAdmin --admin-password $sqlPassword
az sql db create -g $rg -s $sqlServer -n TutorAI --service-objective Basic

# Let Azure services (the API web app) reach the server
az sql server firewall-rule create -g $rg -s $sqlServer -n AllowAzureServices `
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

$conn = "Server=tcp:$sqlServer.database.windows.net,1433;Database=TutorAI;User ID=$sqlAdmin;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
az keyvault secret set --vault-name $kv -n "ConnectionStrings--TutorAI" --value $conn | Out-Null
```

### 6. Anthropic API key

```powershell
az keyvault secret set --vault-name $kv -n "Anthropic--ApiKey" --value "<anthropic-key>" | Out-Null
```

Check the vault now holds all three secrets:

```powershell
az keyvault secret list --vault-name $kv --query "[].name" -o tsv
# AzureSpeech--Key, ConnectionStrings--TutorAI, Anthropic--ApiKey
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
az resource update -g $rg --resource-type "Microsoft.Web/sites/config" --parent "sites/$apiApp" `
  -n web --set properties.healthCheckPath=/api/health
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

The Next.js app forwards `/api/*` to the API. `TUTORAI_API_URL` is read by `next.config.ts` when
the app is **built**, so it must be set before deploying, and the web app must be redeployed if it
changes. App Service builds the app on deploy (`SCM_DO_BUILD_DURING_DEPLOYMENT`).

```powershell
az webapp create -n $webApp -g $rg -p $plan --runtime "NODE:24-lts"
az webapp config appsettings set -n $webApp -g $rg --settings `
  "TUTORAI_API_URL=https://$apiApp.azurewebsites.net" `
  "SCM_DO_BUILD_DURING_DEPLOYMENT=true"
az webapp config set -n $webApp -g $rg --startup-file "npm run start" --always-on true --https-only true

tar.exe -a -c -f publish/web.zip --exclude node_modules --exclude .next -C web .
az webapp deploy -g $rg -n $webApp --src-path publish/web.zip --type zip
```

The first deploy takes several minutes while `npm install` and `next build` run on the server.

### 10. Smoke test

1. Open `https://<webApp>.azurewebsites.net`. The site is served over HTTPS, which the browser
   requires for microphone access.
2. Start a short conversation, allow the microphone, speak a few turns, and end the session.
3. The summary page should show CEFR levels (sessions under 150 learner words are reported as too
   short to grade).

## Updating

| Change | What to do |
| --- | --- |
| API code | Repeat the publish + `az webapp deploy` in step 8 |
| Web code | Repeat the zip + `az webapp deploy` in step 9 |
| New migration script | Re-run step 7 before deploying the API |
| Rotate a key | `az keyvault secret set ...` with the new value, then `az webapp restart -n $apiApp -g $rg` |

## Hardening (optional)

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
