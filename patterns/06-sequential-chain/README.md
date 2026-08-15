# Sequential Chain — Azure PaaS Reference Implementation

`Input → Extract fields → Draft response → Validate draft → Output`

A fixed, linear pipeline: each stage's output is the next stage's only input, with no shared context and no branching. See the companion post: [`posts/06-sequential-chain.md`](../../docs/06-sequential-chain.md).

> **Implementation note:** this sample originally targeted Logic Apps Standard for the chain itself. It's now built as a Durable Functions orchestrator instead — same fixed three-stage pipeline, but on the same Azure Functions + Durable Task Framework foundation used by every other multi-step pattern in this repo, which turned out to be far more reliable to provision and operate than Logic Apps Standard's `ServiceProvider`-connector model.

## Architecture

| Component | Azure Service |
|---|---|
| Chain execution | Durable Functions (orchestrator + activity functions), one activity per stage |
| Stage 1 – Extract fields | Azure OpenAI Service (GPT-4.1) — pulls intent/entities/sentiment out of the raw input as JSON |
| Stage 2 – Draft response | Azure OpenAI Service (GPT-4.1) — drafts a response from the extracted fields only |
| Stage 3 – Validate draft | Azure OpenAI Service (GPT-4.1) — checks tone/consistency, returns the final approved text |
| Output persistence | Azure Blob Storage (`output` container), one blob per run |
| Completion notification | Azure Service Bus queue (`chain-output`) |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ chain_start
                     │
                     ▼
            ChainOrchestrator (Durable)
                     │
         ExtractFields (Azure OpenAI) ──▶ extracted JSON
                     │
         DraftResponse (Azure OpenAI) ──▶ draft text
                     │
         ValidateDraft (Azure OpenAI) ──▶ final text
                     │
        ┌────────────┴─────────────┐
        ▼                          ▼
  WriteOutputBlob            SendToServiceBus
  (output container)         (chain-output queue)
```

## Project layout

```
infra/                                   Bicep IaC (azd-compatible)
src/SequentialChainFunctions/
  Program.cs
  Functions/
    ChainOrchestrator.cs                 HTTP trigger (chain_start) + Durable orchestrator
    ChainActivities.cs                   ExtractFields, DraftResponse, ValidateDraft, WriteOutputBlob, SendToServiceBus
  Models/
    ChainModels.cs
```

## Prerequisites

- Azure subscription with Azure OpenAI access
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- .NET 8 SDK

## Deploy

```bash
azd auth login
azd up
```

Provisions Azure OpenAI, a Durable Functions Function App, Blob Storage (`output` container), a Service Bus namespace with a `chain-output` queue, and Application Insights.

## Run locally

```bash
cp src/SequentialChainFunctions/local.settings.json.example src/SequentialChainFunctions/local.settings.json
cd src/SequentialChainFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/chain/start \
  -H "Content-Type: application/json" \
  -d '{"text": "Hi, I was charged twice for my subscription this month and I would like a refund for the duplicate charge. This is the second time this has happened. My account email is jordan@example.com."}'
```

PowerShell equivalent:

```powershell
$body = @{ text = "Hi, I was charged twice for my subscription this month and I would like a refund for the duplicate charge. This is the second time this has happened. My account email is jordan@example.com." } | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/chain/start" -Body $body -ContentType "application/json"
$start

# Poll until the orchestration finishes
Invoke-RestMethod -Uri $start.statusQueryGetUri
```

## Test the deployed app

Durable Functions — the initial call only registers the run, so poll `statusQueryGetUri` for the result.

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name chain_start --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = @{ text = "Hi, I was charged twice for my subscription this month and I would like a refund for the duplicate charge. This is the second time this has happened. My account email is jordan@example.com." } | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/chain/start?code=$key" -Body $body -ContentType "application/json"

Invoke-RestMethod -Uri $start.statusQueryGetUri
```

Re-run the last line every few seconds until `runtimeStatus` is `Completed` — the `output` property will contain `runId`, `extractedFields`, `draft`, and `finalText`. If it fails instead, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design points

- Each stage only ever sees the immediately preceding stage's output, never the original input or any earlier stage's result — that's what makes this a *chain* rather than a shared-context loop or a plan the model composes itself. The sequence and prompts are fixed at deploy time, not decided by the model.
- `CallActivityAsync` is used with Durable Functions' built-in `RetryOptions` (3 attempts, exponential backoff) on the three OpenAI stages, so a transient model/API failure doesn't fail the whole run.
- The final output is written to two places for two different consumers: a blob per run (`output/{runId}.txt`) for audit/retrieval, and a Service Bus message on `chain-output` for anything downstream that wants to react to completions in near-real-time without polling.

**Repo:** Bicep IaC + C# Durable Functions three-stage chain sample.
