using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using FanOutFunctions.Models;

namespace FanOutFunctions.Functions;

public class FanOutOrchestrator
{
    [Function(nameof(RunFanOut))]
    public async Task<FanOutResult> RunFanOut([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var request = context.GetInput<FanOutRequest>()!;
        var runId = context.InstanceId;

        var chunks = SplitIntoChunks(request.Document, request.ChunkSizeChars);

        var retryOptions = new TaskOptions(new TaskRetryOptions(
            new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(3), backoffCoefficient: 2.0)));

        // ---- Fan-out: schedule all branches without awaiting individually ----
        var tasks = new List<Task<ChunkSummary>>();
        for (var i = 0; i < chunks.Count; i++)
        {
            tasks.Add(context.CallActivityAsync<ChunkSummary>(
                nameof(FanOutActivities.SummarizeChunk),
                new SummarizeChunkInput(runId, i, chunks[i]),
                retryOptions));
        }

        // ---- Fan-in: wait for every branch to complete ----
        ChunkSummary[] results = await Task.WhenAll(tasks);

        var finalSummary = await context.CallActivityAsync<string>(
            nameof(FanOutActivities.AggregateSummaries),
            new AggregateInput(runId, results.ToList()));

        return new FanOutResult(runId, chunks.Count, finalSummary, results.OrderBy(r => r.ChunkIndex).ToList());
    }

    private static List<string> SplitIntoChunks(string text, int chunkSizeChars)
    {
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += chunkSizeChars)
        {
            chunks.Add(text.Substring(i, Math.Min(chunkSizeChars, text.Length - i)));
        }
        return chunks.Count > 0 ? chunks : new List<string> { text };
    }

    [Function("fanout_start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "fanout/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var request = await JsonSerializer.DeserializeAsync<FanOutRequest>(req.Body);
        if (request is null || string.IsNullOrWhiteSpace(request.Document))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Request body must include a non-empty 'document'." });
            return bad;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(RunFanOut), request);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }
}
