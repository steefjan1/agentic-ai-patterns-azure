# Orchestrator — Azure PaaS Reference Implementation

`User Request → Orchestrator (Agent) → Agent 1 (Research), Agent 2 (Data), Agent 3 (Analytics) → Aggregate & Respond`

A central agent, hosted on Azure AI Foundry Agent Service, delegates to specialist tools and synthesizes their results. See the companion post: [`posts/05-orchestrator.md`](../../docs/05-orchestrator.md).

## Architecture

| Component | Azure Service |
|---|---|
| Orchestrator agent | Azure AI Foundry Agent Service (Persistent Agents) — owns the thread, decides which specialist tool(s) to call, synthesizes the final answer |
| Specialist: Research | Azure Functions + Azure AI Search (grounds answers in a knowledge base) |
| Specialist: Data | Azure Functions + Azure SQL Database (structured lookups) |
| Specialist: Analytics | Azure Functions (computes summary metrics) |
| Governance | Microsoft Entra ID (managed identity end to end — no static keys) |
| Telemetry | Application Insights |

> **Note on topology:** the post describes each specialist as its own Function App behind API Management for independent scaling and governance. This sample runs all three specialists as functions inside one Function App to keep the reference deployable in a single `azd up`; `infra/main.bicep` is commented where you'd split them out for a production topology.

```
Client ──HTTP──▶ OrchestratorFunction
                       │
                       ▼
          Azure AI Foundry Agent (thread + run)
                       │
        requires_action (tool calls) │
        ┌──────────────┼───────────────┐
        ▼              ▼               ▼
  ResearchTool     DataTool       AnalyticsTool
  (AI Search)      (Azure SQL)    (in-proc calc)
        │              │               │
        └──────── tool outputs submitted back to the run ────────┘
                       │
                       ▼
              Final synthesized answer
```

## Project layout

```
infra/                                Bicep IaC (azd-compatible)
src/OrchestratorFunctions/
  Program.cs
  Functions/
    OrchestratorFunction.cs            HTTP entry point; drives the agent run loop
  Services/
    FoundryAgentService.cs             Creates/reuses the Foundry agent, drives thread + run + tool-call resolution
    ResearchToolService.cs             Azure AI Search-backed research lookup
    DataToolService.cs                 Azure SQL-backed structured data lookup
    AnalyticsToolService.cs            In-process metrics calculation
  Models/
    OrchestratorModels.cs
```

## Prerequisites

- Azure subscription with an Azure AI Foundry project
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- .NET 8 SDK

## Deploy

```bash
azd auth login
azd up
```

Provisions an Azure AI Foundry project + `gpt-4.1` model deployment, Azure AI Search (Basic), Azure SQL Database (Basic, sample schema), a Function App hosting all three specialist tools plus the orchestrator entry point, and Application Insights.

## Run locally

```bash
cp src/OrchestratorFunctions/local.settings.json.example src/OrchestratorFunctions/local.settings.json
cd src/OrchestratorFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/orchestrate \
  -H "Content-Type: application/json" \
  -d '{"message": "How many enterprise accounts churned last quarter, and what does our documentation say we should do about churn risk?"}'
```

PowerShell equivalent:

```powershell
$body = @{ message = "How many enterprise accounts churned last quarter, and what does our documentation say we should do about churn risk?" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/orchestrate" -Body $body -ContentType "application/json"
```

This single request touches both the Data specialist (churn count from Azure SQL) and the Research specialist (churn-risk guidance from Azure AI Search) — the orchestrator agent decides to call both and merges the results itself.

## Test the deployed app

> **Note:** neither the `knowledge-base` search index nor the sample Azure SQL schema/rows are seeded automatically by `azd up`. Without seeding, the Research and Data specialists will report "no results" rather than erroring — the orchestrator still runs, just with nothing to find. See the [ReAct pattern's index-seeding steps](../02-react/README.md#test-the-deployed-app) for the same Azure AI Search seeding approach (swap in this pattern's index name/fields); populating the SQL sample schema is a straightforward `Invoke-Sqlcmd`/`sqlcmd` script against the provisioned server using your own Azure AD login.

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name orchestrate --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = @{ message = "How many enterprise accounts churned last quarter, and what does our documentation say we should do about churn risk?" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/orchestrate?code=$key" -Body $body -ContentType "application/json"
```

This is synchronous — the response comes back directly (no polling). If it fails, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design points

- The orchestrator never implements domain logic itself — it only holds the routing/aggregation prompt and the tool definitions. Each specialist owns its own data access and its own narrower system prompt.
- Tool resolution follows the Foundry Agent Service `requires_action` protocol: the run pauses, `FoundryAgentService` resolves each requested tool call against the matching local service, and submits the results back to the run before polling for completion.
- Because specialists are just services behind function-tool definitions, splitting any of them into their own Function App later is a matter of moving the class and pointing the tool definition at an HTTP action instead of an in-process call — the agent-side contract doesn't change.

**Repo:** Bicep IaC + Azure AI Foundry orchestrator agent + three specialist tool implementations.
