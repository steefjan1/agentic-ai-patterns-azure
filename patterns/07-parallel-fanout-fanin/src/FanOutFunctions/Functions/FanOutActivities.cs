using System.Text;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using OpenAI.Chat;
using FanOutFunctions.Models;

namespace FanOutFunctions.Functions;

public record SummarizeChunkInput(string RunId, int ChunkIndex, string ChunkText);
public record AggregateInput(string RunId, List<ChunkSummary> Summaries);

public class FanOutActivities
{
    private readonly ChatClient _chatClient;
    private readonly BlobServiceClient _blobService;

    public FanOutActivities(AzureOpenAIClient client, BlobServiceClient blobService)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = client.GetChatClient(deployment);
        _blobService = blobService;
    }

    [Function(nameof(SummarizeChunk))]
    public async Task<ChunkSummary> SummarizeChunk([ActivityTrigger] SummarizeChunkInput input)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Summarize this excerpt in 2-3 sentences. It is one part of a larger document."),
            new UserChatMessage(input.ChunkText),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        var summary = completion.Content[0].Text;

        await WriteAuditAsync(input.RunId, $"branch-{input.ChunkIndex:D3}", summary);

        return new ChunkSummary(input.ChunkIndex, summary);
    }

    [Function(nameof(AggregateSummaries))]
    public async Task<string> AggregateSummaries([ActivityTrigger] AggregateInput input)
    {
        var ordered = input.Summaries.OrderBy(s => s.ChunkIndex);
        var combined = string.Join("\n\n", ordered.Select(s => $"[Part {s.ChunkIndex + 1}] {s.Summary}"));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Synthesize these partial summaries into one coherent overall summary."),
            new UserChatMessage(combined),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        var finalSummary = completion.Content[0].Text;

        await WriteAuditAsync(input.RunId, "final-aggregate", finalSummary);
        return finalSummary;
    }

    private async Task WriteAuditAsync(string runId, string name, string content)
    {
        var containerName = Environment.GetEnvironmentVariable("BRANCH_AUDIT_CONTAINER") ?? "branch-audit";
        var container = _blobService.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient($"{runId}/{name}.txt");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
    }
}
