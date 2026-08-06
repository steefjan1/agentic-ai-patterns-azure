using Microsoft.Azure.Cosmos;
using MeshFunctions.Models;

namespace MeshFunctions.Services;

/// <summary>
/// Shared, eventually-consistent state the mesh uses to know when enough of its independent
/// agents have weighed in on a given correlation ID -- deliberately has no authority over
/// what any agent does, it's just a record of what's happened so far.
/// </summary>
public class CorrelationStateService
{
    private readonly CosmosClient _cosmosClient;
    private Container Container => _cosmosClient.GetContainer("mesh", "correlations");

    public CorrelationStateService(CosmosClient cosmosClient) => _cosmosClient = cosmosClient;

    public async Task CreateAsync(string correlationId, string topic)
    {
        var record = new CorrelationRecord { id = correlationId, CorrelationId = correlationId, Topic = topic };
        await Container.UpsertItemAsync(record, new PartitionKey(correlationId));
    }

    public async Task<CorrelationRecord> RecordResearchAsync(string correlationId, string result)
    {
        var record = await GetAsync(correlationId);
        record.ResearchResult = result;
        await Container.UpsertItemAsync(record, new PartitionKey(correlationId));
        return record;
    }

    public async Task<CorrelationRecord> RecordFactCheckAsync(string correlationId, string result)
    {
        var record = await GetAsync(correlationId);
        record.FactCheckResult = result;
        await Container.UpsertItemAsync(record, new PartitionKey(correlationId));
        return record;
    }

    public async Task<CorrelationRecord> RecordFinalOutputAsync(string correlationId, string output)
    {
        var record = await GetAsync(correlationId);
        record.FinalOutput = output;
        record.Status = "complete";
        await Container.UpsertItemAsync(record, new PartitionKey(correlationId));
        return record;
    }

    public async Task<CorrelationRecord> GetAsync(string correlationId)
    {
        try
        {
            var response = await Container.ReadItemAsync<CorrelationRecord>(correlationId, new PartitionKey(correlationId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new CorrelationRecord { id = correlationId, CorrelationId = correlationId, Topic = "" };
        }
    }
}
