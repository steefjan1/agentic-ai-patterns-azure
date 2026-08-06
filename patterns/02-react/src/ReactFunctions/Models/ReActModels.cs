namespace ReactFunctions.Models;

public record ReActRequest(string Goal);

/// <summary>One iteration of the Think -> Act -> Observe loop.</summary>
public record ReActStep(string Thought, string Action, string ActionInput, string? Observation);

public record ReActDecision(string Thought, string Action, string ActionInput, bool IsFinalAnswer, string? FinalAnswer);

public record ReActResult(string Goal, string FinalAnswer, List<ReActStep> Transcript, bool HitIterationLimit);
