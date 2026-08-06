using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using HierarchicalFunctions.Models;
using HierarchicalFunctions.Services;

namespace HierarchicalFunctions.Functions;

public class ManagerFunction
{
    private readonly ManagerService _managerService;
    private readonly ILogger<ManagerFunction> _logger;

    public ManagerFunction(ManagerService managerService, ILogger<ManagerFunction> logger)
    {
        _managerService = managerService;
        _logger = logger;
    }

    [Function("manager_start")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "manager/start")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<ManagerRequest>(req.Body);
        if (body is null || string.IsNullOrWhiteSpace(body.Message))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'message'." });
            return bad;
        }

        _logger.LogInformation("Manager request: {Message}", body.Message);

        var result = await _managerService.HandleAsync(body.Message);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }
}
