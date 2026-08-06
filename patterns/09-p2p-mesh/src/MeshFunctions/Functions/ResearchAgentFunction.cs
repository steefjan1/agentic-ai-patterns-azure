using System.Text.Json;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MeshFunctions.Models;
using MeshFunctions.Services;

namespace MeshFunctions.Functions;

/// <summary>Reacts only to "request.created". Doesn't know FactCheckAgent or SynthesisAgent exist.</summary>
public class ResearchAgentFunction
{
    private readonly ResearchService _research;
    private readonly CorrelationStateService _state;
    private readonly EventPublisherService _publisher;
    private readonly ILogger<ResearchAgentFunction> _logger;

    public ResearchAgentFunction(ResearchService research, CorrelationStateService state, EventPublisherService publisher, ILogger<ResearchAgentFunction> logger)
    {
        _research = research;
        _state = state;
        _publisher = publisher;
        _logger = logger;
    }

    [Function("research_agent")]
    public async Task Run([EventGridTrigger] EventGridEvent evt)
    {
        if (evt.EventType != "request.created") return;

        var data = evt.Data!.ToObjectFromJson<MeshEventData>();
        _logger.LogInformation("[ResearchAgent] handling {CorrelationId}", data.CorrelationId);

        var brief = await _research.ResearchAsync(data.Topic);
        await _state.RecordResearchAsync(data.CorrelationId, brief);

        await _publisher.PublishAsync(
            "research.completed",
            $"mesh/{data.CorrelationId}",
            data with { Research = brief });
    }
}
