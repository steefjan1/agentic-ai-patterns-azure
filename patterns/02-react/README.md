# ReAct — Azure PaaS Reference Implementation

`Think → Act → Observe → repeat until goal achieved`

The agent reasons about the next action, takes it, observes the result, and loops — until it decides it has enough to answer. See the companion post: [`posts/02-react.md`](../../docs/02-react.md).

## Architecture

| Component | Azure Service |
|---|---|
| Reasoning (think + decide next action) | Azure OpenAI Service (GPT-4o) |
| Loop host, durable state | Durable Functions (.NET 8 Isolated Worker) |
| Grounding ("observe") | Azure AI Search (hybrid search over a sample knowledge base) |
| Tool execution | Azure Functions activity functions |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ StartReAct (client function)
                     │
                     ▼
            ReActOrchestrator (Durable, replay-safe)
                     │
        ┌────────────┴────────────┐
        ▼                         ▼
  ThinkAndDecide             SearchObservation
  (Azure OpenAI)             (Azure AI Search)
        │                         │
        └──────── loop (max 6 iterations) ────────┘
                     │
                     ▼
              Final answer returned
```

## Project layout

```
infra/                         Bicep IaC (azd-compatible)
src/ReactFunctions/
  Program.cs
  Functions/
    ReActClientFunction.cs      HTTP trigger that starts the orchestration
    ReActOrchestrator.cs        Durable orchestrator: the think/act/observe loop
    ReActActivities.cs          Activity functions: ThinkAndDecide, SearchObservation
  Services/
    AzureAiSearchService.cs     Wraps Azure.Search.Documents hybrid search
  Models/
    ReActModels.cs
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

Provisions Azure OpenAI (`gpt-4.1` deployment), Azure AI Search (Basic tier), a Durable Functions-enabled Function App, storage for the Durable Task Hub, and Application Insights.

The search index is **not** seeded automatically — run `scripts/seed-index.sh` (or the PowerShell equivalent below) once after `azd up` before testing, otherwise every "observe" step will come back empty.

## Run locally

```bash
cp src/ReactFunctions/local.settings.json.example src/ReactFunctions/local.settings.json
# fill in AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT, AZURE_SEARCH_ENDPOINT, AZURE_SEARCH_INDEX
cd src/ReactFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/react/start \
  -H "Content-Type: application/json" \
  -d '{"goal": "What Azure service should I use for the Reflection pattern and why?"}'
```

PowerShell equivalent:

```powershell
$body = @{ goal = "What Azure service should I use for the Reflection pattern and why?" } | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/react/start" -Body $body -ContentType "application/json"
$start

# Poll until the orchestration finishes
Invoke-RestMethod -Uri $start.statusQueryGetUri
```

The response includes a `statusQueryGetUri` you can poll for the final answer plus the full thought/action/observation transcript.

## Test the deployed app

**1. Seed the search index** (one-time, after each fresh deploy) — the search resource requires Azure AD auth to be explicitly enabled for RBAC roles to work for data-plane calls, so seed with the admin key rather than a bearer token:

```powershell
cd patterns/02-react
$rg = azd env get-value AZURE_RESOURCE_GROUP
$searchServiceName = az search service list -g $rg --query "[0].name" -o tsv
$adminKey = az search admin-key show -g $rg --service-name $searchServiceName --query primaryKey -o tsv
$searchEndpoint = azd env get-value AZURE_SEARCH_ENDPOINT
$apiVersion = "2024-07-01"
$headers = @{ "api-key" = $adminKey; "Content-Type" = "application/json" }

$indexBody = @{
    name = "knowledge-base"
    fields = @(
        @{ name = "id"; type = "Edm.String"; key = $true; searchable = $false }
        @{ name = "title"; type = "Edm.String"; searchable = $true; filterable = $true }
        @{ name = "content"; type = "Edm.String"; searchable = $true }
    )
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Put -Uri "$searchEndpoint/indexes/knowledge-base?api-version=$apiVersion" -Headers $headers -Body $indexBody

$docs = @(); $i = 0
Get-ChildItem .\data\sample-docs\*.md | ForEach-Object {
    $i++
    $lines = Get-Content $_.FullName
    $title = (($lines | Where-Object { $_ -match '^title:' } | Select-Object -First 1)) -replace '^title:\s*', ''
    $content = ($lines | Select-Object -Skip 3) -join ' '
    $docs += @{ "@search.action" = "mergeOrUpload"; id = "doc$i"; title = $title; content = $content }
}
$uploadBody = @{ value = $docs } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri "$searchEndpoint/indexes/knowledge-base/docs/index?api-version=$apiVersion" -Headers $headers -Body $uploadBody
```

**2. Call the deployed endpoint and poll for the result** (Durable Functions — the initial call only registers the run):

```powershell
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name react_start --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = @{ goal = "What Azure service should I use for the Reflection pattern and why?" } | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/react/start?code=$key" -Body $body -ContentType "application/json"

Invoke-RestMethod -Uri $start.statusQueryGetUri
```

Re-run the last line every few seconds until `runtimeStatus` is `Completed`; `output.FinalAnswer` and `output.Transcript` will have the result. If it fails instead, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design points

- The orchestrator function is **deterministic and replay-safe** by construction: all the actual work (LLM calls, search calls) happens in activity functions, never inline in the orchestrator.
- A hard cap of 6 iterations (`MaxIterations` in `ReActOrchestrator.cs`) and a Durable Functions timeout prevent runaway loops — a real operational risk this pattern doesn't show in the diagram.
- Every thought/action/observation is appended to a transcript that's replayed into the next `ThinkAndDecide` call, so the model can see its own reasoning history.

**Repo:** Bicep IaC + C# Durable Functions sample with Azure AI Search grounding.
