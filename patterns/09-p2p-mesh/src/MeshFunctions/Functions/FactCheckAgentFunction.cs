using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MeshFunctions.Models;
using MeshFunctions.Services;

namespace MeshFunctions.Functions;

/// <summary>Reacts only to "research.completed". Doesn't know ResearchAgent or SynthesisAgent exist.</summary>
public class FactCheckAgentFunction
{
    private readonly FactCheckService _factCheck;
    private readonly CorrelationStateService _state;
    private readonly EventPublisherService _publisher;
    private readonly ILogger<FactCheckAgentFunction> _logger;

    public FactCheckAgentFunction(FactCheckService factCheck, CorrelationStateService state, EventPublisherService publisher, ILogger<FactCheckAgentFunction> logger)
    {
        _factCheck = factCheck;
        _state = state;
        _publisher = publisher;
        _logger = logger;
    }

    [Function("factcheck_agent")]
    public async Task Run([EventGridTrigger] EventGridEvent evt)
    {
        if (evt.EventType != "research.completed") return;

        var data = evt.Data!.ToObjectFromJson<MeshEventData>();
        _logger.LogInformation("[FactCheckAgent] handling {CorrelationId}", data.CorrelationId);

        var verification = await _factCheck.VerifyAsync(data.Research ?? "");
        await _state.RecordFactCheckAsync(data.CorrelationId, verification);

        await _publisher.PublishAsync(
            "factcheck.completed",
            $"mesh/{data.CorrelationId}",
            data with { FactCheck = verification });
    }
}
