using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using HierarchicalFunctions.Services;

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
            var ns = Environment.GetEnvironmentVariable("SERVICEBUS_FULLYQUALIFIEDNAMESPACE")
                ?? throw new InvalidOperationException("SERVICEBUS_FULLYQUALIFIEDNAMESPACE is not set");
            return new ServiceBusClient(ns, credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
                ?? throw new InvalidOperationException("COSMOS_ENDPOINT is not set");
            return new CosmosClient(endpoint, credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_SEARCH_ENDPOINT is not set");
            var index = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX") ?? "it-knowledge-base";
            return new SearchClient(new Uri(endpoint), index, credential);
        });

        services.AddSingleton<FinanceAgentService>();
        services.AddSingleton<OpsAgentService>();
        services.AddSingleton<ITAgentService>();
        services.AddSingleton<ManagerService>();
    })
    .Build();

host.Run();
