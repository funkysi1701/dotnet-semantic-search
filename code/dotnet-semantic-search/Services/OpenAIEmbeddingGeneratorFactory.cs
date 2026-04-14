using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using MicrosoftExtensionsAiSample.Utils;
using OpenAI.Embeddings;

namespace MicrosoftExtensionsAiSample.Services;

/// <summary>
/// Builds an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for OpenAI embeddings.
/// API key: <c>OpenAI:ApiKey</c> or <c>OPENAI_API_KEY</c>. Defaults to <c>text-embedding-3-small</c> at 768 dimensions to match the Cosmos vector index.
/// </summary>
public static class OpenAIEmbeddingGeneratorFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var apiKey = FirstNonEmpty(configuration, "OPENAI_API_KEY", "OpenAI:ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Configure OpenAI: set user secret OpenAI:ApiKey, or environment variable OPENAI_API_KEY.");
        }

        var model = FirstNonEmpty(configuration, "OpenAI:EmbeddingModel")
            ?? Statics.TextEmbedding3SmallModelName;

        var dimensions = CosmosDbService.VectorDimensions;
        var dimStr = configuration["OpenAI:EmbeddingDimensions"];
        if (!string.IsNullOrWhiteSpace(dimStr) && int.TryParse(dimStr, out var parsed) && parsed > 0)
        {
            dimensions = parsed;
        }

        var client = new EmbeddingClient(model, apiKey);
        return client.AsIEmbeddingGenerator(dimensions);
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
}
