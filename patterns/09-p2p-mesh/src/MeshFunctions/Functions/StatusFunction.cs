using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using MeshFunctions.Services;

namespace MeshFunctions.Functions;

public class StatusFunction
{
    private readonly CorrelationStateService _state;

    public StatusFunction(CorrelationStateService state) => _state = state;

    [Function("mesh_status")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "mesh/status/{correlationId}")] HttpRequestData req,
        string correlationId)
    {
        var record = await _state.GetAsync(correlationId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            record.CorrelationId,
            record.Topic,
            record.ResearchResult,
            record.FactCheckResult,
            record.FinalOutput,
            record.Status,
        });
        return response;
    }
}
