using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using PlanningFunctions.Models;

namespace PlanningFunctions.Functions;

public class PlanningOrchestrator
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    [Function(nameof(RunPlan))]
    public async Task<PlanExecutionResult> RunPlan([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<PlanningRequest>()!;
        var runId = context.InstanceId;

        var plan = await context.CallActivityAsync<List<PlanStep>>(nameof(PlanningActivities.GeneratePlan), request.Goal);

        var results = new List<StepExecutionResult>();
        var succeeded = true;

        var retryOptions = new TaskOptions(new TaskRetryOptions(
            new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(5), backoffCoefficient: 2.0)));

        for (var i = 0; i < plan.Count; i++)
        {
            try
            {
                var result = await context.CallActivityAsync<StepExecutionResult>(
                    nameof(PlanningActivities.ExecuteStep),
                    new ExecuteStepInput(runId, i, plan[i]),
                    retryOptions);
                results.Add(result);
            }
            catch (TaskFailedException ex)
            {
                await context.CallActivityAsync(
                    nameof(PlanningActivities.EscalateFailure),
                    new EscalateInput(runId, i, plan[i].Description, ex.Message));

                results.Add(new StepExecutionResult(i, plan[i].Type, plan[i].Description, "failed", ex.Message));
                succeeded = false;
                break; // stop at first unrecoverable step; remaining steps stay unexecuted
            }
        }

        return new PlanExecutionResult(runId, request.Goal, results, succeeded);
    }

    [Function("plan_start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "plan/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var request = await JsonSerializer.DeserializeAsync<PlanningRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.Goal))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'goal'." });
            return bad;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(RunPlan), request);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
