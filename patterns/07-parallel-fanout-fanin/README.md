# Parallel Fan-out/Fan-in — Azure PaaS Reference Implementation

`Input → Agent A, Agent B, ... Agent N (parallel) → Aggregator (Σ) → Output`

Splits work into independent branches, runs them concurrently, and joins the results — using Durable Functions' native fan-out/fan-in support (`Task.WhenAll`). See the companion post: [`posts/07-parallel-fanout-fanin.md`](../../docs/07-parallel-fanout-fanin.md).

## Architecture

| Component | Azure Service |
|---|---|
| Fan-out orchestration | Durable Functions (`Task.WhenAll` over N activity calls) |
| Each parallel branch | Azure Functions activity function + Azure OpenAI (summarizes one chunk) |
| Aggregation (fan-in) | Durable Functions orchestrator, final Azure OpenAI synthesis call |
| Scale-out compute | Azure Functions Premium plan |
| Audit | Azure Blob Storage (each branch's raw output + the final aggregate) |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ fanout_start
                     │
                     ▼
         FanOutOrchestrator (Durable)
                     │
        split input into N chunks
                     │
     ┌────────┬──────┴──────┬────────┐
     ▼        ▼             ▼        ▼
 Summarize  Summarize   Summarize  Summarize     ← Task.WhenAll (concurrent)
 Chunk 1    Chunk 2     Chunk 3    Chunk N
     └────────┴──────┬──────┴────────┘
                      ▼
              AggregateSummaries (Σ)
                      │
                      ▼
                Final output
```

## Project layout

```
infra/                             Bicep IaC (azd-compatible)
src/FanOutFunctions/
  Program.cs
  Functions/
    FanOutClientFunction.cs         HTTP trigger that starts the orchestration
    FanOutOrchestrator.cs           Durable orchestrator: split, Task.WhenAll, aggregate
    FanOutActivities.cs             SummarizeChunk + AggregateSummaries activity functions
  Models/
    FanOutModels.cs
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

Provisions Azure OpenAI (`gpt-4.1`), a Durable Functions Function App on an Elastic Premium plan (for reliable concurrent scale-out), storage for the Durable Task Hub and branch audit trail, and Application Insights.

## Run locally

```bash
cp src/FanOutFunctions/local.settings.json.example src/FanOutFunctions/local.settings.json
cd src/FanOutFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/fanout/start \
  -H "Content-Type: application/json" \
  -d @data/sample-long-document.json
```

PowerShell equivalent:

```powershell
$body = Get-Content .\data\sample-long-document.json -Raw
$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/fanout/start" -Body $body -ContentType "application/json"
$start

# Poll until the orchestration finishes
Invoke-RestMethod -Uri $start.statusQueryGetUri
```

## Test the deployed app

Durable Functions — the initial call only registers the run, so poll `statusQueryGetUri` for the result.

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name fanout_start --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = Get-Content .\data\sample-long-document.json -Raw
$start = Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/fanout/start?code=$key" -Body $body -ContentType "application/json"

Invoke-RestMethod -Uri $start.statusQueryGetUri
```

Re-run the last line every few seconds until `runtimeStatus` is `Completed`. If it fails instead, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design points

- `context.CallActivityAsync` is invoked once per chunk **without** awaiting each call individually; the resulting `Task` objects are collected into a list and passed to `await Task.WhenAll(tasks)`. Durable Functions schedules all of them concurrently across available Function instances.
- Each branch has its own `RetryOptions`, so one chunk failing and retrying doesn't block or restart the other N-1 branches.
- The fan-in step is itself a model call: rather than naively concatenating N partial summaries, `AggregateSummaries` asks Azure OpenAI to synthesize them into one coherent result.

**Repo:** Bicep IaC + C# Durable Functions fan-out/fan-in sample.
