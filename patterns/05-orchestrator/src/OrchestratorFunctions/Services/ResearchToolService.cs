using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace OrchestratorFunctions.Services;

/// <summary>Research specialist: grounds answers in a knowledge base via Azure AI Search.</summary>
public class ResearchToolService
{
    private readonly SearchClient _searchClient;

    public ResearchToolService(SearchClient searchClient) => _searchClient = searchClient;

    public async Task<string> ResearchAsync(string query)
    {
        SearchResults<SearchDocument> results;
        try
        {
            var options = new SearchOptions { Size = 3 };
            results = await _searchClient.SearchAsync<SearchDocument>(query, options);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Index not created yet (sample data/schema isn't seeded automatically by azd up) --
            // treat exactly like a zero-result search rather than failing the whole orchestrator run.
            return "No documentation found.";
        }

        var passages = new List<string>();
        await foreach (var result in results.GetResultsAsync())
        {
            if (result.Document.TryGetValue("content", out var content))
            {
                passages.Add(content?.ToString() ?? "");
            }
        }

        return passages.Count > 0 ? string.Join("\n---\n", passages) : "No documentation found.";
    }
}
