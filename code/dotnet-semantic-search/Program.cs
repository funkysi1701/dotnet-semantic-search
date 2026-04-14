using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using MicrosoftExtensionsAiSample.Utils;
using MicrosoftExtensionsAiSample.Services;
using MicrosoftExtensionsAiSample.Models;

// Order matters: environment variables first, then user secrets, so local secrets override
// empty or placeholder machine env (e.g. Cosmos__ConnectionString=). CI agents without a
// secrets store still get values from environment only.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(
        $"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json",
        optional: true,
        reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddUserSecrets(typeof(CosmosDbService).Assembly)
    .Build();

IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null;

// Initialize Azure Cosmos DB (NoSQL + vector search)
CosmosDbService vectorService;
try
{
    vectorService = new CosmosDbService(configuration);
    await vectorService.InitializeAsync();
    Console.WriteLine("✅ Cosmos DB container ready for vector search");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to initialize Cosmos DB: {ex.Message}");
    Console.WriteLine("Use (from code/dotnet-semantic-search): dotnet user-secrets set \"Cosmos:ConnectionString\" \"AccountEndpoint=...;AccountKey=...;\"");
    Console.WriteLine("Verify: dotnet user-secrets list --project dotnet-semantic-search.csproj");
    Console.WriteLine("Or set COSMOS_CONNECTION_STRING / COSMOS_ENDPOINT + COSMOS_KEY in the environment.");
    Console.WriteLine("If secrets exist but are ignored, remove empty Cosmos__ConnectionString / COSMOS_* from machine or user environment.");
    Console.WriteLine("Enable vector search on the account (EnableNoSQLVectorSearch) if queries fail.");
    return;
}

try
{
    embeddingGenerator = OpenAIEmbeddingGeneratorFactory.Create(configuration);
    Console.WriteLine("✅ OpenAI embedding client ready (text-embedding-3-small @ 768 dims by default)");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to configure OpenAI embeddings: {ex.Message}");
    Console.WriteLine("Set OPENAI_API_KEY or OpenAI:ApiKey (user secrets: dotnet user-secrets set \"OpenAI:ApiKey\" \"...\" --project dotnet-semantic-search.csproj).");
    return;
}

ConsoleHelper.ShowHeader();

var maxEmbeddingConcurrency = GetMaxEmbeddingConcurrency(configuration);

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
            await ProcessBlogsAsync(embeddingGenerator, vectorService, maxEmbeddingConcurrency);
            break;

        case "2":
            await SearchBlogsAsync(embeddingGenerator, vectorService);
            break;

        case "3":
            Console.WriteLine("👋 Goodbye!");
            vectorService.Dispose();
            return;

        default:
            Console.WriteLine("❌ Invalid option. Please try again.");
            break;
    }

    PauseIfInteractive("\nPress any key to continue...");
}

static async Task ProcessBlogsAsync(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CosmosDbService vectorService,
    int maxEmbeddingConcurrency)
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
                    var indexedVectors = await EmbedChunksBoundedAsync(
                        embeddingGenerator,
                        chunks,
                        maxEmbeddingConcurrency);

                    await vectorService.SaveBlogPostChunksAsync(
                        blogPost.Id,
                        blogPost.Title,
                        blogPost.Url,
                        blogPost.ImageUrl,
                        blogPost.PublishedAt,
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

static int GetMaxEmbeddingConcurrency(IConfiguration configuration)
{
    const int defaultValue = 4;
    var s = configuration["OpenAI:MaxEmbeddingConcurrency"];
    if (string.IsNullOrWhiteSpace(s) || !int.TryParse(s, out var v))
    {
        return defaultValue;
    }

    return Math.Clamp(v, 1, 32);
}

static async Task<List<(int chunkIndex, float[] vector)>> EmbedChunksBoundedAsync(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IReadOnlyList<string> chunks,
    int maxConcurrency)
{
    maxConcurrency = Math.Clamp(maxConcurrency, 1, 32);
    using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

    async Task<(int chunkIndex, float[] vector)> EmbedAsync(int index)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var embedding = await embeddingGenerator.GenerateAsync(chunks[index]).ConfigureAwait(false);
            return (index, embedding.Vector.ToArray());
        }
        finally
        {
            semaphore.Release();
        }
    }

    var tasks = new Task<(int chunkIndex, float[] vector)>[chunks.Count];
    for (var i = 0; i < chunks.Count; i++)
    {
        tasks[i] = EmbedAsync(i);
    }

    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
    return results.ToList();
}

static async Task SearchBlogsAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, CosmosDbService vectorService)
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
