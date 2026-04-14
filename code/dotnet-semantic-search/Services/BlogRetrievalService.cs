using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MicrosoftExtensionsAiSample.Models;

namespace MicrosoftExtensionsAiSample.Services;

public static class BlogRetrievalService
{
    /// <summary>Media RSS (mrss) namespace — <c>media:content</c> featured images.</summary>
    private static readonly XNamespace Mrss = "http://search.yahoo.com/mrss/";
    public static async Task<List<BlogPost>> GetAllBlogPostsAsync()
    {
        var blogPosts = new List<BlogPost>();

        // Define the base URL of the RSS feed
        string baseUrl = "https://www.funkysi1701.com/index.xml";

        // Initialize HttpClient to make HTTP requests
        using (HttpClient client = new HttpClient())
        {
            // Construct the URL for the current page
            string url = baseUrl;

            try
            {
                Console.WriteLine($"📡 Fetching page 1...");

                using var response = await client.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine("Page 1 not found (404). No feed to retrieve.");
                    Console.WriteLine($"✅ Retrieved {blogPosts.Count} blog posts total.");
                    return blogPosts;
                }

                response.EnsureSuccessStatusCode();

                // Read the content as a string
                string feedContent = await response.Content.ReadAsStringAsync();

                // Clean the XML content by removing invalid characters
                feedContent = CleanInvalidXmlCharacters(feedContent);

                // Parse the cleaned RSS feed content using XDocument
                XDocument feedXml = XDocument.Parse(feedContent);

                // Select all <item> elements in the RSS feed
                var items = feedXml.Descendants("item").ToList();

                // Loop through each <item> and extract the title and other information
                foreach (var item in items)
                {
                    // Extract title, content, category, tags, and URL from each <item>
                    string title = item.Element("title")?.Value ?? "No Title";

                    // Full post body (Hugo often omits content:encoded; summary images may live only in <description>.)
                    var encoded = item.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/"))?.Value;
                    var description = item.Element("description")?.Value;
                    string content = encoded ?? description ?? "No Content";

                    string urlItem = item.Element("link")?.Value ?? "";
                    var categories = item.Elements("category").Select(c => c.Value).ToList();

                    // Stable id for Cosmos parent_post_id (default BlogPost.Id would be a new GUID every fetch).
                    var stableId = item.Element("guid")?.Value?.Trim();
                    if (string.IsNullOrEmpty(stableId))
                    {
                        stableId = urlItem.Trim();
                    }

                    if (string.IsNullOrEmpty(stableId))
                    {
                        stableId = Guid.NewGuid().ToString();
                    }

                    var imageUrl = TryExtractMrssImageUrl(item)
                        ?? TryExtractFirstImageUrlFromHtml(content);
                    if (imageUrl is null && encoded is not null && description is not null)
                    {
                        imageUrl = TryExtractFirstImageUrlFromHtml(encoded)
                            ?? TryExtractFirstImageUrlFromHtml(description);
                    }

                    var blogPost = new BlogPost
                    {
                        Id = stableId,
                        Title = title,
                        Content = content,
                        Url = urlItem,
                        Categories = categories,
                        ImageUrl = imageUrl,
                        PublishedAt = TryParseRssPubDate(item.Element("pubDate")?.Value)
                    };

                    blogPost.GenerateCombinedText();
                    blogPosts.Add(blogPost);

                    Console.WriteLine($"  📝 {title}");
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions (e.g., network errors, XML parsing errors)
                Console.WriteLine($"❌ Error fetching or parsing page 1: {ex.Message}");
            }

            Console.WriteLine($"✅ Retrieved {blogPosts.Count} blog posts total.");
        }

        return blogPosts;
    }

    /// <summary>RSS 2.0 <c>pubDate</c> (RFC 822 / similar).</summary>
    private static DateTimeOffset? TryParseRssPubDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var dto))
        {
            return dto;
        }

        return null;
    }

    /// <summary>First <c>media:content</c> with <c>medium="image"</c> (MRSS) on the RSS item.</summary>
    private static string? TryExtractMrssImageUrl(XElement item)
    {
        foreach (var el in item.Elements(Mrss + "content"))
        {
            var medium = (string?)el.Attribute("medium");
            if (!string.Equals(medium, "image", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = el.Attribute("url")?.Value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            raw = WebUtility.HtmlDecode(raw);
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.ToString();
            }
        }

        return null;
    }

    /// <summary>First absolute http(s) URL from an <c>img</c> <c>src</c> in HTML (description / content fallback).</summary>
    private static string? TryExtractFirstImageUrlFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(html);
        foreach (Match m in ImgSrcInTag.Matches(decoded))
        {
            var raw = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim());
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.ToString();
            }
        }

        return null;
    }

    private static readonly Regex ImgSrcInTag = new(
        """<img\b[^>]*\bsrc\s*=\s*(['"])(?<url>.*?)\1""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // Method to clean up invalid XML characters
    private static string CleanInvalidXmlCharacters(string xmlContent)
    {
        // Replace invalid characters (specifically 0x1E) with an empty string
        string cleanedXml = Regex.Replace(xmlContent, @"\x1E", "");

        return cleanedXml;
    }
}
