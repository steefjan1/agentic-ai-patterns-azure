using System.Text;
using Azure.AI.OpenAI;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using OpenAI.Chat;

namespace SequentialChainFunctions.Functions;

public record WriteOutputInput(string RunId, string FinalText);

public class ChainActivities
{
    private readonly ChatClient _chatClient;
    private readonly BlobServiceClient _blobService;
    private readonly ServiceBusClient _serviceBusClient;

    public ChainActivities(AzureOpenAIClient openAiClient, BlobServiceClient blobService, ServiceBusClient serviceBusClient)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = openAiClient.GetChatClient(deployment);
        _blobService = blobService;
        _serviceBusClient = serviceBusClient;
    }

    // Stage 1: pull structured signal out of the raw inbound text.
    [Function(nameof(ExtractFields))]
    public async Task<string> ExtractFields([ActivityTrigger] string inputText)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Extract intent, entities, and sentiment from the input as JSON."),
            new UserChatMessage(inputText),
        };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }

    // Stage 2: turn the extracted fields into a first-pass response. Only ever sees stage 1's
    // output, never the original raw text -- each stage is deliberately blind to everything
    // upstream of its immediate input, which is what makes this a *chain* and not a shared-context loop.
    [Function(nameof(DraftResponse))]
    public async Task<string> DraftResponse([ActivityTrigger] string extractedFields)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Draft a customer response based on the extracted fields."),
            new UserChatMessage(extractedFields),
        };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }

    // Stage 3: a distinct pass, with a distinct prompt, whose only job is to check stage 2's
    // work before it goes out the door.
    [Function(nameof(ValidateDraft))]
    public async Task<string> ValidateDraft([ActivityTrigger] string draft)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Check the draft for tone and factual consistency with the extracted fields. Return the final approved text."),
            new UserChatMessage(draft),
        };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }

    [Function(nameof(WriteOutputBlob))]
    public async Task WriteOutputBlob([ActivityTrigger] WriteOutputInput input)
    {
        var containerName = Environment.GetEnvironmentVariable("OUTPUT_CONTAINER") ?? "output";
        var container = _blobService.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient($"{input.RunId}.txt");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input.FinalText));
        await blob.UploadAsync(stream, overwrite: true);
    }

    [Function(nameof(SendToServiceBus))]
    public async Task SendToServiceBus([ActivityTrigger] string finalText)
    {
        var queueName = Environment.GetEnvironmentVariable("SERVICEBUS_QUEUE") ?? "chain-output";
        var sender = _serviceBusClient.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage(finalText));
    }
}
