# ReAct — Azure PaaS Reference Implementation

`Think → Act → Observe → repeat until goal achieved`

The agent reasons about the next action, takes it, observes the result, and loops — until it decides it has enough to answer. See the companion post: [`posts/02-react.md`](../../docs/02-react.md).

## Architecture

| Component | Azure Service |
|---|---|
| Reasoning (think + decide next action) | Azure OpenAI Service (gpt-4.1) |
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

Provisions Azure OpenAI (`gpt-4.1` deployment), Azure AI Search (Basic tier), a Durable Functions-enabled Function App, storage for the Durable Task Hub, and Application Insights. A post-provision hook indexes the sample documents in `data/sample-docs/` into the search index.

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

```powershell
$body = @{ goal = "What Azure service should I use for the Reflection pattern and why?" } | ConvertTo-Json
$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/react/start" -Body $body -ContentType "application/json"
$start

# Poll until the orchestration finishes
Invoke-RestMethod -Uri $start.statusQueryGetUri
```

The response includes a `statusQueryGetUri` you can poll for the final answer plus the full thought/action/observation transcript.

## Key design points

- The orchestrator function is **deterministic and replay-safe** by construction: all the actual work (LLM calls, search calls) happens in activity functions, never inline in the orchestrator.
- A hard cap of 6 iterations (`MaxIterations` in `ReActOrchestrator.cs`) and a Durable Functions timeout prevent runaway loops — a real operational risk this pattern doesn't show in the diagram.
- Every thought/action/observation is appended to a transcript that's replayed into the next `ThinkAndDecide` call, so the model can see its own reasoning history.

**Repo:** Bicep IaC + C# Durable Functions sample with Azure AI Search grounding.
