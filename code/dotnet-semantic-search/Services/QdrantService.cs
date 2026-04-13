using System.Net.Http.Json;
using System.Text.Json;
using MicrosoftExtensionsAiSample.Models;

namespace MicrosoftExtensionsAiSample.Services;

public class QdrantService
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _collection;
    private readonly int _dimensions;

    public QdrantService(string endpoint = "http://localhost:6333", string collection = "blog_posts", int dimensions = 768)
    {
        _endpoint = endpoint.TrimEnd('/');
        _collection = collection;
        _dimensions = dimensions;
        _http = new HttpClient();
    }

    public async Task InitializeAsync()
    {
        var url = $"{_endpoint}/collections/{_collection}";
        var payload = new
        {
            vectors = new { size = _dimensions, distance = "Cosine" }
        };
        var resp = await _http.PutAsJsonAsync(url, payload);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Collection already exists; continue without error
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task ClearAllDataAsync()
    {
        var url = $"{_endpoint}/collections/{_collection}/points/delete";
        var payload = new { filter = new { @operator = "should", should = Array.Empty<object>() }, points = (int[]?)null, @delete = new { @filter = new { } } };
        // Use a truncate-style delete by deleting all points
        var resp = await _http.PostAsJsonAsync(url, new { @filter = new { } });
        // If collection empty, ignore errors
        _ = resp;
    }

    public async Task<BlogPost> SaveBlogPostAsync(BlogPost blogPost)
    {
        var url = $"{_endpoint}/collections/{_collection}/points";
        var point = new
        {
            points = new[]
            {
                new {
                    id = blogPost.Id,
                    vector = blogPost.Vector,
                    payload = new
                    {
                        title = blogPost.Title,
                        url = blogPost.Url,
                        parent_post_id = blogPost.Id,
                        chunk_index = 0
                    }
                }
            }
        };
        var resp = await _http.PutAsJsonAsync(url, point);
        resp.EnsureSuccessStatusCode();
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

        var urlUpsert = $"{_endpoint}/collections/{_collection}/points";
        var points = chunks.Select(c => new
        {
            id = Guid.NewGuid().ToString(),
            vector = c.vector,
            payload = new
            {
                title,
                url,
                parent_post_id = parentPostId,
                chunk_index = c.chunkIndex
            }
        }).ToArray();

        var resp = await _http.PutAsJsonAsync(urlUpsert, new { points });
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<BlogPost>> SearchSimilarBlogPostsAsync(float[] queryVector, int maxResults = 5)
    {
        var url = $"{_endpoint}/collections/{_collection}/points/search";
        var payload = new
        {
            vector = queryVector,
            limit = Math.Max(maxResults * 6, 24),
            with_payload = true,
            with_vector = false
        };
        var resp = await _http.PostAsJsonAsync(url, payload);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var bestByParent = new Dictionary<string, (BlogPost post, float score)>(StringComparer.Ordinal);

        foreach (var hit in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var payloadEl = hit.GetProperty("payload");
            var title = payloadEl.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var postUrl = payloadEl.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var parentId = payloadEl.TryGetProperty("parent_post_id", out var p)
                ? p.GetString()
                : null;
            var pointId = hit.GetProperty("id").ToString();
            var groupKey = !string.IsNullOrEmpty(parentId) ? parentId : pointId;
            var score = hit.TryGetProperty("score", out var s) ? s.GetSingle() : 0f;

            var blog = new BlogPost
            {
                Id = !string.IsNullOrEmpty(parentId) ? parentId : pointId,
                Title = title,
                Url = postUrl
            };

            if (!bestByParent.TryGetValue(groupKey, out var existing) || score > existing.score)
            {
                bestByParent[groupKey] = (blog, score);
            }
        }

        return bestByParent.Values
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => x.post)
            .ToList();
    }
}
