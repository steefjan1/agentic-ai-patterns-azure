# Agentic AI Design Patterns on Azure, Part 6: Sequential Chain

Sequential Chain is the pattern for structured business processes: the output of one agent becomes the input to the next, in a fixed pipeline. There's no branching and no re-planning — just a series of well-defined stages, which makes it a natural fit for a low-code orchestration layer rather than hand-written control flow.

## The pattern

`Input → Agent 1 → Agent 2 → ... → Agent N → Output`

Best for pipelines, ETL, content generation, and step-by-step processing.

## Azure architecture

| Component | Azure Service | Role |
|---|---|---|
| Pipeline orchestration | Azure Logic Apps (Standard) | Defines the fixed sequence of stages declaratively; each stage is a workflow action |
| Each chain stage | Azure OpenAI Service, called via the built-in Azure OpenAI connector | Performs one transformation (e.g. extract → summarize → translate → format) |
| File/document intake | Azure Blob Storage | Trigger source and drop location for input/output artifacts |
| Structured hand-off | Azure Service Bus | Optional durable hand-off between stages when a stage runs asynchronously or needs guaranteed delivery |
| Observability | Azure Monitor + Logic Apps run history | Every stage's input/output is visible in the run history out of the box — no custom logging needed |

Logic Apps Standard is the star of this pattern: because the chain is fixed and declarative, you get a visual, versioned workflow definition with built-in retry policies per action, instead of writing an orchestrator function to do the same thing.

## Implementation walkthrough

The sample workflow triggers on a new blob landing in an "intake" container (a raw customer email, in the example). Stage one calls Azure OpenAI to extract structured fields (intent, entities, sentiment) as JSON. Stage two calls Azure OpenAI again to draft a response, using stage one's output as context. Stage three runs a light validation pass, and stage four writes the final artifact to an "output" container and drops a message on Service Bus for a downstream system to pick up. Each stage is a distinct Logic Apps action with its own retry policy (`count`, `interval`, exponential backoff), so a transient failure at stage two doesn't require re-running stage one.

Because each connector action's input and output payload is preserved in the run history, debugging a chain is a matter of opening the failed run in the portal — there's no custom tracing to build.

## Deploying it

`azd up` provisions the Logic Apps Standard app with the workflow definition, the Azure OpenAI resource and connection, two Blob Storage containers, a Service Bus namespace and queue, and Application Insights.

```bash
azd auth login
azd up
```

## When to reach for this pattern

Use Sequential Chain when the stages and their order are known and fixed — content pipelines, document processing, structured ETL over unstructured input. It's the cheapest pattern to operate and reason about because there's no dynamic branching to test; if you find yourself wanting a stage to conditionally skip or repeat based on model output, you've drifted into Planning or ReAct territory.

**Repo:** `repos/06-sequential-chain` — Bicep IaC + Logic Apps Standard workflow calling Azure OpenAI in sequence.
