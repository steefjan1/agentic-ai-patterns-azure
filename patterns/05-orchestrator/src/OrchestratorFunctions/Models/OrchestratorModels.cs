namespace OrchestratorFunctions.Models;

public record OrchestratorRequest(string Message);

public record OrchestratorResponse(string Answer, List<string> ToolsInvoked);
