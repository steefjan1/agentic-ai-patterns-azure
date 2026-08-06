namespace ToolUseFunctions.Models;

public record AgentRequest(string Message);

public record AgentResponse(string Answer, bool ToolCalled);
