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
        var options = new SearchOptions { Size = 3 };
        SearchResults<SearchDocument> results = await _searchClient.SearchAsync<SearchDocument>(query, options);

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
