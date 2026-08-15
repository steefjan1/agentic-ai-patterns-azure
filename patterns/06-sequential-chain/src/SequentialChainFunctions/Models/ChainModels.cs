namespace SequentialChainFunctions.Models;

public record ChainRequest(string Text);

public record ChainResult(string RunId, string ExtractedFields, string Draft, string FinalText);
