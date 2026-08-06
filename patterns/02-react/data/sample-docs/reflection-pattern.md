---
title: Reflection Pattern
---
The Reflection pattern has an AI agent review its own output before returning it to the user.
On Azure, this is typically implemented with Durable Functions: one activity function drafts
an answer with Azure OpenAI, a second activity function critiques it against a rubric, and a
third revises it if the critique fails. It's best suited to quality-critical outputs such as
generated code or compliance-adjacent text.
