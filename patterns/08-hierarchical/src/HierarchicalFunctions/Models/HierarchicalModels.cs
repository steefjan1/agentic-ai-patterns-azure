namespace HierarchicalFunctions.Models;

public record ManagerRequest(string Message);

public record DomainTask(string RunId, string Domain, string Question);

public record DomainReply(string RunId, string Domain, string Answer);

public record ManagerResponse(string RunId, string FinalAnswer, List<DomainReply> DomainReplies, bool TimedOut);
