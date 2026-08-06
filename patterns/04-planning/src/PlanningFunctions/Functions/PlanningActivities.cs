using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using OpenAI.Chat;
using PlanningFunctions.Models;

namespace PlanningFunctions.Functions;

public record ExecuteStepInput(string RunId, int StepIndex, PlanStep Step);
public record EscalateInput(string RunId, int StepIndex, string Description, string Error);

public class PlanningActivities
{
    private readonly ChatClient _chatClient;
    private readonly TableServiceClient _tableService;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly string[] AllowedStepTypes = { "summarize", "notify", "call_api" };

    public PlanningActivities(AzureOpenAIClient client, TableServiceClient tableService, IHttpClientFactory httpClientFactory)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = client.GetChatClient(deployment);
        _tableService = tableService;
        _httpClientFactory = httpClientFactory;
    }

    [Function(nameof(GeneratePlan))]
    public async Task<List<PlanStep>> GeneratePlan([ActivityTrigger] string goal)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($$"""
                Decompose the user's goal into an ordered list of steps. Each step must have a
                "type" from this fixed set: {{string.Join(", ", AllowedStepTypes)}}, and a short
                "description". Respond as STRICT JSON: { "steps": [{ "type": "...", "description": "..." }] }.
                """),
            new UserChatMessage(goal),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
        using var doc = JsonDocument.Parse(completion.Content[0].Text);
        var steps = new List<PlanStep>();
        foreach (var element in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            steps.Add(new PlanStep(element.GetProperty("type").GetString() ?? "summarize", element.GetProperty("description").GetString() ?? ""));
        }
        return steps;
    }

    [Function(nameof(ExecuteStep))]
    public async Task<StepExecutionResult> ExecuteStep([ActivityTrigger] ExecuteStepInput input)
    {
        await UpdateStatusAsync(input.RunId, input.StepIndex, input.Step, "executing", null);

        // Dispatch by step type. In a real system each of these would call a distinct
        // downstream system; here they're simulated via a second Azure OpenAI call.
        var output = input.Step.Type switch
        {
            "summarize" => await RunModelStepAsync("Summarize the following in two sentences:", input.Step.Description),
            "notify" => await RunModelStepAsync("Draft a short notification message for:", input.Step.Description),
            "call_api" => await RunModelStepAsync("Describe, in one sentence, the API call that would be made for:", input.Step.Description),
            _ => throw new InvalidOperationException($"Unknown step type '{input.Step.Type}'"),
        };

        await UpdateStatusAsync(input.RunId, input.StepIndex, input.Step, "complete", output);
        return new StepExecutionResult(input.StepIndex, input.Step.Type, input.Step.Description, "complete", output);
    }

    [Function(nameof(EscalateFailure))]
    public async Task EscalateFailure([ActivityTrigger] EscalateInput input)
    {
        await UpdateStatusAsync(input.RunId, input.StepIndex, new PlanStep("unknown", input.Description), "failed", input.Error);

        var url = Environment.GetEnvironmentVariable("ESCALATION_WORKFLOW_URL");
        if (string.IsNullOrWhiteSpace(url)) return;

        var client = _httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new { input.RunId, input.StepIndex, input.Description, input.Error });
        await client.PostAsync(url, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
    }

    private async Task<string> RunModelStepAsync(string instruction, string description)
    {
        var messages = new List<ChatMessage> { new UserChatMessage($"{instruction} {description}") };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }

    private async Task UpdateStatusAsync(string runId, int stepIndex, PlanStep step, string status, string? output)
    {
        var tableName = Environment.GetEnvironmentVariable("PLAN_STATUS_TABLE") ?? "planstatus";
        var table = _tableService.GetTableClient(tableName);
        await table.CreateIfNotExistsAsync();

        var entity = new TableEntity(runId, stepIndex.ToString("D4"))
        {
            ["Type"] = step.Type,
            ["Description"] = step.Description,
            ["Status"] = status,
            ["Output"] = output ?? "",
            ["UpdatedAt"] = DateTimeOffset.UtcNow,
        };

        await table.UpsertEntityAsync(entity);
    }
}
