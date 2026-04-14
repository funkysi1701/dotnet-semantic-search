using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using MicrosoftExtensionsAiSample.Models;

namespace MicrosoftExtensionsAiSample.Services;

/// <summary>
/// Azure Cosmos DB for NoSQL with vector indexing (768-dimensional cosine vectors; embeddings from OpenAI text-embedding-3-small with shortened dimensions by default).
/// Credentials are read from <see cref="IConfiguration"/> (JSON, then environment, then user secrets so local secrets win over empty env placeholders).
/// Prefer <c>dotnet user-secrets set "Cosmos:ConnectionString" "..."</c> for local development.
/// </summary>
public sealed class CosmosDbService : IDisposable
{
    public const int VectorDimensions = 768;

    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly string _containerName;
    private Container? _container;

    public CosmosDbService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var (ep, key) = ResolveCredentials(configuration);
        _client = new CosmosClient(ep, key, new CosmosClientOptions
        {
            ApplicationName = "dotnet-semantic-search"
        });

        _databaseName = FirstNonEmpty(
            configuration,
            "COSMOS_DATABASE",
            "Cosmos:Database")
            ?? "semantic-search-blog";

        _containerName = FirstNonEmpty(
            configuration,
            "COSMOS_CONTAINER",
            "Cosmos:Container")
            ?? "blog-embeddings";
    }

    private static (string Endpoint, string Key) ResolveCredentials(IConfiguration configuration)
    {
        var conn = FirstNonEmpty(
            configuration,
            "COSMOS_CONNECTION_STRING",
            "Cosmos:ConnectionString",
            "ConnectionStrings:COSMOS",
            "ConnectionStrings:Cosmos");

        if (!string.IsNullOrWhiteSpace(conn) && TryParseAccountConnectionString(conn, out var endpoint, out var accountKey))
        {
            return (endpoint, accountKey);
        }

        var ep = FirstNonEmpty(
            configuration,
            "COSMOS_ENDPOINT",
            "AZURE_COSMOS_ENDPOINT",
            "Cosmos:Endpoint");
        var ky = FirstNonEmpty(
            configuration,
            "COSMOS_KEY",
            "COSMOS_ACCOUNT_KEY",
            "Cosmos:Key",
            "Cosmos:AccountKey");
        if (string.IsNullOrWhiteSpace(ep) || string.IsNullOrWhiteSpace(ky))
        {
            throw new InvalidOperationException(
                "Configure Azure Cosmos DB: set user secret Cosmos:ConnectionString (see README), " +
                "or COSMOS_CONNECTION_STRING / COSMOS_ENDPOINT + COSMOS_KEY environment variables.");
        }

        return (ep.TrimEnd('/'), ky);
    }

    private static string? FirstNonEmpty(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool TryParseAccountConnectionString(string connectionString, out string endpoint, out string key)
    {
        string? ep = null, ky = null;
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            if (name.Equals("AccountEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                ep = value;
            }
            else if (name.Equals("AccountKey", StringComparison.OrdinalIgnoreCase))
            {
                ky = value;
            }
        }

        if (!string.IsNullOrEmpty(ep) && !string.IsNullOrEmpty(ky))
        {
            endpoint = ep.TrimEnd('/');
            key = ky;
            return true;
        }

        endpoint = string.Empty;
        key = string.Empty;
        return false;
    }

    public async Task InitializeAsync()
    {
        var databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(_databaseName);
        var database = databaseResponse.Database;

        var embeddings = new Collection<Embedding>
        {
            new()
            {
                Path = "/vector",
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = VectorDimensions
            }
        };

        var properties = new ContainerProperties(_containerName, partitionKeyPath: "/id")
        {
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(embeddings),
            IndexingPolicy = new IndexingPolicy
            {
                IncludedPaths = { new IncludedPath { Path = "/*" } },
                ExcludedPaths = { new ExcludedPath { Path = "/vector/*" } },
                VectorIndexes =
                {
                    new VectorIndexPath { Path = "/vector", Type = VectorIndexType.QuantizedFlat }
                }
            }
        };

        var containerResponse = await database.CreateContainerIfNotExistsAsync(properties);
        _container = containerResponse.Container;
    }

    private Container Container =>
        _container ?? throw new InvalidOperationException("Call InitializeAsync before using the container.");

    public async Task ClearAllDataAsync()
    {
        var query = new QueryDefinition("SELECT VALUE c.id FROM c");
        using var feed = Container.GetItemQueryIterator<string>(query);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            foreach (var id in page)
            {
                await Container.DeleteItemAsync<object>(id, new PartitionKey(id));
            }
        }
    }

    public async Task<BlogPost> SaveBlogPostAsync(BlogPost blogPost)
    {
        ValidateEmbedding(blogPost.Vector);
        var id = SanitizeCosmosId(blogPost.Id);
        var payload = BuildItemPayload(
            id,
            parentPostId: SanitizeCosmosId(blogPost.Id),
            chunkIndex: 0,
            SanitizeFreeText(blogPost.Title),
            SanitizeFreeText(blogPost.Url),
            blogPost.ImageUrl,
            blogPost.Vector);

        try
        {
            await Container.UpsertItemAsync(payload, new PartitionKey(id));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            ThrowCosmosWriteException(ex, blogPost.Vector.Length);
        }

        return blogPost;
    }

    public async Task SaveBlogPostChunksAsync(
        string parentPostId,
        string title,
        string url,
        string? imageUrl,
        IReadOnlyList<(int chunkIndex, float[] vector)> chunks)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        foreach (var (chunkIndex, vector) in chunks)
        {
            ValidateEmbedding(vector);
            var id = SanitizeCosmosId(Guid.NewGuid().ToString());
            var payload = BuildItemPayload(
                id,
                parentPostId: SanitizeCosmosId(parentPostId),
                chunkIndex,
                SanitizeFreeText(title),
                SanitizeFreeText(url),
                imageUrl,
                vector);

            try
            {
                await Container.UpsertItemAsync(payload, new PartitionKey(id));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                ThrowCosmosWriteException(ex, vector.Length);
            }
        }
    }

    public async Task<List<BlogPost>> SearchSimilarBlogPostsAsync(float[] queryVector, int maxResults = 5)
    {
        if (queryVector.Length != VectorDimensions)
        {
            throw new ArgumentException($"Expected {VectorDimensions} dimensions, got {queryVector.Length}.", nameof(queryVector));
        }

        var top = Math.Max(maxResults * 6, 24);
        var sql =
            $"SELECT TOP {top} c.id, c.title, c.url, c.image_url, c.parent_post_id, VectorDistance(c.vector, @embedding) AS dist " +
            "FROM c ORDER BY VectorDistance(c.vector, @embedding)";

        var query = new QueryDefinition(sql).WithParameter("@embedding", queryVector);
        using var feed = Container.GetItemQueryIterator<VectorSearchRow>(query);

        var bestByParent = new Dictionary<string, (BlogPost post, double dist)>(StringComparer.Ordinal);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            foreach (var row in page)
            {
                var groupKey = !string.IsNullOrEmpty(row.ParentPostId) ? row.ParentPostId : row.Id;
                if (!bestByParent.TryGetValue(groupKey, out var existing) || row.Dist < existing.dist)
                {
                    string? imageUrl = string.IsNullOrWhiteSpace(row.ImageUrl) ? null : row.ImageUrl;
                    if (imageUrl is null && bestByParent.TryGetValue(groupKey, out var prev))
                    {
                        imageUrl = prev.post.ImageUrl;
                    }

                    var blog = new BlogPost
                    {
                        Id = !string.IsNullOrEmpty(row.ParentPostId) ? row.ParentPostId : row.Id,
                        Title = row.Title ?? string.Empty,
                        Url = row.Url ?? string.Empty,
                        ImageUrl = imageUrl
                    };
                    bestByParent[groupKey] = (blog, row.Dist);
                }
                else if (!string.IsNullOrWhiteSpace(row.ImageUrl) && string.IsNullOrWhiteSpace(existing.post.ImageUrl))
                {
                    existing.post.ImageUrl = row.ImageUrl;
                }
            }
        }

        // Same permalink can appear under different parent_post_id values (e.g. legacy rows from
        // random GUID ids per RSS fetch). Keep the single best-scoring row per canonical URL.
        var ranked = bestByParent.Values.OrderBy(x => x.dist).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<BlogPost>();
        foreach (var entry in ranked)
        {
            var key = PostEquivalenceKey(entry.post);
            if (!seen.Add(key))
            {
                continue;
            }

            results.Add(entry.post);
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static string PostEquivalenceKey(BlogPost p)
    {
        if (!string.IsNullOrWhiteSpace(p.Url))
        {
            return NormalizePostUrl(p.Url);
        }

        return "id:" + p.Id;
    }

    private static string NormalizePostUrl(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed.ToLowerInvariant();
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }

        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{path}{uri.Query}"
            .ToLowerInvariant();
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Cosmos requires a JSON property exactly <c>id</c> (case-sensitive) for partition path <c>/id</c>.
    /// A <see cref="Dictionary{TKey,TValue}"/> avoids serializer naming mismatches.
    /// </summary>
    private static Dictionary<string, object?> BuildItemPayload(
        string id,
        string parentPostId,
        int chunkIndex,
        string title,
        string url,
        string? imageUrl,
        float[] vector)
    {
        var doc = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["parent_post_id"] = parentPostId,
            ["chunk_index"] = chunkIndex,
            ["title"] = title,
            ["url"] = url,
            ["vector"] = vector
        };
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            doc["image_url"] = SanitizeFreeText(imageUrl.Trim());
        }

        return doc;
    }

    private static void ValidateEmbedding(float[] vector)
    {
        if (vector.Length != VectorDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding has {vector.Length} dimensions; this container expects {VectorDimensions}. " +
                "Use a matching model or create a new Cosmos container with the correct vectorEmbeddingPolicy dimensions.");
        }

        for (var i = 0; i < vector.Length; i++)
        {
            var v = vector[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                throw new InvalidOperationException(
                    $"Embedding contains invalid values (NaN/Infinity) at index {i}; skip or fix the source text.");
            }
        }
    }

    private static string SanitizeCosmosId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Document id cannot be null or whitespace.");
        }

        var trimmed = id.Trim();
        Span<char> buffer = stackalloc char[trimmed.Length];
        var len = 0;
        foreach (var ch in trimmed)
        {
            if (ch is '/' or '\\' or '?' or '#')
            {
                buffer[len++] = '-';
            }
            else
            {
                buffer[len++] = ch;
            }
        }

        var result = new string(buffer[..len]);
        if (result.Length > 1023)
        {
            result = result[..1023];
        }

        return result;
    }

    private static string SanitizeFreeText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static void ThrowCosmosWriteException(CosmosException ex, int vectorLength)
    {
        throw new InvalidOperationException(
            "Cosmos DB rejected the document (400). Common causes: " +
            "(1) item `id` must match partition key and use only allowed characters; " +
            $"(2) `vector` length must be {VectorDimensions} for this container (got {vectorLength}); " +
            "(3) container vector policy does not match the account or was created with different dimensions — use a new container name and re-run. " +
            $"Cosmos message: {ex.Message}",
            ex);
    }

    private sealed class VectorSearchRow
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("parent_post_id")]
        public string? ParentPostId { get; set; }

        [JsonPropertyName("dist")]
        public double Dist { get; set; }
    }
}
