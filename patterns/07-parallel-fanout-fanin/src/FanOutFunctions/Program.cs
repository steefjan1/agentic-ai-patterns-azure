using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;

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
            var account = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT")
                ?? throw new InvalidOperationException("AZURE_STORAGE_ACCOUNT is not set");
            return new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential);
        });
    })
    .Build();

host.Run();
