using Microsoft.Azure.Cosmos;

namespace HierarchicalFunctions.Services;

/// <summary>Finance domain expert: grounds answers in a Cosmos DB container.</summary>
public class FinanceAgentService
{
    private readonly CosmosClient _cosmosClient;

    public FinanceAgentService(CosmosClient cosmosClient) => _cosmosClient = cosmosClient;

    public async Task<string> AnswerAsync(string question)
    {
        try
        {
            var container = _cosmosClient.GetContainer("hierarchical", "budgets");
            var iterator = container.GetItemQueryIterator<dynamic>(
                "SELECT TOP 1 * FROM c ORDER BY c._ts DESC");

            if (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync();
                var item = page.FirstOrDefault();
                if (item is not null)
                {
                    return $"Latest budget snapshot: {item}. Question was: '{question}'.";
                }
            }
        }
        catch (CosmosException)
        {
            // Sample container may not be seeded yet in a fresh environment.
        }

        return $"Finance has no specific budget line for '{question}' this quarter; treat as discretionary spend pending approval.";
    }
}
