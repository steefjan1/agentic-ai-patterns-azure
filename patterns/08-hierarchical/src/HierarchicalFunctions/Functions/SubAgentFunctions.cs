using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using HierarchicalFunctions.Models;
using HierarchicalFunctions.Services;

namespace HierarchicalFunctions.Functions;

/// <summary>
/// Each domain sub-agent is an independent Service Bus-triggered function: it never talks to
/// the manager or the other sub-agents directly, it only reacts to messages on its own
/// filtered subscription and replies on the shared session-scoped reply queue.
/// </summary>
public class SubAgentFunctions
{
    private readonly FinanceAgentService _finance;
    private readonly OpsAgentService _ops;
    private readonly ITAgentService _it;
    private readonly ILogger<SubAgentFunctions> _logger;

    public SubAgentFunctions(FinanceAgentService finance, OpsAgentService ops, ITAgentService it, ILogger<SubAgentFunctions> logger)
    {
        _finance = finance;
        _ops = ops;
        _it = it;
        _logger = logger;
    }

    [Function("finance_sub_agent")]
    public async Task RunFinance(
        [ServiceBusTrigger("domain-tasks", "finance-sub", Connection = "SERVICEBUS_FULLYQUALIFIEDNAMESPACE")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await HandleAsync(message, messageActions, "Finance", _finance.AnswerAsync);
    }

    [Function("ops_sub_agent")]
    public async Task RunOps(
        [ServiceBusTrigger("domain-tasks", "ops-sub", Connection = "SERVICEBUS_FULLYQUALIFIEDNAMESPACE")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await HandleAsync(message, messageActions, "Ops", _ops.AnswerAsync);
    }

    [Function("it_sub_agent")]
    public async Task RunIT(
        [ServiceBusTrigger("domain-tasks", "it-sub", Connection = "SERVICEBUS_FULLYQUALIFIEDNAMESPACE")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await HandleAsync(message, messageActions, "IT", _it.AnswerAsync);
    }

    private async Task HandleAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        string domain,
        Func<string, Task<string>> answerFunc)
    {
        var task = JsonSerializer.Deserialize<DomainTask>(message.Body.ToString())!;
        _logger.LogInformation("[{Domain}] handling run {RunId}: {Question}", domain, task.RunId, task.Question);

        var answer = await answerFunc(task.Question);
        var reply = new DomainReply(task.RunId, domain, answer);

        // Reply on the session-scoped queue the manager is listening on for this run.
        var replyMessage = new ServiceBusMessage(JsonSerializer.Serialize(reply))
        {
            SessionId = task.RunId,
        };

        // Sender is created per-invocation here for simplicity; in a high-throughput sub-agent
        // you would inject and reuse a single ServiceBusSender per domain instead.
        await using var client = new ServiceBusClient(
            Environment.GetEnvironmentVariable("SERVICEBUS_FULLYQUALIFIEDNAMESPACE"),
            new Azure.Identity.DefaultAzureCredential());
        await using var sender = client.CreateSender("domain-replies");
        await sender.SendMessageAsync(replyMessage);

        await messageActions.CompleteMessageAsync(message);
    }
}
