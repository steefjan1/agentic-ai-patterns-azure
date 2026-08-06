using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReactFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
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

        services.AddSingleton<AzureAiSearchService>();
    })
    .Build();

host.Run();
