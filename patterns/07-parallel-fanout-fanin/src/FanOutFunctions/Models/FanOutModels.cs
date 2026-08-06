namespace FanOutFunctions.Models;

public record FanOutRequest(string Document, int ChunkSizeChars = 2000);

public record ChunkSummary(int ChunkIndex, string Summary);

public record FanOutResult(string RunId, int ChunkCount, string FinalSummary, List<ChunkSummary> BranchResults);
