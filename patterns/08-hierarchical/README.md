# Hierarchical — Azure PaaS Reference Implementation

`User Request → Manager Agent (Top Agent) → Sub Agent A (Finance), Sub Agent B (Ops), Sub Agent C (IT) → Consolidate & Respond`

A manager agent decomposes a request across domain experts, dispatches asynchronously, and reconciles their (possibly disagreeing) answers. See the companion post: [`posts/08-hierarchical.md`](../../docs/08-hierarchical.md).

## Architecture

| Component | Azure Service |
|---|---|
| Manager agent | Azure OpenAI Service (gpt-4.1) — decomposes the request, dispatches sub-tasks, reconciles replies |
| Inter-agent messaging | Azure Service Bus — a topic (`domain-tasks`) with one filtered subscription per domain, plus a session-enabled reply queue (`domain-replies`) |
| Sub-agent: Finance | Azure Functions (Service Bus-triggered) + Azure Cosmos DB |
| Sub-agent: Ops | Azure Functions (Service Bus-triggered) + Azure SQL Database |
| Sub-agent: IT | Azure Functions (Service Bus-triggered) + Azure AI Search |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ ManagerFunction
                       │
             gpt-4.1 decomposes request
                       │
        publish to topic "domain-tasks" (filtered by Domain)
        ┌──────────────┼───────────────┐
        ▼              ▼               ▼
  Finance sub      Ops sub         IT sub
  (Cosmos DB)       (SQL)         (AI Search)
        │              │               │
        └──── replies land on session-scoped "domain-replies" queue ────┘
                       │
                       ▼
        Manager reconciles (gpt-4.1) → final answer
```

## Project layout

```
infra/                                     Bicep IaC
src/HierarchicalFunctions/
  Program.cs
  Functions/
    ManagerFunction.cs                      HTTP entry point: decompose, dispatch, wait, reconcile
    FinanceSubAgentFunction.cs              ServiceBusTrigger on "finance-sub"
    OpsSubAgentFunction.cs                  ServiceBusTrigger on "ops-sub"
    ITSubAgentFunction.cs                   ServiceBusTrigger on "it-sub"
  Services/
    ManagerService.cs                       Decomposition + reconciliation prompts, dispatch/collect logic
    FinanceAgentService.cs                  Cosmos DB-backed domain logic
    OpsAgentService.cs                      Azure SQL-backed domain logic
    ITAgentService.cs                       Azure AI Search-backed domain logic
  Models/
    HierarchicalModels.cs
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

Provisions Azure OpenAI, a Service Bus namespace (topic + 3 filtered subscriptions + a session-enabled reply queue), Cosmos DB, Azure SQL Database, Azure AI Search, one Function App hosting the manager and all three sub-agents, and Application Insights.

## Run locally

```bash
cp src/HierarchicalFunctions/local.settings.json.example src/HierarchicalFunctions/local.settings.json
cd src/HierarchicalFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/manager/start \
  -H "Content-Type: application/json" \
  -d '{"message": "Can we afford to give the support team new laptops this quarter, and is IT ready to provision them?"}'
```

This touches all three domains: Finance (budget), Ops (headcount/quarter context), and IT (provisioning capacity) — the manager decides which are relevant, dispatches to each, and reconciles.

## Key design points

- Sub-agents communicate with the manager **asynchronously via Service Bus**, not direct HTTP calls — each can scale, fail, and redeploy independently of the others and of the manager.
- Replies are correlated using a **session-enabled queue**, with the session ID set to the run ID: the manager opens a `ServiceBusSessionReceiver` for that session and collects replies until it has one from every domain it dispatched to, or a timeout elapses.
- Reconciliation is an explicit step, not a merge: the manager's final prompt is given all sub-agent replies and instructed to surface disagreement between domains rather than silently picking one.

**Repo:** Bicep IaC + C# Azure Functions manager/sub-agent sample over Service Bus.
