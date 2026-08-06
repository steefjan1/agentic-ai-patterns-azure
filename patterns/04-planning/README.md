# Planning — Azure PaaS Reference Implementation

`Goal → Step 1, Step 2, ... Step N → Execute → Review → Complete`

The agent decomposes a goal into a typed, ordered plan up front, then executes each step with retry and tracked progress. See the companion post: [`posts/04-planning.md`](../../docs/04-planning.md).

## Architecture

| Component | Azure Service |
|---|---|
| Plan generation | Azure OpenAI Service (gpt-4.1), structured JSON output |
| Plan execution | Durable Functions (orchestrator + activity functions), automatic per-step retry |
| Step registry | Azure Functions activity functions, one per step type (`summarize`, `notify`, `call_api`) |
| Progress tracking | Azure Table Storage — one row per step, updated live as execution proceeds |
| Escalation | Azure Logic Apps (Standard) workflow, called when a step exhausts its retries |
| Telemetry | Application Insights |

```
Client ──HTTP──▶ plan_start
                     │
                     ▼
            PlanningOrchestrator (Durable)
                     │
              GeneratePlan (Azure OpenAI) ──▶ [{type, description}, ...]
                     │
        ┌────────────┴─────────────┐
        ▼                          ▼
  ExecuteStep(1)  ──▶ Table Storage row updated ──▶ ExecuteStep(2) ──▶ ...
        │ (on exhausted retries)
        ▼
  Logic Apps: notify-failure workflow
```

## Project layout

```
infra/                            Bicep IaC (azd-compatible)
src/PlanningFunctions/
  Program.cs
  Functions/
    PlanningClientFunction.cs      HTTP trigger that starts the orchestration
    PlanningOrchestrator.cs        Durable orchestrator: generate plan, execute steps in order
    PlanningActivities.cs          GeneratePlan + ExecuteStep + status tracking
  Models/
    PlanningModels.cs
workflow/
  notify-failure.json              Logic Apps Standard workflow definition (failure escalation)
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

Provisions Azure OpenAI, a Durable Functions Function App, Table Storage for step status, a Logic Apps Standard app hosting the failure-notification workflow, and Application Insights.

## Run locally

```bash
cp src/PlanningFunctions/local.settings.json.example src/PlanningFunctions/local.settings.json
cd src/PlanningFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/plan/start \
  -H "Content-Type: application/json" \
  -d '{"goal": "Onboard a new enterprise customer: summarize their contract, notify the account team, and call the provisioning API."}'
```

## Key design points

- The plan is **data, not code**: the model returns a JSON array of typed steps from a fixed vocabulary (`summarize`, `notify`, `call_api`), and the orchestrator dispatches each to the matching activity function. The model can't invent arbitrary actions.
- Each `ExecuteStep` call uses Durable Functions' built-in `RetryOptions` (3 attempts, exponential backoff). If a step still fails after retries, the orchestrator calls the Logic Apps workflow to escalate to a human instead of continuing silently.
- Table Storage rows are updated after every step transition (`pending → executing → complete/failed`), so a client can poll live progress independently of the orchestration status endpoint.

**Repo:** Bicep IaC + C# Durable Functions planner/executor sample + Logic Apps escalation workflow.
