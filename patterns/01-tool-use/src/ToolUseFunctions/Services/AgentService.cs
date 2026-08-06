using System.Text.Json;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using ToolUseFunctions.Models;

namespace ToolUseFunctions.Services;

/// <summary>
/// Implements the Tool Use loop: one Azure OpenAI call to decide whether (and how)
/// to invoke a tool, run the tool, then one more call to produce the final answer.
/// </summary>
public class AgentService
{
    private readonly ChatClient _chatClient;
    private readonly OrderLookupService _orderLookup;

    private static readonly ChatTool GetOrderStatusTool = ChatTool.CreateFunctionTool(
        functionName: "get_order_status",
        functionDescription: "Look up the shipping status and ETA for a customer order by its order ID.",
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "orderId": { "type": "string", "description": "The order ID, e.g. '1042'." }
            },
            "required": ["orderId"]
        }
        """));

    public AgentService(AzureOpenAIClient client, OrderLookupService orderLookup)
    {
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4.1";
        _chatClient = client.GetChatClient(deployment);
        _orderLookup = orderLookup;
    }

    public async Task<AgentResponse> HandleAsync(string userMessage)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "You are a customer support agent. Use the get_order_status tool whenever " +
                "the customer asks about an order. Never make up an order status."),
            new UserChatMessage(userMessage),
        };

        var options = new ChatCompletionOptions();
        options.Tools.Add(GetOrderStatusTool);

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);

        if (completion.FinishReason == ChatFinishReason.ToolCalls)
        {
            messages.Add(new AssistantChatMessage(completion));

            foreach (var toolCall in completion.ToolCalls)
            {
                if (toolCall.FunctionName == "get_order_status")
                {
                    using var argsDoc = JsonDocument.Parse(toolCall.FunctionArguments);
                    var orderId = argsDoc.RootElement.GetProperty("orderId").GetString() ?? "";

                    var result = await _orderLookup.GetOrderStatusAsync(orderId);
                    var resultJson = JsonSerializer.Serialize(result);

                    messages.Add(new ToolChatMessage(toolCall.Id, resultJson));
                }
            }

            // Second round-trip: model turns the tool result into a natural-language answer.
            ChatCompletion final = await _chatClient.CompleteChatAsync(messages);
            return new AgentResponse(final.Content[0].Text, ToolCalled: true);
        }

        return new AgentResponse(completion.Content[0].Text, ToolCalled: false);
    }
}
