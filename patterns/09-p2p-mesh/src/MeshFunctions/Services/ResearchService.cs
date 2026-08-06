using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace MeshFunctions.Services;

public class ResearchService
{
    private readonly ChatClient _chatClient;

    public ResearchService(AzureOpenAIClient client)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = client.GetChatClient(deployment);
    }

    public async Task<string> ResearchAsync(string topic)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Produce a concise, factual research brief (3-4 sentences) on the given topic."),
            new UserChatMessage(topic),
        };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }
}
