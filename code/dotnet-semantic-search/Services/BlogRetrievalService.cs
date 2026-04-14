using System.Text.RegularExpressions;
using System.Xml.Linq;
using MicrosoftExtensionsAiSample.Models;

namespace MicrosoftExtensionsAiSample.Services;

public static class BlogRetrievalService
{
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

                    // Try to get full content first, fallback to description if not available
                    string content = item.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/"))?.Value
                                    ?? item.Element("description")?.Value
                                    ?? "No Content";

                    string urlItem = item.Element("link")?.Value ?? "";
                    var categories = item.Elements("category").Select(c => c.Value).ToList();

                    var blogPost = new BlogPost
                    {
                        Title = title,
                        Content = content,
                        Url = urlItem,
                        Categories = categories
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

    // Method to clean up invalid XML characters
    private static string CleanInvalidXmlCharacters(string xmlContent)
    {
        // Replace invalid characters (specifically 0x1E) with an empty string
        string cleanedXml = Regex.Replace(xmlContent, @"\x1E", "");

        return cleanedXml;
    }
}
