using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using MicrosoftExtensionsAiSample.Utils;
using OpenAI.Embeddings;

namespace MicrosoftExtensionsAiSample.Services;

/// <summary>
/// Builds an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for OpenAI embeddings.
/// API key: <c>OpenAI:ApiKey</c> or <c>OPENAI_API_KEY</c>. Defaults to <c>text-embedding-3-small</c> at <see cref="CosmosDbService.VectorDimensions"/> to match the Cosmos vector index.
/// Optional <c>OpenAI:EmbeddingDimensions</c> must equal that value if set (otherwise startup fails).
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
        if (!string.IsNullOrWhiteSpace(dimStr))
        {
            if (!int.TryParse(dimStr, out var parsed) || parsed <= 0)
            {
                throw new InvalidOperationException(
                    $"OpenAI:EmbeddingDimensions value \"{dimStr}\" is not a positive integer.");
            }

            if (parsed != dimensions)
            {
                throw new InvalidOperationException(
                    $"OpenAI:EmbeddingDimensions is {parsed} but this app uses Cosmos vector index dimension {dimensions} " +
                    $"(see {nameof(CosmosDbService)}.{nameof(CosmosDbService.VectorDimensions)}). " +
                    "Remove OpenAI:EmbeddingDimensions, set it to match, or change the Cosmos container policy and the constant together.");
            }
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
