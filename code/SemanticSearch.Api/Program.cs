using Microsoft.Extensions.AI;
using MicrosoftExtensionsAiSample.Models;
using MicrosoftExtensionsAiSample.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CosmosDbService>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
{
    var baseUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434/";
    var model = builder.Configuration["Ollama:Model"] ?? "nomic-embed-text";
    return new OllamaEmbeddingGenerator(new Uri(baseUrl), model);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();

app.UseCors();

var cosmos = app.Services.GetRequiredService<CosmosDbService>();
await cosmos.InitializeAsync();

app.MapPost("/api/search", async (
    SearchRequest body,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    CosmosDbService db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(body.Query))
    {
        return Results.BadRequest("Query is required.");
    }

    var max = body.MaxResults is > 0 and <= 50 ? body.MaxResults : 10;
    var queryEmbedding = await embeddings.GenerateAsync(body.Query, cancellationToken: cancellationToken);
    var posts = await db.SearchSimilarBlogPostsAsync(queryEmbedding.Vector.ToArray(), maxResults: max);
    var response = posts.Select(p => new SearchResultItem(p.Id, p.Title, p.Url)).ToList();
    return Results.Ok(response);
})
.WithName("SearchBlogPosts");

app.Run();

internal sealed record SearchRequest(string Query, int MaxResults = 10);

internal sealed record SearchResultItem(string Id, string Title, string Url);
