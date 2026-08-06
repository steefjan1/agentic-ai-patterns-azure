---
title: Durable Functions Overview
---
Azure Durable Functions is an extension of Azure Functions for writing stateful workflows in a
serverless environment. It supports orchestrator functions (deterministic, replay-safe control
flow), activity functions (the actual work), and durable entities (stateful objects). Common
application patterns include function chaining, fan-out/fan-in, async HTTP APIs, monitoring, and
human interaction. State is persisted automatically between activity calls, which is what makes
long-running, multi-step agent loops like ReAct reliable across restarts.
