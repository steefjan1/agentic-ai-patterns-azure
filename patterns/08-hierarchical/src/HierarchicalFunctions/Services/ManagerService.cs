using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Messaging.ServiceBus;
using OpenAI.Chat;
using HierarchicalFunctions.Models;

namespace HierarchicalFunctions.Services;

/// <summary>
/// The manager agent: decomposes a request across domains, dispatches sub-tasks over
/// Service Bus, waits on a session-scoped reply queue, and reconciles the answers.
/// </summary>
public class ManagerService
{
    private readonly ChatClient _chatClient;
    private readonly ServiceBusClient _serviceBusClient;
    private static readonly string[] KnownDomains = { "Finance", "Ops", "IT" };

    public ManagerService(AzureOpenAIClient client, ServiceBusClient serviceBusClient)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        _chatClient = client.GetChatClient(deployment);
        _serviceBusClient = serviceBusClient;
    }

    public async Task<ManagerResponse> HandleAsync(string userMessage)
    {
        var runId = Guid.NewGuid().ToString("N");

        var relevantDomains = await DecomposeAsync(userMessage);

        var sender = _serviceBusClient.CreateSender("domain-tasks");
        foreach (var domain in relevantDomains)
        {
            var task = new DomainTask(runId, domain, userMessage);
            var message = new ServiceBusMessage(JsonSerializer.Serialize(task))
            {
                SessionId = runId, // sub-agents echo this back on their reply
                ApplicationProperties = { ["Domain"] = domain },
            };
            await sender.SendMessageAsync(message);
        }

        var replies = await CollectRepliesAsync(runId, relevantDomains.Count, TimeSpan.FromSeconds(30));
        var timedOut = replies.Count < relevantDomains.Count;

        var finalAnswer = await ReconcileAsync(userMessage, replies);

        return new ManagerResponse(runId, finalAnswer, replies, timedOut);
    }

    private async Task<List<string>> DecomposeAsync(string userMessage)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"""
                You route requests to domain experts: {string.Join(", ", KnownDomains)}.
                Decide which domains are relevant to the user's request (at least one).
                Respond as STRICT JSON: {{ "domains": ["Finance", "Ops"] }}.
                """),
            new UserChatMessage(userMessage),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
        using var doc = JsonDocument.Parse(completion.Content[0].Text);
        var domains = doc.RootElement.GetProperty("domains")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Where(d => d is not null && KnownDomains.Contains(d))
            .Select(d => d!)
            .ToList();

        return domains.Count > 0 ? domains : KnownDomains.ToList();
    }

    private async Task<List<DomainReply>> CollectRepliesAsync(string runId, int expectedCount, TimeSpan timeout)
    {
        var replies = new List<DomainReply>();
        var deadline = DateTime.UtcNow.Add(timeout);

        await using var receiver = await _serviceBusClient.AcceptSessionAsync("domain-replies", runId);

        while (replies.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var message = await receiver.ReceiveMessageAsync(remaining);
            if (message is null) break;

            var reply = JsonSerializer.Deserialize<DomainReply>(message.Body);
            if (reply is not null) replies.Add(reply);

            await receiver.CompleteMessageAsync(message);
        }

        return replies;
    }

    private async Task<string> ReconcileAsync(string originalQuestion, List<DomainReply> replies)
    {
        var repliesText = replies.Count == 0
            ? "(no domain replies were received before the timeout)"
            : string.Join("\n", replies.Select(r => $"[{r.Domain}] {r.Answer}"));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You are the manager reconciling domain expert replies into one answer for the user. " +
                "If domains disagree or one is missing, say so explicitly rather than picking a side silently."),
            new UserChatMessage($"Original question: {originalQuestion}\n\nDomain replies:\n{repliesText}"),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }
}
