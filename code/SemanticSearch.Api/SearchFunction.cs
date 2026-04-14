using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.AI;
using MicrosoftExtensionsAiSample.Services;

namespace SemanticSearch.Api;

public sealed class SearchFunction(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    CosmosDbService db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("Search")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "search")] HttpRequestData req,
        FunctionContext context)
    {
        SearchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SearchRequest>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.");
            return bad;
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Query))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Query is required.");
            return bad;
        }

        var max = body.MaxResults is > 0 and <= 50 ? body.MaxResults : 10;
        var queryEmbedding = await embeddings.GenerateAsync(body.Query, cancellationToken: context.CancellationToken);
        var posts = await db.SearchSimilarBlogPostsAsync(queryEmbedding.Vector.ToArray(), maxResults: max);
        var response = posts.Select(p => new SearchResultItem(p.Id, p.Title, p.Url, p.ImageUrl, p.PublishedAt)).ToList();

        var ok = req.CreateResponse(HttpStatusCode.OK);
        ok.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await ok.WriteStringAsync(JsonSerializer.Serialize(response, JsonOptions));
        return ok;
    }
}

internal sealed record SearchRequest(string Query, int MaxResults = 10);

internal sealed record SearchResultItem(
    string Id,
    string Title,
    string Url,
    string? ImageUrl = null,
    DateTimeOffset? PublishedAt = null);
