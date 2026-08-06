using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MeshFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var credential = new DefaultAzureCredential();

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set");
            return new AzureOpenAIClient(new Uri(endpoint), credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_SEARCH_ENDPOINT is not set");
            var index = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX") ?? "knowledge-base";
            return new SearchClient(new Uri(endpoint), index, credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
                ?? throw new InvalidOperationException("COSMOS_ENDPOINT is not set");
            return new CosmosClient(endpoint, credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("EVENTGRID_TOPIC_ENDPOINT")
                ?? throw new InvalidOperationException("EVENTGRID_TOPIC_ENDPOINT is not set");
            return new EventGridPublisherClient(new Uri(endpoint), credential);
        });

        services.AddSingleton<EventPublisherService>();
        services.AddSingleton<CorrelationStateService>();
        services.AddSingleton<ResearchService>();
        services.AddSingleton<FactCheckService>();
        services.AddSingleton<SynthesisService>();
    })
    .Build();

host.Run();
