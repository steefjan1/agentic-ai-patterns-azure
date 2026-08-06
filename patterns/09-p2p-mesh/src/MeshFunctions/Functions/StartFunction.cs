using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using MeshFunctions.Models;
using MeshFunctions.Services;

namespace MeshFunctions.Functions;

public class StartFunction
{
    private readonly EventPublisherService _publisher;
    private readonly CorrelationStateService _state;

    public StartFunction(EventPublisherService publisher, CorrelationStateService state)
    {
        _publisher = publisher;
        _state = state;
    }

    [Function("mesh_start")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "mesh/start")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<MeshStartRequest>(req.Body);
        if (body is null || string.IsNullOrWhiteSpace(body.Topic))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'topic'." });
            return bad;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        await _state.CreateAsync(correlationId, body.Topic);

        await _publisher.PublishAsync(
            eventType: "request.created",
            subject: $"mesh/{correlationId}",
            data: new MeshEventData(correlationId, body.Topic, null, null, null));

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { correlationId, statusUrl = $"/api/mesh/status/{correlationId}" });
        return response;
    }
}
