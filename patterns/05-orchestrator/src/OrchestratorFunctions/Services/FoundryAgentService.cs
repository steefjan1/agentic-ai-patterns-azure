using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Agents.Persistent;

namespace OrchestratorFunctions.Services;

/// <summary>
/// Drives an Azure AI Foundry Agent Service run: creates (or reuses) the orchestrator agent,
/// posts the user message, and resolves any requested tool calls against the local specialist
/// services before letting the run complete.
///
/// NOTE: Azure.AI.Projects / Azure.AI.Agents.Persistent are evolving quickly; verify method
/// names against the SDK version pinned in the .csproj if you bump it. This targets
/// Azure.AI.Projects 2.0.1 + Azure.AI.Agents.Persistent 1.2.0-beta.9 -- the "Classic Agents"
/// (PersistentAgentsClient) surface reached via AIProjectClient.GetPersistentAgentsClient().
/// </summary>
public class FoundryAgentService
{
    private readonly AIProjectClient _projectClient;
    private readonly ResearchToolService _research;
    private readonly DataToolService _data;
    private readonly AnalyticsToolService _analytics;
    private string? _agentId;

    private const string OrchestratorInstructions = """
        You are an orchestrator agent for an enterprise assistant. You have three tools:
        research_docs (search internal documentation), query_churn (structured account data),
        and compute_metric (pre-aggregated business metrics). Call whichever tools are relevant
        to the user's question -- you may call more than one -- then synthesize a single answer
        that clearly reflects what each tool returned. Do not guess at facts a tool could answer.
        """;

    public FoundryAgentService(AIProjectClient projectClient, ResearchToolService research, DataToolService data, AnalyticsToolService analytics)
    {
        _projectClient = projectClient;
        _research = research;
        _data = data;
        _analytics = analytics;
    }

    public async Task<(string Answer, List<string> ToolsInvoked)> RunAsync(string userMessage)
    {
        var agents = _projectClient.GetPersistentAgentsClient();
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";

        if (_agentId is null)
        {
            var createdAgent = (await agents.Administration.CreateAgentAsync(
                model: deployment,
                name: "orchestrator-agent",
                instructions: OrchestratorInstructions,
                tools: ToolDefinitions())).Value;
            _agentId = createdAgent.Id;
        }

        var thread = (await agents.Threads.CreateThreadAsync()).Value;
        await agents.Messages.CreateMessageAsync(thread.Id, MessageRole.User, userMessage);

        var run = (await agents.Runs.CreateRunAsync(thread.Id, _agentId)).Value;
        var toolsInvoked = new List<string>();

        while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress || run.Status == RunStatus.RequiresAction)
        {
            if (run.Status == RunStatus.RequiresAction && run.RequiredAction is SubmitToolOutputsAction submitToolOutputsAction)
            {
                var toolOutputs = new List<ToolOutput>();

                foreach (var toolCall in submitToolOutputsAction.ToolCalls)
                {
                    if (toolCall is RequiredFunctionToolCall functionToolCall)
                    {
                        toolsInvoked.Add(functionToolCall.Name);
                        var output = await ResolveToolCallAsync(functionToolCall.Name, functionToolCall.Arguments);
                        toolOutputs.Add(new ToolOutput(toolCall, output));
                    }
                }

                run = (await agents.Runs.SubmitToolOutputsToRunAsync(run, toolOutputs, toolApprovals: null)).Value;
            }
            else
            {
                await Task.Delay(500);
                run = (await agents.Runs.GetRunAsync(thread.Id, run.Id)).Value;
            }
        }

        var messages = agents.Messages.GetMessagesAsync(threadId: thread.Id, order: ListSortOrder.Descending);
        PersistentThreadMessage? lastAgentMessage = null;
        await foreach (var candidate in messages)
        {
            if (candidate.Role == MessageRole.Agent)
            {
                lastAgentMessage = candidate;
                break;
            }
        }

        var answer = lastAgentMessage?.ContentItems.OfType<MessageTextContent>().FirstOrDefault()?.Text
            ?? "(the agent did not return a text answer)";

        return (answer, toolsInvoked);
    }

    private async Task<string> ResolveToolCallAsync(string toolName, string argumentsJson)
    {
        using var args = JsonDocument.Parse(argumentsJson);

        return toolName switch
        {
            "research_docs" => await _research.ResearchAsync(args.RootElement.GetProperty("query").GetString() ?? ""),
            "query_churn" => await _data.QueryChurnAsync(args.RootElement.GetProperty("period").GetString() ?? "last_quarter"),
            "compute_metric" => await _analytics.ComputeMetricAsync(args.RootElement.GetProperty("metricName").GetString() ?? ""),
            _ => $"Unknown tool: {toolName}",
        };
    }

    private static List<ToolDefinition> ToolDefinitions() => new()
    {
        new FunctionToolDefinition(
            name: "research_docs",
            description: "Search internal documentation for guidance, policies, or explanations.",
            parameters: BinaryData.FromString("""
            { "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }
            """)),
        new FunctionToolDefinition(
            name: "query_churn",
            description: "Look up the number of enterprise accounts that churned in a given period.",
            parameters: BinaryData.FromString("""
            { "type": "object", "properties": { "period": { "type": "string", "description": "e.g. 'last_quarter'" } }, "required": ["period"] }
            """)),
        new FunctionToolDefinition(
            name: "compute_metric",
            description: "Return a pre-aggregated business metric such as churn_rate or nps.",
            parameters: BinaryData.FromString("""
            { "type": "object", "properties": { "metricName": { "type": "string" } }, "required": ["metricName"] }
            """)),
    };
}
