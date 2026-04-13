using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using MicrosoftExtensionsAiSample.Models;

namespace MicrosoftExtensionsAiSample.Services;

/// <summary>
/// Azure Cosmos DB for NoSQL with vector indexing (nomic-embed-text: 768 dims, cosine).
/// Requires account capability <c>EnableNoSQLVectorSearch</c> and env <see cref="ResolveCredentials"/>.
/// </summary>
public sealed class CosmosDbService : IDisposable
{
    public const int VectorDimensions = 768;

    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly string _containerName;
    private Container? _container;

    public CosmosDbService(
        string? endpoint = null,
        string? authKey = null,
        string? databaseName = null,
        string? containerName = null)
    {
        if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(authKey))
        {
            _client = new CosmosClient(endpoint, authKey, new CosmosClientOptions
            {
                ApplicationName = "dotnet-semantic-search"
            });
        }
        else
        {
            var (ep, key) = ResolveCredentials();
            _client = new CosmosClient(ep, key, new CosmosClientOptions
            {
                ApplicationName = "dotnet-semantic-search"
            });
        }

        _databaseName = databaseName
            ?? Environment.GetEnvironmentVariable("COSMOS_DATABASE")
            ?? "semantic-search-blog";
        _containerName = containerName
            ?? Environment.GetEnvironmentVariable("COSMOS_CONTAINER")
            ?? "blog-embeddings";
    }

    private static (string Endpoint, string Key) ResolveCredentials()
    {
        var conn = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(conn))
        {
            string? ep = null, ky = null;
            foreach (var segment in conn.Split(';', StringSplitOptions.RemoveEmptyEntries))
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
                return (ep.TrimEnd('/'), ky);
            }
        }

        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("AZURE_COSMOS_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("COSMOS_KEY")
            ?? Environment.GetEnvironmentVariable("COSMOS_ACCOUNT_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Configure Azure Cosmos DB: set COSMOS_CONNECTION_STRING, or COSMOS_ENDPOINT and COSMOS_KEY.");
        }

        return (endpoint.TrimEnd('/'), key);
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
            $"SELECT TOP {top} c.id, c.title, c.url, c.parent_post_id, VectorDistance(c.vector, @embedding) AS dist " +
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
                var blog = new BlogPost
                {
                    Id = !string.IsNullOrEmpty(row.ParentPostId) ? row.ParentPostId : row.Id,
                    Title = row.Title ?? string.Empty,
                    Url = row.Url ?? string.Empty
                };

                if (!bestByParent.TryGetValue(groupKey, out var existing) || row.Dist < existing.dist)
                {
                    bestByParent[groupKey] = (blog, row.Dist);
                }
            }
        }

        return bestByParent.Values
            .OrderBy(x => x.dist)
            .Take(maxResults)
            .Select(x => x.post)
            .ToList();
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
        float[] vector) =>
        new()
        {
            ["id"] = id,
            ["parent_post_id"] = parentPostId,
            ["chunk_index"] = chunkIndex,
            ["title"] = title,
            ["url"] = url,
            ["vector"] = vector
        };

    private static void ValidateEmbedding(float[] vector)
    {
        if (vector.Length != VectorDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding has {vector.Length} dimensions; this container expects {VectorDimensions} (nomic-embed-text). " +
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

        [JsonPropertyName("parent_post_id")]
        public string? ParentPostId { get; set; }

        [JsonPropertyName("dist")]
        public double Dist { get; set; }
    }
}
