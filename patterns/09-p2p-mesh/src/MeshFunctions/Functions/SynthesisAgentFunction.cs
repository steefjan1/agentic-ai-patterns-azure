using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MeshFunctions.Models;
using MeshFunctions.Services;

namespace MeshFunctions.Functions;

/// <summary>
/// The only mesh-aware agent: it subscribes to both "research.completed" and "factcheck.completed",
/// and only produces final output once it's seen both for a given correlation ID (tracked in
/// Cosmos DB, not in-memory -- this function may run on a different instance for each event).
/// </summary>
public class SynthesisAgentFunction
{
    private readonly SynthesisService _synthesis;
    private readonly CorrelationStateService _state;
    private readonly EventPublisherService _publisher;
    private readonly ILogger<SynthesisAgentFunction> _logger;

    public SynthesisAgentFunction(SynthesisService synthesis, CorrelationStateService state, EventPublisherService publisher, ILogger<SynthesisAgentFunction> logger)
    {
        _synthesis = synthesis;
        _state = state;
        _publisher = publisher;
        _logger = logger;
    }

    [Function("synthesis_agent")]
    public async Task Run([EventGridTrigger] EventGridEvent evt)
    {
        if (evt.EventType is not ("research.completed" or "factcheck.completed")) return;

        var data = evt.Data!.ToObjectFromJson<MeshEventData>();
        var record = await _state.GetAsync(data.CorrelationId);

        if (string.IsNullOrEmpty(record.ResearchResult) || string.IsNullOrEmpty(record.FactCheckResult))
        {
            _logger.LogInformation("[SynthesisAgent] {CorrelationId}: still waiting on the other branch", data.CorrelationId);
            return; // whichever event arrives second is the one that proceeds
        }

        _logger.LogInformation("[SynthesisAgent] {CorrelationId}: both inputs present, synthesizing", data.CorrelationId);

        var finalOutput = await _synthesis.SynthesizeAsync(record.ResearchResult, record.FactCheckResult);
        await _state.RecordFinalOutputAsync(data.CorrelationId, finalOutput);

        await _publisher.PublishAsync(
            "mesh.completed",
            $"mesh/{data.CorrelationId}",
            data with { FinalOutput = finalOutput });
    }
}
