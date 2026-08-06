# 9 Agentic AI Design Patterns on Azure PaaS

Nine agentic AI design patterns — Tool Use, ReAct, Reflection, Planning, Orchestrator,
Sequential Chain, Parallel Fan-out/Fan-in, Hierarchical, and P2P Mesh — each implemented as a
working, independently deployable reference solution on Azure PaaS (Azure OpenAI, Azure AI
Foundry Agent Service, Azure Functions, Durable Functions, Logic Apps, Azure AI Search, Service
Bus, Event Grid, Cosmos DB, Azure SQL).

Start with the [overview post](./docs/00-overview.md) for a comparison table and how the
patterns relate to each other, then read the post for whichever pattern applies and open its
matching folder under `patterns/`.

## Patterns

| # | Pattern | Doc | Sample |
|---|---|---|---|
| 1 | Tool Use | [docs/01-tool-use.md](./docs/01-tool-use.md) | [patterns/01-tool-use](./patterns/01-tool-use) |
| 2 | ReAct | [docs/02-react.md](./docs/02-react.md) | [patterns/02-react](./patterns/02-react) |
| 3 | Reflection | [docs/03-reflection.md](./docs/03-reflection.md) | [patterns/03-reflection](./patterns/03-reflection) |
| 4 | Planning | [docs/04-planning.md](./docs/04-planning.md) | [patterns/04-planning](./patterns/04-planning) |
| 5 | Orchestrator | [docs/05-orchestrator.md](./docs/05-orchestrator.md) | [patterns/05-orchestrator](./patterns/05-orchestrator) |
| 6 | Sequential Chain | [docs/06-sequential-chain.md](./docs/06-sequential-chain.md) | [patterns/06-sequential-chain](./patterns/06-sequential-chain) |
| 7 | Parallel Fan-out/Fan-in | [docs/07-parallel-fanout-fanin.md](./docs/07-parallel-fanout-fanin.md) | [patterns/07-parallel-fanout-fanin](./patterns/07-parallel-fanout-fanin) |
| 8 | Hierarchical | [docs/08-hierarchical.md](./docs/08-hierarchical.md) | [patterns/08-hierarchical](./patterns/08-hierarchical) |
| 9 | P2P Mesh | [docs/09-p2p-mesh.md](./docs/09-p2p-mesh.md) | [patterns/09-p2p-mesh](./patterns/09-p2p-mesh) |

## Repo layout

```
agentic-ai-patterns-azure/
├── docs/                       Overview post + one post per pattern
│   ├── 00-overview.md
│   └── 01-tool-use.md ... 09-p2p-mesh.md
├── infra/
│   └── modules/                Shared Bicep, used by every pattern
│       ├── observability.bicep   Log Analytics + Application Insights
│       └── openai.bicep          Azure OpenAI account + model deployment(s)
└── patterns/
    ├── 01-tool-use/             Each pattern is self-contained and independently deployable
    │   ├── README.md
    │   ├── azure.yaml
    │   ├── infra/main.bicep     References ../../../infra/modules/*
    │   └── src/
    ├── 02-react/
    ├── ...
    └── 09-p2p-mesh/
```

## Why one repo instead of nine

Each pattern still deploys independently — nothing here requires standing up all nine at
once. What a single repo buys you is a consistent place to compare them and one less place for
the shared plumbing to drift: `infra/modules/observability.bicep` and
`infra/modules/openai.bicep` are used by all nine patterns instead of being copy-pasted (and
silently diverging) nine times over. Everything specific to a pattern — Azure AI Search,
Service Bus, Cosmos DB, Azure SQL, Event Grid, Logic Apps — stays in that pattern's own
`infra/main.bicep`, so reading one pattern's infra file still shows you everything relevant to
it without chasing definitions across the repo.

## Prerequisites

- An Azure subscription with access to Azure OpenAI
- [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- .NET 8 SDK
- Azure CLI (`az`), logged in (`az login`), for the patterns that use setup scripts (search
  indexing, Event Grid subscriptions)

## Deploying a single pattern

```bash
az login
azd auth login

cd patterns/03-reflection   # or whichever pattern you want
azd up
```

`azd up` provisions that pattern's resources (via its `infra/main.bicep`, which pulls in the
shared modules from `infra/modules/`) and deploys its code. Each pattern's own README has the
exact resources it provisions and a sample request to try once it's deployed. Patterns with an
extra one-time setup step (seeding a search index, wiring Event Grid subscriptions) call that
out in their `scripts/` folder and README.

Tearing a pattern down:

```bash
cd patterns/03-reflection
azd down --purge
```

## Notes on scope

These are reference implementations sized for learning and prototyping, not hardened production
templates — SKUs are picked for cost (Basic/Standard tiers throughout), and a few pieces are
deliberately simplified from what the companion blog posts describe as the "full" production
topology (noted in each pattern's README where it applies, e.g. Orchestrator's three specialists
run in one Function App here rather than three). Each README says explicitly where a sample
diverges from the post for the sake of a single deployable unit.
