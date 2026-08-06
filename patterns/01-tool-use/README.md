# Tool Use — Azure PaaS Reference Implementation

`User Request → AI Agent → Tool (API/DB) → Result`

The simplest agentic pattern: one Azure OpenAI call decides which tool to invoke, an Azure Function runs the tool, the result is returned. See the companion post: [`posts/01-tool-use.md`](../../docs/01-tool-use.md).

## Architecture

| Component | Azure Service |
|---|---|
| Reasoning + function-calling | Azure OpenAI Service (GPT-4o deployment) |
| Agent entry point + tool execution | Azure Functions (.NET 8 Isolated Worker, HTTP triggers) |
| Secrets | Azure Key Vault |
| Telemetry | Application Insights |
| Auth to Azure OpenAI | Managed Identity (no API keys in code or config) |

```
Client ──HTTP──▶ AgentFunction ──▶ Azure OpenAI (function calling)
                       │                    │
                       │◀── tool_calls ─────┘
                       ▼
              GetOrderStatusFunction (in-proc call)
                       │
                       ▼
              Azure OpenAI (final answer) ──▶ Client
```

## Project layout

```
infra/               Bicep IaC (azd-compatible)
src/ToolUseFunctions/
  Program.cs          Isolated worker host bootstrap + DI
  Functions/
    AgentFunction.cs          HTTP-triggered entry point; runs the tool-use loop
  Services/
    AgentService.cs           Azure OpenAI function-calling loop
    OrderLookupService.cs     Example tool: order status lookup
  Models/
    AgentModels.cs            Request/response DTOs
azure.yaml            azd project definition
```

## Prerequisites

- Azure subscription with access to Azure OpenAI
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- .NET 8 SDK

## Deploy

```bash
azd auth login
azd up
```

This provisions Azure OpenAI (with a `gpt-4.1` deployment), the Function App, storage, Key Vault, and Application Insights, then builds and deploys `src/ToolUseFunctions`.

## Run locally

```bash
cp src/ToolUseFunctions/local.settings.json.example src/ToolUseFunctions/local.settings.json
# fill in AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_DEPLOYMENT
cd src/ToolUseFunctions
func start
```

```bash
curl -X POST http://localhost:7071/api/agent \
  -H "Content-Type: application/json" \
  -d '{"message": "What is the status of order 1042?"}'
```

PowerShell equivalent:

```powershell
$body = @{ message = "What is the status of order 1042?" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/agent" -Body $body -ContentType "application/json"
```

## Test the deployed app

The Function App requires a function key (`AuthorizationLevel.Function`), so calls to the live endpoint need one extra step beyond the local `curl`/PowerShell examples above.

```powershell
$rg = azd env get-value AZURE_RESOURCE_GROUP
$funcApp = azd env get-value FUNCTION_APP_NAME
$key = az functionapp function keys list -g $rg -n $funcApp --function-name agent --query "default" -o tsv
if (-not $key) { $key = az functionapp keys list -g $rg -n $funcApp --query "functionKeys.default" -o tsv }

$body = @{ message = "What is the status of order 1042?" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "https://$funcApp.azurewebsites.net/api/agent?code=$key" -Body $body -ContentType "application/json"
```

This is synchronous — the response comes back directly with the final answer (no polling required). If something goes wrong, check Application Insights:

```powershell
az extension add -n application-insights --only-show-errors
$aiName = az monitor app-insights component show -g $rg --query "[0].name" -o tsv
az monitor app-insights query -g $rg -a $aiName --analytics-query "exceptions | order by timestamp desc | take 5 | project timestamp, outerMessage, innermostMessage" -o table
```

## Key design point

The model never touches data directly — it only ever requests a named tool with JSON arguments. `AgentService` resolves that request to an in-process call to `OrderLookupService`, executes it, and feeds the JSON result back to the model for the final natural-language response. That boundary is what makes this pattern safe to expose to real backends.
