# Reflection — Azure PaaS Reference Implementation

`Initial Answer → Reflect (self-review) → Revise Answer → Final Answer`

The agent drafts an answer, critiques its own draft against a rubric, and revises before responding — with the full trail kept for audit. See the companion post: [`posts/03-reflection.md`](../../docs/03-reflection.md).

## Architecture

| Component | Azure Service |
|---|---|
| Drafting | Azure OpenAI Service — primary deployment (`gpt-4.1`) |
| Critique | Azure OpenAI Service — lighter deployment (`gpt-4.1-mini`) |
| Revision | Azure OpenAI Service — primary deployment |
| Orchestration | Durable Functions (draft → reflect → revise, optional retry loop) |
| Audit trail | Azure Blob Storage (every draft/critique/revision triple, per run ID) |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ reflect_start
                     │
                     ▼
           ReflectionOrchestrator (Durable)
                     │
     ┌───────────────┼────────────────┐
     ▼                ▼                ▼
 DraftAnswer      ReflectOnDraft   ReviseAnswer   (loop up to 2x if critique fails)
 (gpt-4.1)         (gpt-4.1-mini)    (gpt-4.1)
     │                │                │
     └──────── every stage persisted to Blob Storage ────────┘
```

## Project layout

```
infra/                              Bicep IaC (azd-compatible)
src/ReflectionFunctions/
  Program.cs
  Functions/
    ReflectionClientFunction.cs      HTTP trigger that starts the orchestration
    ReflectionOrchestrator.cs        Durable orchestrator: draft/reflect/revise loop
    ReflectionActivities.cs          Activity functions + Blob audit writes
  Models/
    ReflectionModels.cs
```

## Prerequisites

- Azure subscription with Azure OpenAI access (needs quota for two deployments)
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- .NET 8 SDK

## Deploy

```bash
azd auth login
azd up
```

Provisions Azure OpenAI with `gpt-4.1` and `gpt-4.1-mini` deployments, a Durable Functions Function App, a storage account (Durable Task Hub + `reflection-audit` container), and Application Insights.

## Run locally

```bash
cp src/ReflectionFunctions/local.settings.json.example src/ReflectionFunctions/local.settings.json
cd src/ReflectionFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/reflect/start \
  -H "Content-Type: application/json" \
  -d '{"prompt": "Write a function that validates an IBAN number.", "rubric": "Code must be correct, handle edge cases, and include a docstring."}'
```

PowerShell equivalent:

```powershell
$body = @{
    prompt = "Write a function that validates an IBAN number."
    rubric = "Code must be correct, handle edge cases, and include a docstring."
} | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/reflect/start" -Body $body -ContentType "application/json"
$start

# Poll until the orchestration finishes
Invoke-RestMethod -Uri $start.statusQueryGetUri
```

## Test the deployed app

Durable Functions — the initial call only registers the run, so poll `statusQueryGetUri` for the result.

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name reflect_start --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = @{
    prompt = "Write a function that validates an IBAN number."
    rubric = "Code must be correct, handle edge cases, and include a docstring."
} | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/reflect/start?code=$key" -Body $body -ContentType "application/json"

Invoke-RestMethod -Uri $start.statusQueryGetUri
```

Re-run the last line every few seconds until `runtimeStatus` is `Completed`. If it fails instead, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design points

- Critique runs on a cheaper model deployment (`gpt-4.1-mini`) — critique is a narrower task than generation and doesn't need the largest model, which meaningfully cuts cost for a pattern that doubles model calls.
- The orchestrator loops back to `ReflectOnDraft` up to `MaxRevisions` (default 2) times if the critique still fails the rubric, then returns the best available draft flagged `ReachedRevisionLimit: true` rather than looping forever.
- Every stage's input/output is written to Blob Storage under `reflection-audit/{runId}/`, which is what makes this pattern usable for compliance-sensitive content — the full self-review trail is inspectable, not just the final answer.

**Repo:** Bicep IaC + C# Durable Functions draft/critique/revise sample.
