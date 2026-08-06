# Agentic AI Design Patterns on Azure, Part 5: Orchestrator

The Orchestrator pattern introduces a central coordinator that delegates work to specialist agents and aggregates their results. It's the pattern enterprise AI platforms converge on once a single agent can no longer reasonably cover research, data lookups, and analytics in one prompt — you split by domain and let a coordinator route.

## The pattern

`User Request → Orchestrator (Agent) → Agent 1 (Research), Agent 2 (Data), ... Agent N (Analytics) → Aggregate & Respond`

Best for enterprise platforms, domain specialization, and governance.

## Azure architecture

| Component | Azure Service | Role |
|---|---|---|
| Orchestrator agent | Azure AI Foundry Agent Service | Hosts the coordinating agent, holds the routing/aggregation logic and conversation thread |
| Specialist agents | Azure Functions (one per specialist: Research, Data, Analytics) | Each is a narrowly scoped Azure OpenAI-backed function with its own system prompt and tool access |
| Grounding for Research agent | Azure AI Search | Vector/hybrid search over the knowledge base the Research specialist draws from |
| Grounding for Data agent | Azure SQL Database / Azure Cosmos DB | Structured data the Data specialist queries |
| Governance | Azure API Management + Microsoft Entra ID | Fronts each specialist endpoint, enforces auth and rate limits, gives you a single place to audit who called what |
| Observability | Application Insights + Azure Monitor | End-to-end tracing across the orchestrator and every specialist call |

Azure AI Foundry Agent Service is the natural home for the orchestrator itself: it gives you managed threads, built-in tool-calling to registered "agent tools" (which we point at the specialist Function endpoints), and a way to swap the underlying model without touching the delegation logic.

## Implementation walkthrough

The orchestrator agent is defined in Azure AI Foundry with three tools registered, each mapped to an HTTP action calling one of the specialist Function Apps. When a user request comes in, the Foundry agent decides which specialist(s) are relevant — it may call one, several, or all three — collects their responses, and synthesizes a final answer. Each specialist Function is itself a minimal Tool Use-style agent: it receives a scoped sub-question, calls Azure OpenAI with a narrow system prompt and its own tools (AI Search for Research, SQL for Data, a metrics API for Analytics), and returns a structured result.

API Management sits in front of the specialist Functions so the orchestrator (and nothing else) can reach them, with Entra ID managed identity used for the orchestrator-to-APIM calls instead of static keys.

## Deploying it

`azd up` provisions the Azure AI Foundry project and orchestrator agent, three Function Apps for the specialists, Azure AI Search, Azure SQL Database (Basic tier, sample schema), APIM (Consumption tier), and Application Insights.

```bash
azd auth login
azd up
```

## When to reach for this pattern

Reach for Orchestrator when you have genuinely distinct domains of expertise that benefit from separate prompts, separate tools, and separate governance — and you want one entry point for the user. It's more infrastructure than a single agent needs, so don't reach for it until Tool Use or ReAct inside a single agent has actually become unwieldy.

**Repo:** `repos/05-orchestrator` — Bicep IaC + Azure AI Foundry orchestrator agent + three specialist Azure Functions.
