using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ToolUseFunctions.Models;
using ToolUseFunctions.Services;

namespace ToolUseFunctions.Functions;

public class AgentFunction
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly AgentService _agentService;
    private readonly ILogger<AgentFunction> _logger;

    public AgentFunction(AgentService agentService, ILogger<AgentFunction> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    [Function("agent")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agent")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<AgentRequest>(req.Body, JsonOptions);

        if (body is null || string.IsNullOrWhiteSpace(body.Message))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'message'." });
            return bad;
        }

        _logger.LogInformation("Agent request received: {Message}", body.Message);

        var result = await _agentService.HandleAsync(body.Message);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }
}
