using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ToolUseFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSingleton(_ =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set");

            // Managed identity in Azure; falls back to az login / VS credentials locally.
            return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        });

        services.AddSingleton<OrderLookupService>();
        services.AddSingleton<AgentService>();
    })
    .Build();

host.Run();
