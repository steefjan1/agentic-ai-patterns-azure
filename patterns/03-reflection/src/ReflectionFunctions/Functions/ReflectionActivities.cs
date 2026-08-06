using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using OpenAI.Chat;
using ReflectionFunctions.Models;

namespace ReflectionFunctions.Functions;

public record DraftInput(string RunId, string Prompt);
public record ReflectInput(string RunId, string Prompt, string Rubric, string Draft);
public record ReviseInput(string RunId, string Prompt, string Draft, Critique Critique);
public record AuditWrite(string RunId, string Stage, string Content);

public class ReflectionActivities
{
    private readonly ChatClient _primaryClient;
    private readonly ChatClient _critiqueClient;
    private readonly BlobServiceClient _blobService;

    public ReflectionActivities(AzureOpenAIClient client, BlobServiceClient blobService)
    {
        var primary = Environment.GetEnvironmentVariable("AZURE_OPENAI_PRIMARY_DEPLOYMENT") ?? "gpt-4.1";
        var critique = Environment.GetEnvironmentVariable("AZURE_OPENAI_CRITIQUE_DEPLOYMENT") ?? "gpt-4.1-mini";
        _primaryClient = client.GetChatClient(primary);
        _critiqueClient = client.GetChatClient(critique);
        _blobService = blobService;
    }

    [Function(nameof(DraftAnswer))]
    public async Task<string> DraftAnswer([ActivityTrigger] DraftInput input)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You produce a first-pass answer. Be thorough but do not over-explain."),
            new UserChatMessage(input.Prompt),
        };
        ChatCompletion completion = await _primaryClient.CompleteChatAsync(messages);
        var draft = completion.Content[0].Text;
        await WriteAuditAsync(input.RunId, "01-draft", draft);
        return draft;
    }

    [Function(nameof(ReflectOnDraft))]
    public async Task<Critique> ReflectOnDraft([ActivityTrigger] ReflectInput input)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"""
                You are a strict reviewer. Evaluate the draft against this rubric: "{input.Rubric}".
                Respond as STRICT JSON: {{ "pass": true|false, "issues": ["issue 1", "issue 2"] }}.
                "pass" is true only if there are no material issues.
                """),
            new UserChatMessage($"Original request: {input.Prompt}\n\nDraft:\n{input.Draft}"),
        };

        ChatCompletion completion = await _critiqueClient.CompleteChatAsync(messages, options);
        var raw = completion.Content[0].Text;
        await WriteAuditAsync(input.RunId, "02-critique", raw);

        var critique = JsonSerializer.Deserialize<Critique>(raw, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new Critique(true, new List<string>());

        return critique;
    }

    [Function(nameof(ReviseAnswer))]
    public async Task<string> ReviseAnswer([ActivityTrigger] ReviseInput input)
    {
        var issues = string.Join("; ", input.Critique.Issues);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Revise the draft to address every issue raised by the reviewer. Return only the revised answer."),
            new UserChatMessage($"Original request: {input.Prompt}\n\nDraft:\n{input.Draft}\n\nReviewer issues:\n{issues}"),
        };

        ChatCompletion completion = await _primaryClient.CompleteChatAsync(messages);
        var revised = completion.Content[0].Text;
        await WriteAuditAsync(input.RunId, "03-revision", revised);
        return revised;
    }

    private async Task WriteAuditAsync(string runId, string stage, string content)
    {
        var containerName = Environment.GetEnvironmentVariable("AUDIT_CONTAINER") ?? "reflection-audit";
        var container = _blobService.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient($"{runId}/{stage}.txt");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
    }
}
