using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace ReactFunctions.Services;

/// <summary>The "Observe" half of the loop — grounds the agent's next thought in real data.</summary>
public class AzureAiSearchService
{
    private readonly SearchClient _searchClient;

    public AzureAiSearchService(SearchClient searchClient) => _searchClient = searchClient;

    public async Task<string> SearchAsync(string query, int top = 3)
    {
        var options = new SearchOptions { Size = top };
        options.Select.Add("content");
        options.Select.Add("title");

        SearchResults<SearchDocument> results = await _searchClient.SearchAsync<SearchDocument>(query, options);

        var passages = new List<string>();
        await foreach (SearchResult<SearchDocument> result in results.GetResultsAsync())
        {
            var title = result.Document.TryGetValue("title", out var t) ? t?.ToString() : "untitled";
            var content = result.Document.TryGetValue("content", out var c) ? c?.ToString() : "";
            passages.Add($"[{title}] {content}");
        }

        return passages.Count > 0
            ? string.Join("\n---\n", passages)
            : "No relevant results found in the knowledge base.";
    }
}
