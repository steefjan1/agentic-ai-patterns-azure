namespace MeshFunctions.Models;

public record MeshStartRequest(string Topic);

public record MeshEventData(string CorrelationId, string Topic, string? Research, string? FactCheck, string? FinalOutput);

/// <summary>Cosmos DB document tracking which events have landed for a correlation ID.</summary>
public class CorrelationRecord
{
    public string id { get; set; } = default!; // Cosmos requires lowercase 'id'
    public string CorrelationId { get; set; } = default!;
    public string Topic { get; set; } = default!;
    public string? ResearchResult { get; set; }
    public string? FactCheckResult { get; set; }
    public string? FinalOutput { get; set; }
    public string Status { get; set; } = "in_progress";
}
