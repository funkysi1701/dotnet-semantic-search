# .NET Semantic Search Demo

A .NET application demonstrating semantic search capabilities using AI embeddings with **Azure Cosmos DB for NoSQL** (vector search) and the **OpenAI** API (`text-embedding-3-small` at 768 dimensions by default).

## Features

- 📚 **Blog Post Processing**: Retrieves blog posts from RSS feeds and generates embeddings
- 🔍 **Semantic Search**: Search through blog posts using natural language queries
- 🎯 **Vector Similarity**: Uses AI embeddings to find semantically similar content
- ☁️ **Cosmos DB**: Embeddings and metadata stored in Azure Cosmos DB for NoSQL with native vector indexing

## Architecture

- **Vector Database**: Azure Cosmos DB for NoSQL (`VectorDistance` queries, 768-d cosine)
- **AI Embeddings**: OpenAI `text-embedding-3-small` shortened to 768 dimensions (matches Cosmos vector index)
- **Framework**: .NET 9
- **AI Integration**: Microsoft.Extensions.AI

### Azure Static Web Apps and .NET version

The **SemanticSearch.Api** project is deployed as the **Azure Functions** API behind **Azure Static Web Apps** (`api_location` in the GitHub workflow). That path is built and run on runtimes **supported by Static Web Apps for Functions**. **.NET 10 is not supported** for SWA-managed Functions today—keep **`SemanticSearch.Api`** on **`net9.0`** and use the **.NET 9 SDK** locally and in CI (see `.github/workflows` **`setup-dotnet`** with **`dotnet-version: 9.x`**). Do not retarget the API to **`net10.0`** expecting it to deploy until Microsoft documents **.NET 10** support for Azure Static Web Apps Functions.

**Renovate** (see **`renovate.json`** at the repo root) limits relevant **NuGet** upgrades to versions **below 10.0.0** for Microsoft platform libraries (ASP.NET Core, Azure Functions worker, `System.*`, and most `Microsoft.Extensions.*`), so automated PRs do not move the stack to the **.NET 10** package wave. **`Microsoft.Extensions.AI`**, **`Microsoft.Extensions.AI.Abstractions`**, and **`Microsoft.Extensions.AI.OpenAI`** are excluded from that cap because they already use **10.x** versioning independently of the **.NET** runtime.

## Prerequisites

