using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using ReactFunctions.Models;

namespace ReactFunctions.Functions;

public class ReActOrchestrator
{
    private const int MaxIterations = 6;

    [Function(nameof(RunReActLoop))]
    public async Task<ReActResult> RunReActLoop(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<ReActRequest>()!;
        var logger = context.CreateReplaySafeLogger<ReActOrchestrator>();

        var transcript = new List<ReActStep>();
        string? finalAnswer = null;
        var hitLimit = true;

        for (var i = 0; i < MaxIterations; i++)
        {
            var transcriptJson = JsonSerializer.Serialize(new { goal = request.Goal, steps = transcript });

            var decision = await context.CallActivityAsync<ReActDecision>(
                nameof(ReActActivities.ThinkAndDecide), transcriptJson);

            if (decision.IsFinalAnswer)
            {
                finalAnswer = decision.FinalAnswer;
                transcript.Add(new ReActStep(decision.Thought, "answer", decision.ActionInput, null));
                hitLimit = false;
                break;
            }

            var observation = await context.CallActivityAsync<string>(
                nameof(ReActActivities.SearchObservation), decision.ActionInput);

            transcript.Add(new ReActStep(decision.Thought, decision.Action, decision.ActionInput, observation));

            if (!context.IsReplaying)
            {
                logger.LogInformation("Step {Step}: {Action}({Input})", i + 1, decision.Action, decision.ActionInput);
            }
        }

        finalAnswer ??= "I was unable to reach a confident answer within the allotted reasoning steps.";

        return new ReActResult(request.Goal, finalAnswer, transcript, hitLimit);
    }

    [Function("react_start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "react/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var request = await JsonSerializer.DeserializeAsync<ReActRequest>(req.Body);
        if (request is null || string.IsNullOrWhiteSpace(request.Goal))
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'goal'." });
            return bad;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(RunReActLoop), request);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
