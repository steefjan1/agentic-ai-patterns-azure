# P2P Mesh — Azure PaaS Reference Implementation

`Agent A ↔ Agent B ↔ Agent C (all interconnected) → Final Output`

Agents react to events from each other with no central coordinator — connective tissue provided entirely by Azure Event Grid pub-sub. See the companion post: [`posts/09-p2p-mesh.md`](../../docs/09-p2p-mesh.md).

## Architecture

| Component | Azure Service |
|---|---|
| Event backbone | Azure Event Grid (custom topic) — every agent publishes and subscribes; no agent calls another directly |
| Research agent | Azure Functions (Event Grid-triggered) + Azure OpenAI |
| Fact-check agent | Azure Functions (Event Grid-triggered) + Azure AI Search |
| Synthesis agent | Azure Functions (Event Grid-triggered) + Azure OpenAI |
| Shared coordination state | Azure Cosmos DB — tracks which events have landed for a given correlation ID |
| Telemetry | Application Insights (correlation ID propagated through every event for distributed tracing) |

> **Note on completion detection:** the post describes a lightweight Durable entity "watcher." This sample instead exposes a simple HTTP status endpoint that reads the same Cosmos DB correlation record — functionally equivalent for a reference sample, with one less moving part to stand up.

```
StartFunction ──publish "request.created"──▶ Event Grid topic
                                                    │
                          ┌─────────────────────────┴─────────────────────────┐
                          ▼                                                   │
                 ResearchAgent (subscribes: request.created)                  │
                          │ publish "research.completed"                      │
                          ▼                                                   │
        ┌─────────────────┴──────────────────┐                               │
        ▼                                     ▼                               │
 FactCheckAgent                        SynthesisAgent  ◀──────────────────────┘
 (subscribes: research.completed)      (subscribes: research.completed,
        │ publish "factcheck.completed"        factcheck.completed;
        ▼                                       proceeds once both seen for a
 SynthesisAgent (2nd trigger)                    correlation ID, via Cosmos DB)
        │ publish "mesh.completed"
        ▼
   Client polls StatusFunction
```

## Project layout

```
infra/                                Bicep IaC
scripts/
  create-event-subscriptions.sh        Wires the 3 Event Grid subscriptions to the deployed function endpoints
src/MeshFunctions/
  Program.cs
  Functions/
    StartFunction.cs                   HTTP trigger: publishes "request.created", returns a correlation ID
    ResearchAgentFunction.cs           EventGridTrigger: request.created -> research.completed
    FactCheckAgentFunction.cs          EventGridTrigger: research.completed -> factcheck.completed
    SynthesisAgentFunction.cs          EventGridTrigger: research.completed + factcheck.completed -> mesh.completed
    StatusFunction.cs                  HTTP trigger: polls the correlation record for the client
  Services/
    EventPublisherService.cs           Wraps EventGridPublisherClient
    CorrelationStateService.cs         Cosmos DB-backed per-correlation event tracking
    ResearchService.cs / FactCheckService.cs / SynthesisService.cs
  Models/
    MeshModels.cs
```

## Prerequisites

- Azure subscription with Azure OpenAI access
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) or Azure CLI
- .NET 8 SDK

## Deploy

```bash
azd auth login
azd up
./scripts/create-event-subscriptions.sh   # wires Event Grid -> function endpoints (needs function keys, fetched post-deploy)
```

Provisions an Event Grid custom topic, Azure OpenAI, Azure AI Search, Cosmos DB (correlation state), one Function App hosting all three agents plus the start/status endpoints, and Application Insights.

## Run locally

Local Event Grid delivery requires a public endpoint (e.g. via `ngrok` or Azure Dev Tunnels), since Event Grid pushes events over HTTPS. For local testing without that, invoke `ResearchAgentFunction` directly with a sample Event Grid payload from `data/sample-event.json`.

```bash
curl -X POST http://localhost:7071/api/mesh/start \
  -H "Content-Type: application/json" \
  -d '{"topic": "What is Kubernetes and how does it relate to Azure Container Apps?"}'
```

```bash
curl http://localhost:7071/api/mesh/status/<correlationId>
```

## Key design points

- No agent function calls another agent directly or knows the other agents exist — each only knows the event types it subscribes to. Agents can be added, removed, or independently redeployed without touching the others.
- `SynthesisAgentFunction` is the one place mesh-wide state matters: it subscribes to **two** event types and uses the Cosmos DB correlation record to know when it's seen both `research.completed` and `factcheck.completed` for a given run before producing the final output. Whichever event arrives second is the one that triggers synthesis.
- Every published event carries the same `correlationId` in its event data, which is what makes distributed tracing across a mesh with no central orchestrator possible at all — without it, there is no way to reconstruct which events belonged to which run.

**Repo:** Bicep IaC + C# Event Grid-triggered Azure Functions mesh sample.