### Required
- An [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/how-to-create-account) account with **vector search** enabled ([EnableNoSQLVectorSearch](https://learn.microsoft.com/azure/cosmos-db/vector-search))
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (recommended for this repo so local builds match CI and SWA)

### Verify Prerequisites
```bash
# Check .NET
dotnet --version

# Should show .NET 9.x for this solution (see "Azure Static Web Apps and .NET version" above)
```

## Setup Instructions

### 1. Clone and Build the Project

```bash
# Clone the repository
git clone <repository-url>
cd dotnet-semantic-search/code

# Build the project
dotnet build
```

### 2. Set Up Azure Cosmos DB (vector store)

1. Create a Cosmos DB for NoSQL account and enable **Vector Search in Azure Cosmos DB for NoSQL** (portal **Features** or Azure CLI `EnableNoSQLVectorSearch`). Propagation can take several minutes.
2. Copy the **URI** and **PRIMARY KEY** (or a connection string).

3. **Store credentials securely (do not commit secrets to git).** The app loads configuration in this order: `appsettings.json` → `appsettings.{DOTNET_ENVIRONMENT}.json` → **environment variables** → **user secrets** (user secrets override the same key from environment, so a blank `Cosmos__ConnectionString` in your profile does not wipe a secret).

**Recommended for local development — user secrets** (run commands from the **`code/dotnet-semantic-search`** folder, or pass `--project` to that `.csproj` so they bind to the same `UserSecretsId` as the app):

```bash
cd dotnet-semantic-search
dotnet user-secrets set "Cosmos:ConnectionString" "AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>;"
```

From the repo root:

```bash
dotnet user-secrets set "Cosmos:ConnectionString" "AccountEndpoint=...;AccountKey=...;" --project code/dotnet-semantic-search/dotnet-semantic-search.csproj
dotnet user-secrets list --project code/dotnet-semantic-search/dotnet-semantic-search.csproj
```

Optional: override database or container name (defaults match `appsettings.json`):

```bash
dotnet user-secrets set "Cosmos:Database" "semantic-search-blog"
dotnet user-secrets set "Cosmos:Container" "blog-embeddings"
```

**Optional file-based overrides:** copy `appsettings.json` to `appsettings.Development.local.json`, add your `Cosmos` values, and keep that file untracked (it is listed in `.gitignore`). Do not put secrets in committed `appsettings*.json` files.

**CI / shells — environment variables** (PowerShell example):

```powershell
$env:COSMOS_CONNECTION_STRING = "AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>;"
# Optional overrides (defaults shown):
# $env:COSMOS_DATABASE = "semantic-search-blog"
# $env:COSMOS_CONTAINER = "blog-embeddings"
```

Alternatively use `COSMOS_ENDPOINT` and `COSMOS_KEY` instead of `COSMOS_CONNECTION_STRING`.

The app creates the database and container on first run (768-dimensional cosine vectors on `/vector`, partition key `/id`).

### 3. Set Up OpenAI (embeddings API)

1. Create an [OpenAI API key](https://platform.openai.com/api-keys) (billing enabled as required by your account).
2. Store it in **user secrets** (recommended locally) or environment variable **`OPENAI_API_KEY`**.

From **`code/dotnet-semantic-search`**:

```bash
cd dotnet-semantic-search
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
```

From the repo root:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project code/dotnet-semantic-search/dotnet-semantic-search.csproj
```

For **`SemanticSearch.Api`** only (Azure Functions worker), use the same key name on that project’s user secrets:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project code/SemanticSearch.Api/SemanticSearch.Api.csproj
```

**Azure Static Web Apps / Functions**: add application setting **`OpenAI__ApiKey`** (or **`OPENAI_API_KEY`**) in the portal; never commit keys to git.

Optional overrides (defaults match `appsettings.json`):

- **`OpenAI:EmbeddingModel`** — default `text-embedding-3-small`
- **`OpenAI:EmbeddingDimensions`** — default `768` (must match your Cosmos vector index; change only if you recreate the container with a different size)

### 4. Run the Application

```bash
# Navigate to the project directory
cd dotnet-semantic-search

# Run the application
dotnet run
```

## Usage

The application provides a simple menu interface:

### 1. 📚 Process Blog Posts
- Retrieves blog posts from configured RSS feeds
- Generates AI embeddings for each post
- Stores embeddings in Azure Cosmos DB
- **First time setup**: Choose this option to populate the database

### 2. 🔍 Search Blog Posts
- Enter natural language search queries
- Uses AI embeddings to find semantically similar content
- Returns ranked results with similarity scores
- **Example queries**: "API security", "React performance", "Azure deployment"

### 3. 🚪 Exit
- Cleanly exits the application

## Configuration

### RSS Feed Source
The application is configured to process blog posts from Trailhead Technology's RSS feed. You can modify the RSS source in the code if needed.

### Model Configuration
- **Embedding Model**: `text-embedding-3-small` (768 dimensions via API shortening)
- **Vector Dimensions**: 768 (Cosmos container policy)
- **Embedding Provider**: OpenAI (`Microsoft.Extensions.AI.OpenAI`)

## Troubleshooting

### Cosmos DB issues

- Confirm **vector search** is enabled on the account and wait for the capability to apply.
- Ensure credentials are available: `dotnet user-secrets list --project code/dotnet-semantic-search/dotnet-semantic-search.csproj`, or `COSMOS_CONNECTION_STRING` / `COSMOS_ENDPOINT` + `COSMOS_KEY` in the environment for the same process as `dotnet run`.
- If user secrets are set but the app still cannot connect, check for **empty** `Cosmos__ConnectionString`, `COSMOS_CONNECTION_STRING`, or `COSMOS_KEY` in **user or machine** environment variables (they used to override secrets when env was applied last; the app now applies secrets after env, but clearing stale empty vars still avoids confusion).
- Vector indexing applies only to **new** containers; if you change vector settings, use a new container name or database and re-ingest.

### OpenAI / embedding issues

- **`401` / invalid API key**: confirm `OpenAI:ApiKey` or `OPENAI_API_KEY` is set for the same process as `dotnet run`, or `OpenAI__ApiKey` in Azure Functions configuration.
- **Quota / billing**: embedding calls require an account with available quota.
- **Wrong vector size**: if you change `OpenAI:EmbeddingDimensions`, recreate the Cosmos container (or a new container name) and re-run blog processing so stored vectors match the index.

### Application Issues

```bash
# Check .NET version
dotnet --version

# Restore packages
dotnet restore

# Clean and rebuild
dotnet clean && dotnet build
```

## Performance Notes

- **Cosmos DB**: Request charges depend on indexing, RU/s or serverless configuration, and result size
- **OpenAI**: Latency and cost depend on model and region; `text-embedding-3-small` is sized for production use
- **First Run**: Initial blog post processing may take several minutes depending on the number of posts
- **Embeddings**: Stored in Cosmos DB and queried with `VectorDistance`

## Technical Details

### Dependencies
- `Microsoft.Azure.Cosmos` - Cosmos DB for NoSQL client (vector policy + `VectorDistance` queries)
- `Microsoft.Extensions.AI` - AI abstraction layer
- `Microsoft.Extensions.AI.OpenAI` - OpenAI embedding integration
- `Spectre.Console` - Enhanced console output
- Custom services for Cosmos DB and blog retrieval

### Vector store schema (container items)
- **Partition key**: `/id` (one item per chunk or single-chunk post)
- **Vectors**: `vector`: 768-dimensional `float32`, cosine distance
- **Metadata**: `title`, `url`, `parent_post_id`, `chunk_index`

### Search Algorithm
1. Convert search query to embedding using OpenAI
2. Run a Cosmos SQL query ordering by `VectorDistance(c.vector, @embedding)`
3. Merge hits by `parent_post_id` and return top unique posts
4. Display results with metadata

## Development

### Project Structure
```
dotnet-semantic-search/
├── Program.cs              # Main application entry point
├── Models/                 # Data models
│   ├── BlogPost.cs        # Blog post model
│   └── EmbeddingDocument.cs # Vector document model
├── Services/              # Business logic
│   ├── CosmosDbService.cs # Cosmos DB vector operations
│   └── BlogRetrievalService.cs # RSS feed processing
└── Utils/                 # Utilities
    ├── ConsoleHelper.cs   # Console UI helpers
    └── Statics.cs         # Constants
```

### Adding New Features
- Extend `CosmosDbService` for new vector operations
- Modify `BlogRetrievalService` for different content sources
- Update models for additional metadata fields

### SemanticSearch.Web — SEO (CI)
- Public titles and descriptions are defined under **`Seo`** in **`SemanticSearch.Web/wwwroot/appsettings.json`**. **`Home.razor`** and **`NotFound.razor`** read those keys and emit **`<PageTitle>`** and **`<HeadContent>`** (Open Graph, Twitter, canonical, JSON-LD). **Do not** put the same home-route SEO tags in shell **`wwwroot/index.html`**: **`HeadOutlet`** is registered as **`head::after`**, so duplicated static tags would produce **two** **`<title>`** / **`<meta name="description">`** (and similar) in the document for crawlers.
- Length rules enforced in CI: **`Seo:Home:PageTitle`** and **`Seo:NotFound:PageTitle`** are **50–60** characters inclusive; **`Seo:Home:MetaDescription`** and **`Seo:NotFound:MetaDescription`** are **110–160** characters inclusive.
- From the **repository root**, run **`python scripts/check_seo_meta.py`** after changing SEO copy, **`index.html`**, **`sitemap.xml`**, or the **`Sitemap:`** line in **`robots.txt`**. GitHub Actions runs the same script before **`dotnet publish`** on Static Web Apps builds.
- If you move the site to another hostname, update **`Seo:CanonicalBaseUrl`** in **`appsettings.json`** (that drives **`Home.razor`** URLs), the **`<loc>`** in **`wwwroot/sitemap.xml`**, and the **`Sitemap:`** URL in **`wwwroot/robots.txt`**, then run the script until it passes. Keep **`/sitemap.xml`** in **`staticwebapp.config.json`** **`navigationFallback.exclude`** so the file is not rewritten to **`index.html`**.

### SemanticSearch.Web — pre-rendered HTML at publish
- The **`BlazorWasmPreRendering.Build`** package runs during **`dotnet publish`** and writes static HTML (including a single, crawler-visible **`<head>`** for **`/`** and **`/not-found`** from **`HeadOutlet`** output). **`Program.cs`** must keep service registration in a **`static void ConfigureServices(...)`** local function, as required by that package (see upstream README).
- **`BlazorWasmPrerenderingUrlPathToExplicitFetch`** includes **`/not-found`** because it is not linked from the home page.

## License

This project is for demonstration purposes. Check individual dependencies for their respective licenses.