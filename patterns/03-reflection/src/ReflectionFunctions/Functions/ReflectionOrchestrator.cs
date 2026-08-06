using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using ReflectionFunctions.Models;

namespace ReflectionFunctions.Functions;

public class ReflectionOrchestrator
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int MaxRevisions = 2;

    [Function(nameof(RunReflectionLoop))]
    public async Task<ReflectionResult> RunReflectionLoop(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<ReflectionRequest>()!;
        var runId = context.InstanceId;
        var history = new List<string>();

        var draft = await context.CallActivityAsync<string>(
            nameof(ReflectionActivities.DraftAnswer), new DraftInput(runId, request.Prompt));
        history.Add(draft);

        var revisionCount = 0;
        var reachedLimit = true;

        while (revisionCount <= MaxRevisions)
        {
            var critique = await context.CallActivityAsync<Critique>(
                nameof(ReflectionActivities.ReflectOnDraft),
                new ReflectInput(runId, request.Prompt, request.Rubric, draft));

            if (critique.Pass)
            {
                reachedLimit = false;
                break;
            }

            if (revisionCount == MaxRevisions)
            {
                break;
            }

            draft = await context.CallActivityAsync<string>(
                nameof(ReflectionActivities.ReviseAnswer),
                new ReviseInput(runId, request.Prompt, draft, critique));
            history.Add(draft);
            revisionCount++;
        }

        return new ReflectionResult(runId, draft, revisionCount, reachedLimit, history);
    }

    [Function("reflect_start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reflect/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var request = await JsonSerializer.DeserializeAsync<ReflectionRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'prompt'." });
            return bad;
        }

        var normalized = request with { Rubric = string.IsNullOrWhiteSpace(request.Rubric) ? "Accurate, complete, clearly written." : request.Rubric };
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(RunReflectionLoop), normalized);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
