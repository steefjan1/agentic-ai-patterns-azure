using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace MeshFunctions.Services;

public class SynthesisService
{
    private readonly ChatClient _chatClient;

    public SynthesisService(AzureOpenAIClient client)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = client.GetChatClient(deployment);
    }

    public async Task<string> SynthesizeAsync(string researchBrief, string factCheckResult)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Combine the research brief and the fact-check note into one final, clearly-caveated answer."),
            new UserChatMessage($"Research: {researchBrief}\n\nFact-check: {factCheckResult}"),
        };
        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages);
        return completion.Content[0].Text;
    }
}
