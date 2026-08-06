using System.Text.Json;
using Azure.AI.Projects;

namespace OrchestratorFunctions.Services;

/// <summary>
/// Drives an Azure AI Foundry Agent Service run: creates (or reuses) the orchestrator agent,
/// posts the user message, and resolves any requested tool calls against the local specialist
/// services before letting the run complete.
///
/// NOTE: Azure.AI.Projects is evolving quickly; verify method names against the SDK version
/// pinned in the .csproj if you bump it. The shape of the agent/thread/run lifecycle below
/// matches the Persistent Agents protocol documented for Azure AI Foundry Agent Service.
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
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

        _agentId ??= (await agents.CreateAgentAsync(
            model: deployment,
            name: "orchestrator-agent",
            instructions: OrchestratorInstructions,
            tools: ToolDefinitions())).Value.Id;

        var thread = await agents.CreateThreadAsync();
        await agents.CreateMessageAsync(thread.Value.Id, "user", userMessage);

        var run = await agents.CreateRunAsync(thread.Value.Id, _agentId);
        var toolsInvoked = new List<string>();

        while (run.Value.Status is "queued" or "in_progress" or "requires_action")
        {
            if (run.Value.Status == "requires_action")
            {
                var toolOutputs = new List<ToolOutput>();

                foreach (var toolCall in run.Value.RequiredAction.SubmitToolOutputs.ToolCalls)
                {
                    toolsInvoked.Add(toolCall.Function.Name);
                    var output = await ResolveToolCallAsync(toolCall.Function.Name, toolCall.Function.Arguments);
                    toolOutputs.Add(new ToolOutput(toolCall.Id, output));
                }

                run = await agents.SubmitToolOutputsToRunAsync(thread.Value.Id, run.Value.Id, toolOutputs);
            }
            else
            {
                await Task.Delay(500);
                run = await agents.GetRunAsync(thread.Value.Id, run.Value.Id);
            }
        }

        var messages = await agents.GetMessagesAsync(thread.Value.Id);
        var lastAssistantMessage = messages.Value.Data.First(m => m.Role == "assistant");
        var answer = lastAssistantMessage.Content.OfType<MessageTextContent>().First().Text.Value;

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
