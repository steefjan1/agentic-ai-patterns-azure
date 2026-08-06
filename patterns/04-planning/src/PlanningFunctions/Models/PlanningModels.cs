namespace PlanningFunctions.Models;

public record PlanningRequest(string Goal);

public record PlanStep(string Type, string Description);

public record StepExecutionResult(int StepIndex, string Type, string Description, string Status, string? Output);

public record PlanExecutionResult(string RunId, string Goal, List<StepExecutionResult> Steps, bool Succeeded);
