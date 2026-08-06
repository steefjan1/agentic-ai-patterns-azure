using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace MeshFunctions.Services;

public class FactCheckService
{
    private readonly SearchClient _searchClient;

    public FactCheckService(SearchClient searchClient) => _searchClient = searchClient;

    public async Task<string> VerifyAsync(string researchBrief)
    {
        try
        {
            SearchResults<SearchDocument> results = await _searchClient.SearchAsync<SearchDocument>(
                researchBrief, new SearchOptions { Size = 1 });

            await foreach (var result in results.GetResultsAsync())
            {
                if (result.Document.TryGetValue("content", out var content))
                {
                    return $"Corroborated by knowledge base: {content}";
                }
            }
        }
        catch (Exception)
        {
            // Sample index may not be seeded yet.
        }

        return "No corroborating source found in the knowledge base; treat as unverified.";
    }
}
