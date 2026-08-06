using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Azure.Functions.Worker;
using OpenAI.Chat;
using ReactFunctions.Models;
using ReactFunctions.Services;

namespace ReactFunctions.Functions;

public class ReActActivities
{
    private readonly ChatClient _chatClient;
    private readonly AzureAiSearchService _search;

    private const string SystemPrompt = """
        You are a research agent that solves problems step by step using the ReAct pattern.
        At each step, respond with STRICT JSON matching this shape:
        {
          "thought": "your reasoning about what to do next",
          "action": "search" | "answer",
          "actionInput": "the search query, or the final answer text if action is 'answer'"
        }
        Use "search" to look up grounding information before answering. Use "answer" only once
        you have enough information. Do not fabricate facts you have not observed.
        """;

    public ReActActivities(AzureOpenAIClient client, AzureAiSearchService search)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        _chatClient = client.GetChatClient(deployment);
        _search = search;
    }

    [Function(nameof(ThinkAndDecide))]
    public async Task<ReActDecision> ThinkAndDecide([ActivityTrigger] string transcriptJson)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"Transcript so far (JSON):\n{transcriptJson}\n\nWhat is your next step?"),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
        var raw = completion.Content[0].Text;

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var action = root.GetProperty("action").GetString() ?? "answer";
        var actionInput = root.GetProperty("actionInput").GetString() ?? "";
        var thought = root.GetProperty("thought").GetString() ?? "";

        return action == "answer"
            ? new ReActDecision(thought, action, actionInput, IsFinalAnswer: true, FinalAnswer: actionInput)
            : new ReActDecision(thought, action, actionInput, IsFinalAnswer: false, FinalAnswer: null);
    }

    [Function(nameof(SearchObservation))]
    public async Task<string> SearchObservation([ActivityTrigger] string query)
    {
        return await _search.SearchAsync(query);
    }
}
