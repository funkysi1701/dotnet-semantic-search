using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
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

    /// <summary>Stored on auxiliary title-only rows so they can be distinguished from body chunks.</summary>
    public const int TitleAuxiliaryChunkIndex = -1;

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
            blogPost.PublishedAt,
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
        DateTimeOffset? publishedAt,
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
                publishedAt,
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

    /// <summary>
    /// Extra vector row embedding <paramref name="titleVector"/> built from title (+ categories) only.
    /// Same <paramref name="parentPostId"/> as the post; search merge keeps the best (minimum) distance across all chunks including this one.
    /// </summary>
    public async Task SaveAuxiliaryTitleVectorAsync(
        string parentPostId,
        string title,
        string url,
        string? imageUrl,
        DateTimeOffset? publishedAt,
        float[] titleVector)
    {
        ValidateEmbedding(titleVector);
        var id = SanitizeCosmosId(Guid.NewGuid().ToString());
        var payload = BuildItemPayload(
            id,
            parentPostId: SanitizeCosmosId(parentPostId),
            TitleAuxiliaryChunkIndex,
            SanitizeFreeText(title),
            SanitizeFreeText(url),
            imageUrl,
            publishedAt,
            titleVector);

        try
        {
            await Container.UpsertItemAsync(payload, new PartitionKey(id));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            ThrowCosmosWriteException(ex, titleVector.Length);
        }
    }

    /// <param name="lexicalQuery">When non-empty, merges vector order with title/URL token match ranks via RRF (reciprocal rank fusion).</param>
    public async Task<List<BlogPost>> SearchSimilarBlogPostsAsync(
        float[] queryVector,
        int maxResults = 5,
        string? lexicalQuery = null)
    {
        if (queryVector.Length != VectorDimensions)
        {
            throw new ArgumentException($"Expected {VectorDimensions} dimensions, got {queryVector.Length}.", nameof(queryVector));
        }

        // Wider candidate pool before per-post dedupe: small TOP drops recall when many chunks
        // score similarly (e.g. many posts about the same topic). Trade-off: more RU / latency.
        // With lexical hybrid we widen further; title hits are also merged in below so strong
        // title matches are not dropped just because every body chunk missed the vector TOP window.
        var hasLexical = !string.IsNullOrWhiteSpace(lexicalQuery);
        var top = hasLexical
            ? Math.Max(maxResults * 50, 500)
            : Math.Max(maxResults * 25, 200);
        var sql =
            $"SELECT TOP {top} c.id, c.title, c.url, c.image_url, c.published_at, c.parent_post_id, VectorDistance(c.vector, @embedding) AS dist " +
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

                    var publishedAt = TryParsePublishedAt(row.PublishedAt);
                    if (publishedAt is null && bestByParent.TryGetValue(groupKey, out var prevPub))
                    {
                        publishedAt = prevPub.post.PublishedAt;
                    }

                    var blog = new BlogPost
                    {
                        Id = !string.IsNullOrEmpty(row.ParentPostId) ? row.ParentPostId : row.Id,
                        Title = row.Title ?? string.Empty,
                        Url = row.Url ?? string.Empty,
                        ImageUrl = imageUrl,
                        PublishedAt = publishedAt
                    };
                    bestByParent[groupKey] = (blog, row.Dist);
                }
                else if (!string.IsNullOrWhiteSpace(row.ImageUrl) && string.IsNullOrWhiteSpace(existing.post.ImageUrl))
                {
                    existing.post.ImageUrl = row.ImageUrl;
                }
                else if (TryParsePublishedAt(row.PublishedAt) is { } rowPub && existing.post.PublishedAt is null)
                {
                    existing.post.PublishedAt = rowPub;
                }
            }
        }

        // Same permalink can appear under different parent_post_id values (e.g. legacy rows from
        // random GUID ids per RSS fetch). Keep the single best-scoring row per canonical URL.
        var ranked = bestByParent.Values.OrderBy(x => x.dist).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(BlogPost post, double dist)>();
        foreach (var entry in ranked)
        {
            var key = PostEquivalenceKey(entry.post);
            if (!seen.Add(key))
            {
                continue;
            }

            candidates.Add((entry.post, entry.dist));
        }

        if (maxResults <= 0)
        {
            return [];
        }

        if (hasLexical)
        {
            await MergeLexicalTitleHitsIntoCandidatesAsync(candidates, lexicalQuery!.Trim());
        }

        if (string.IsNullOrWhiteSpace(lexicalQuery))
        {
            return candidates.Take(maxResults).Select(c => c.post).ToList();
        }

        return RrfHybridRank(candidates, lexicalQuery.Trim(), maxResults);
    }

    /// <summary>Reciprocal rank fusion over vector distance order and lexical (title + URL) relevance.</summary>
    private static List<BlogPost> RrfHybridRank(
        List<(BlogPost post, double dist)> candidates,
        string lexicalQuery,
        int maxResults)
    {
        const float rrfK = 60f;
        // Lexical list contributes more than vector so obvious title matches are not buried.
        const float lexicalRrfWeight = 2.25f;
        if (candidates.Count == 0)
        {
            return [];
        }

        var tokens = TokenizeLexical(lexicalQuery);
        var phrase = CollapseWhitespaceLower(lexicalQuery);

        var vectorRankByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            vectorRankByKey[PostEquivalenceKey(candidates[i].post)] = i + 1;
        }

        var lexicalOrdered = candidates
            .Select(c => (c.post, c.dist, score: LexicalTitleUrlScore(c.post.Title, c.post.Url, tokens, phrase)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.dist)
            .ToList();

        var lexicalRankByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lexicalOrdered.Count; i++)
        {
            lexicalRankByKey[PostEquivalenceKey(lexicalOrdered[i].post)] = i + 1;
        }

        return candidates
            .Select(c =>
            {
                var key = PostEquivalenceKey(c.post);
                var vr = vectorRankByKey[key];
                var lr = lexicalRankByKey[key];
                var rrf = (1f / (rrfK + vr)) + lexicalRrfWeight * (1f / (rrfK + lr));
                return (c.post, rrf);
            })
            .OrderByDescending(x => x.rrf)
            .Take(maxResults)
            .Select(x => x.post)
            .ToList();
    }

    private static readonly Regex LexicalTokenSplitter = new(@"[\s\p{P}]+", RegexOptions.Compiled);

    private static List<string> TokenizeLexical(string query)
    {
        var s = query.Trim().ToLowerInvariant();
        if (s.Length == 0)
        {
            return [];
        }

        return LexicalTokenSplitter
            .Split(s)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string CollapseWhitespaceLower(string query)
    {
        return Regex.Replace(query.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    private static int LexicalTitleUrlScore(string title, string url, List<string> tokens, string phraseLower)
    {
        var tl = title.ToLowerInvariant();
        var ul = url.ToLowerInvariant();
        var score = 0;
        foreach (var t in tokens)
        {
            if (tl.Contains(t, StringComparison.Ordinal))
            {
                score += 3;
            }
            else if (ul.Contains(t, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        if (phraseLower.Length >= 3 && tl.Contains(phraseLower, StringComparison.Ordinal))
        {
            score += 28;
        }

        return score;
    }

    /// <summary>
    /// Adds posts whose titles match the query via CONTAINS so they participate in RRF even when
    /// no chunk landed in the vector TOP window (common for exact/near-exact title queries).
    /// </summary>
    private async Task MergeLexicalTitleHitsIntoCandidatesAsync(
        List<(BlogPost post, double dist)> candidates,
        string lexicalQueryTrim)
    {
        if (!TryBuildTitleLexicalWhereClause(lexicalQueryTrim, out var whereSql, out var parameters))
        {
            return;
        }

        var sql =
            "SELECT c.id, c.title, c.url, c.image_url, c.published_at, c.parent_post_id FROM c WHERE " +
            whereSql +
            " AND (NOT IS_DEFINED(c.chunk_index) OR c.chunk_index = 0 OR c.chunk_index = " +
            TitleAuxiliaryChunkIndex +
            ")";

        var query = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            query.WithParameter(name, value);
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            seenKeys.Add(PostEquivalenceKey(c.post));
        }

        var maxDist = candidates.Count > 0 ? candidates.Max(x => x.dist) : 0.0;
        var injectDist = Math.Max(maxDist + 0.02, 1.2);

        using var feed = Container.GetItemQueryIterator<TitleLexicalRow>(query);
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            foreach (var row in page)
            {
                var groupKey = !string.IsNullOrEmpty(row.ParentPostId) ? row.ParentPostId : row.Id;
                var blog = new BlogPost
                {
                    Id = groupKey,
                    Title = row.Title ?? string.Empty,
                    Url = row.Url ?? string.Empty,
                    ImageUrl = string.IsNullOrWhiteSpace(row.ImageUrl) ? null : row.ImageUrl.Trim(),
                    PublishedAt = TryParsePublishedAt(row.PublishedAt)
                };

                var key = PostEquivalenceKey(blog);
                if (!seenKeys.Add(key))
                {
                    continue;
                }

                candidates.Add((blog, injectDist));
            }
        }
    }

    /// <returns>false when the query is too vague to run a cheap title scan (no tokens / short phrase).</returns>
    private static bool TryBuildTitleLexicalWhereClause(
        string lexicalQueryTrim,
        out string whereSql,
        out List<(string Name, object Value)> parameters)
    {
        parameters = [];
        var phrase = Regex.Replace(lexicalQueryTrim.Trim(), @"\s+", " ");
        var tokens = TokenizeLexical(lexicalQueryTrim);

        if (phrase.Length >= 4)
        {
            whereSql = "CONTAINS(c.title, @lexPhrase, true)";
            parameters.Add(("@lexPhrase", phrase));
            return true;
        }

        if (tokens.Count == 0)
        {
            whereSql = string.Empty;
            return false;
        }

        var parts = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var p = "@lexTok" + i;
            parts.Add($"CONTAINS(c.title, {p}, true)");
            parameters.Add((p, tokens[i]));
        }

        whereSql = string.Join(" AND ", parts);
        return true;
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
        DateTimeOffset? publishedAt,
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

        if (publishedAt.HasValue)
        {
            doc["published_at"] = publishedAt.Value.ToString("o", CultureInfo.InvariantCulture);
        }

        return doc;
    }

    private static DateTimeOffset? TryParsePublishedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto))
        {
            return dto;
        }

        return null;
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

    /// <summary>Row shape for title CONTAINS scans (no vector / distance).</summary>
    private sealed class TitleLexicalRow
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("image_url")]
        public string? ImageUrl { get; set; }

        [JsonProperty("parent_post_id")]
        public string? ParentPostId { get; set; }

        [JsonProperty("published_at")]
        public string? PublishedAt { get; set; }
    }

    /// <summary>Cosmos query row shape. The SDK deserializes with Newtonsoft.Json — use <see cref="JsonPropertyAttribute"/>, not STJ <c>JsonPropertyName</c>.</summary>
    private sealed class VectorSearchRow
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("image_url")]
        public string? ImageUrl { get; set; }

        [JsonProperty("parent_post_id")]
        public string? ParentPostId { get; set; }

        [JsonProperty("published_at")]
        public string? PublishedAt { get; set; }

        [JsonProperty("dist")]
        public double Dist { get; set; }
    }
}
