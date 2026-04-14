using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MicrosoftExtensionsAiSample.Services;
using SemanticSearch.Api;

var host = new HostBuilder()
    .ConfigureAppConfiguration((_, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        var env = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        config.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false);
        config.AddEnvironmentVariables();
        config.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<CosmosDbService>();
        services.AddHostedService<CosmosInitializerHostedService>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
        {
            var cfg = context.Configuration;
            var baseUrl = cfg["Ollama:BaseUrl"] ?? "http://localhost:11434/";
            var model = cfg["Ollama:Model"] ?? "nomic-embed-text";
            return new OllamaEmbeddingGenerator(new Uri(baseUrl), model);
        });
        services.AddSingleton<SearchFunction>();
    })
    .Build();

await host.RunAsync();

internal sealed class CosmosInitializerHostedService(CosmosDbService cosmos) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => cosmos.InitializeAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
