using Azure.AI.Projects;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrchestratorFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var credential = new DefaultAzureCredential();

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT is not set");
            return new AIProjectClient(new Uri(endpoint), credential);
        });

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_SEARCH_ENDPOINT is not set");
            var index = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX") ?? "knowledge-base";
            return new SearchClient(new Uri(endpoint), index, credential);
        });

        services.AddSingleton<ResearchToolService>();
        services.AddSingleton<DataToolService>();
        services.AddSingleton<AnalyticsToolService>();
        services.AddSingleton<FoundryAgentService>();
    })
    .Build();

host.Run();
