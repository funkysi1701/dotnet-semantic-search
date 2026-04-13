using Microsoft.Extensions.AI;
using MicrosoftExtensionsAiSample.Utils;
using MicrosoftExtensionsAiSample.Services;
using MicrosoftExtensionsAiSample.Models;

IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null;

// Initialize Qdrant service
QdrantService vectorService;
try
{
    vectorService = new QdrantService();
    await vectorService.InitializeAsync();
    Console.WriteLine("✅ Qdrant ready at http://localhost:6333");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to connect to Qdrant: {ex.Message}");
    Console.WriteLine("Make sure Qdrant is running: docker run -p 6333:6333 qdrant/qdrant");
    return;
}

// Initialize Ollama embedding generator with hardcoded model
try
{
    embeddingGenerator = new OllamaEmbeddingGenerator(
        new Uri("http://localhost:11434/"), "nomic-embed-text");
    Console.WriteLine("✅ Connected to Ollama with nomic-embed-text model");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to connect to Ollama: {ex.Message}");
    Console.WriteLine("Make sure Ollama is running on port 11434 with nomic-embed-text model");
    return;
}

ConsoleHelper.ShowHeader();

// Main menu loop
while (true)
{
    Console.WriteLine("🤖 AI Embeddings Menu");
    Console.WriteLine("1. 📚 Process Blog Posts (Retrieve and Generate Embeddings)");
    Console.WriteLine("2. 🔍 Search Blog Posts");
    Console.WriteLine("3. 🚪 Exit");

    Console.Write("\nSelect an option (1-3): ");
    var choice = Console.ReadLine();

    switch (choice)
    {
        case "1":
            await ProcessBlogsAsync(embeddingGenerator, vectorService);
            break;

        case "2":
            await SearchBlogsAsync(embeddingGenerator, vectorService);
            break;

        case "3":
            Console.WriteLine("👋 Goodbye!");
            return;

        default:
            Console.WriteLine("❌ Invalid option. Please try again.");
            break;
    }

    PauseIfInteractive("\nPress any key to continue...");
}

static async Task ProcessBlogsAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, QdrantService vectorService)
{
    try
    {
        Console.WriteLine("📚 Processing Blog Posts...\n");

        Console.Write("🗑️  Do you want to clear existing blog posts first? (y/N): ");
        var clearResponse = Console.ReadLine()?.ToLower();
        if (clearResponse == "y" || clearResponse == "yes")
        {
            Console.WriteLine("🧹 Clearing existing blog posts...");
            await vectorService.ClearAllDataAsync();
            Console.WriteLine("✅ All existing data cleared.");
        }

        Console.WriteLine("📡 Retrieving blog posts from RSS...");
        var blogPosts = await BlogRetrievalService.GetAllBlogPostsAsync();
        Console.WriteLine($"📰 Retrieved {blogPosts.Count} blog posts");

        var processedCount = 0;
        foreach (var blogPost in blogPosts)
        {
            try
            {
                var chunks = TextChunker.SplitIntoChunks(blogPost.CombinedText);
                if (chunks.Count == 0)
                {
                    continue;
                }

                if (chunks.Count == 1)
                {
                    var embedding = await embeddingGenerator.GenerateAsync(chunks[0]);
                    blogPost.Vector = embedding.Vector.ToArray();
                    await vectorService.SaveBlogPostAsync(blogPost);
                }
                else
                {
                    var indexedVectors = new List<(int chunkIndex, float[] vector)>();
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var embedding = await embeddingGenerator.GenerateAsync(chunks[i]);
                        indexedVectors.Add((i, embedding.Vector.ToArray()));
                    }

                    await vectorService.SaveBlogPostChunksAsync(
                        blogPost.Id,
                        blogPost.Title,
                        blogPost.Url,
                        indexedVectors);
                }

                processedCount++;

                if (processedCount % 10 == 0 || processedCount == blogPosts.Count)
                {
                    Console.WriteLine($"⚡ Processed {processedCount}/{blogPosts.Count} blog posts");
                    Console.WriteLine("   ⏰ " + DateTime.Now.ToString("HH:mm:ss"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing blog post '{blogPost.Title}': {ex.Message}");
            }
        }

        Console.WriteLine($"\n✅ Successfully processed {processedCount} out of {blogPosts.Count} blog posts");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error in ProcessBlogsAsync: {ex.Message}");
    }
}

static async Task SearchBlogsAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, QdrantService vectorService)
{
    try
    {
        Console.WriteLine("🔍 Search Blog Posts\n");

        Console.Write("Enter your search query: ");
        var query = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(query))
        {
            Console.WriteLine("❌ Search query cannot be empty.");
            return;
        }

        Console.WriteLine($"\n🔍 Searching for: '{query}'...");

        var queryEmbedding = await embeddingGenerator.GenerateAsync(query);

        var results = await vectorService.SearchSimilarBlogPostsAsync(queryEmbedding.Vector.ToArray(), maxResults: 5);

        if (results.Any())
        {
            Console.WriteLine($"\n📋 Found {results.Count} relevant blog posts:\n");

            for (int i = 0; i < results.Count; i++)
            {
                var blogPost = results[i];
                Console.WriteLine($"{i + 1}. {blogPost.Title}");
                Console.WriteLine($"   🔗 {blogPost.Url}");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("❌ No relevant blog posts found.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error in SearchBlogsAsync: {ex.Message}");
    }
}

static void PauseIfInteractive(string message)
{
    try
    {
        Console.WriteLine(message);
        if (!Console.IsInputRedirected)
        {
            Console.ReadKey(intercept: true);
        }
        // If input is redirected or no console is available, skip waiting.
    }
    catch (InvalidOperationException)
    {
        // No interactive console attached; do not attempt to read a key.
    }
}
