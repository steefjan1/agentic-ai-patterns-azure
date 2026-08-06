namespace ReflectionFunctions.Models;

public record ReflectionRequest(string Prompt, string Rubric);

public record Critique(bool Pass, List<string> Issues);

public record ReflectionResult(
    string RunId,
    string FinalAnswer,
    int RevisionCount,
    bool ReachedRevisionLimit,
    List<string> DraftHistory);
