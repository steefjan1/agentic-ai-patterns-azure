using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace HierarchicalFunctions.Services;

/// <summary>IT domain expert: grounds answers in Azure AI Search.</summary>
public class ITAgentService
{
    private readonly SearchClient _searchClient;

    public ITAgentService(SearchClient searchClient) => _searchClient = searchClient;

    public async Task<string> AnswerAsync(string question)
    {
        try
        {
            SearchResults<SearchDocument> results = await _searchClient.SearchAsync<SearchDocument>(
                question, new SearchOptions { Size = 1 });

            await foreach (var result in results.GetResultsAsync())
            {
                if (result.Document.TryGetValue("content", out var content))
                {
                    return content?.ToString() ?? "No IT guidance found.";
                }
            }
        }
        catch (Exception)
        {
            // Sample index may not be seeded yet in a fresh environment.
        }

        return $"IT has no documented procedure for '{question}'; standard provisioning lead time is 5 business days.";
    }
}
