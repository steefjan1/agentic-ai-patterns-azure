using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OrchestratorFunctions.Models;
using OrchestratorFunctions.Services;

namespace OrchestratorFunctions.Functions;

public class OrchestratorFunction
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly FoundryAgentService _agentService;
    private readonly ILogger<OrchestratorFunction> _logger;

    public OrchestratorFunction(FoundryAgentService agentService, ILogger<OrchestratorFunction> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    [Function("orchestrate")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "orchestrate")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<OrchestratorRequest>(req.Body, JsonOptions);
        if (body is null || string.IsNullOrWhiteSpace(body.Message))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'message'." });
            return bad;
        }

        _logger.LogInformation("Orchestrator request: {Message}", body.Message);

        var (answer, toolsInvoked) = await _agentService.RunAsync(body.Message);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new OrchestratorResponse(answer, toolsInvoked));
        return response;
    }
}
