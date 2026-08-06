# Sequential Chain — Azure PaaS Reference Implementation

`Input → Agent 1 → Agent 2 → ... → Agent N → Output`

A fixed pipeline of stages, each transforming the output of the last — implemented declaratively as an Azure Logic Apps Standard workflow rather than hand-written orchestration code. See the companion post: [`posts/06-sequential-chain.md`](../../docs/06-sequential-chain.md).

## Architecture

| Component | Azure Service |
|---|---|
| Pipeline orchestration | Azure Logic Apps (Standard), declarative workflow with per-action retry policies |
| Each chain stage | Azure OpenAI Service, called via HTTP action (extract → draft → validate) |
| Intake / output | Azure Blob Storage (`intake` and `output` containers) |
| Downstream hand-off | Azure Service Bus (queue) |
| Observability | Logic Apps run history (built in) + Application Insights |

```
Blob landed in "intake/" ──trigger──▶ Extract fields (Azure OpenAI)
                                            │
                                            ▼
                                   Draft response (Azure OpenAI)
                                            │
                                            ▼
                                    Validate draft (Azure OpenAI)
                                            │
                                            ▼
                          Write to "output/" + enqueue on Service Bus
```

## Project layout

```
infra/                                    Bicep IaC
workflow/
  sequential-chain/
    workflow.json                          The 4-stage Logic Apps Standard workflow definition
  host.json
  connections.json                         Placeholder — populated by Bicep-created API connections
scripts/
  deploy.sh                                Zip-deploys the workflow app (fallback to azd)
```

## Prerequisites

- Azure subscription with Azure OpenAI access
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) or Azure CLI
- [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) (Logic Apps Standard reuses the Functions runtime for local dev)

## Deploy

```bash
az login
az group create -n rg-sequential-chain -l eastus
az deployment group create -g rg-sequential-chain -f infra/main.bicep -p environmentName=sequentialchain

# Zip-deploy the workflow content
./scripts/deploy.sh rg-sequential-chain
```

This provisions Azure OpenAI (`gpt-4.1` deployment), a Logic Apps Standard app, a storage account with `intake`/`output` containers, a Service Bus namespace and queue, and Application Insights.

## Test the deployed app

Upload a sample file to the `intake` container. Within about a minute, the workflow trigger fires, runs all three Azure OpenAI stages, writes the result to `output/`, and drops a message on the Service Bus queue.

```powershell
$storageAccount = az deployment group show -g rg-sequential-chain -n main --query "properties.outputs.STORAGE_ACCOUNT_NAME.value" -o tsv

az storage blob upload `
  --account-name $storageAccount `
  --container-name intake `
  --name sample-request.txt `
  --file ./data/sample-request.txt `
  --auth-mode login
```

**Check the result landed in `output/`:**

```powershell
az storage blob list --account-name $storageAccount --container-name output --auth-mode login -o table
az storage blob download --account-name $storageAccount --container-name output --name <blob-name-from-above> --file ./result.txt --auth-mode login
Get-Content .\result.txt
```

**Check the Service Bus hand-off message landed on the queue** (`chain-output`, fixed name):

```powershell
$sbNamespaceFqdn = az deployment group show -g rg-sequential-chain -n main --query "properties.outputs.SERVICEBUS_NAMESPACE.value" -o tsv
$sbNamespace = $sbNamespaceFqdn -replace '\.servicebus\.windows\.net$', ''
az servicebus queue show --resource-group rg-sequential-chain --namespace-name $sbNamespace --name chain-output --query "countDetails.activeMessageCount"
```

**If nothing shows up after a minute or two**, the workflow run history in the portal (Logic App resource → Workflow → Run history) is the fastest way to see exactly which stage failed and why — every action's input/output is preserved there, no separate logging setup needed.

## Key design points

- Each stage is a distinct workflow action with its own retry policy (3 attempts, exponential backoff) — a transient failure at stage two doesn't require re-running stage one.
- Every action's input and output payload is preserved in the Logic Apps run history, visible in the portal, so debugging a chain is a matter of opening the failed run — no custom tracing to build.
- The workflow is purely declarative (`workflow.json`); there's no application code to deploy or version beyond the workflow definition itself.

**Repo:** Bicep IaC + Logic Apps Standard workflow calling Azure OpenAI in sequence.
