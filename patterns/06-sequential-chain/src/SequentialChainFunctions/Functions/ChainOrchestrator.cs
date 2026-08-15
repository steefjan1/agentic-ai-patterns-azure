using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using SequentialChainFunctions.Models;

namespace SequentialChainFunctions.Functions;

public class ChainOrchestrator
{
    // Client JSON commonly uses camelCase/lowercase property names (curl, PowerShell's
    // ConvertTo-Json, JS fetch, etc.) -- match them against our PascalCase C# record properties.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Function(nameof(RunChain))]
    public async Task<ChainResult> RunChain([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<ChainRequest>()!;
        var runId = context.InstanceId;

        var retryOptions = new TaskOptions(new TaskRetryOptions(
            new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(5), backoffCoefficient: 2.0)));

        // Fixed, linear pipeline -- each activity's output is the next activity's only input.
        // No fan-out, no branching: that's what makes this a chain rather than a plan or a mesh.
        var extracted = await context.CallActivityAsync<string>(nameof(ChainActivities.ExtractFields), request.Text, retryOptions);
        var draft = await context.CallActivityAsync<string>(nameof(ChainActivities.DraftResponse), extracted, retryOptions);
        var final = await context.CallActivityAsync<string>(nameof(ChainActivities.ValidateDraft), draft, retryOptions);

        await context.CallActivityAsync(nameof(ChainActivities.WriteOutputBlob), new WriteOutputInput(runId, final));
        await context.CallActivityAsync(nameof(ChainActivities.SendToServiceBus), final);

        return new ChainResult(runId, extracted, draft, final);
    }

    [Function("chain_start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "chain/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var request = await JsonSerializer.DeserializeAsync<ChainRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'text'." });
            return bad;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(RunChain), request);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
