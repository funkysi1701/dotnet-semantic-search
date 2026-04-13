# .NET Semantic Search Demo

A .NET application demonstrating semantic search capabilities using AI embeddings with **Azure Cosmos DB for NoSQL** (vector search) and **Ollama** for local embedding inference.

## Features

- 📚 **Blog Post Processing**: Retrieves blog posts from RSS feeds and generates embeddings
- 🔍 **Semantic Search**: Search through blog posts using natural language queries
- 🎯 **Vector Similarity**: Uses AI embeddings to find semantically similar content
- ☁️ **Cosmos DB**: Embeddings and metadata stored in Azure Cosmos DB for NoSQL with native vector indexing

## Architecture

- **Vector Database**: Azure Cosmos DB for NoSQL (`VectorDistance` queries, 768-d cosine)
- **AI Embeddings**: Ollama with `nomic-embed-text` model (local installation)
- **Framework**: .NET 10
- **AI Integration**: Microsoft.Extensions.AI

## Prerequisites

### Required
- An [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/how-to-create-account) account with **vector search** enabled ([EnableNoSQLVectorSearch](https://learn.microsoft.com/azure/cosmos-db/vector-search))
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Platform-Specific Tools
- **macOS**: [Homebrew](https://brew.sh/) (recommended for Ollama installation)
- **Windows**: [Chocolatey](https://chocolatey.org/) (optional, for package management)
- **Linux**: `curl` for Ollama installation script

### Verify Prerequisites
```bash
# Check .NET
dotnet --version

# Should show .NET 10.0 or later
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
3. Set environment variables before `dotnet run` (PowerShell examples):

```powershell
$env:COSMOS_CONNECTION_STRING = "AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>;"
# Optional overrides (defaults shown):
# $env:COSMOS_DATABASE = "semantic-search-blog"
# $env:COSMOS_CONTAINER = "blog-embeddings"
```

Alternatively use `COSMOS_ENDPOINT` and `COSMOS_KEY` instead of `COSMOS_CONNECTION_STRING`.

The app creates the database and container on first run (768-dimensional cosine vectors on `/vector`, partition key `/id`).

### 3. Set Up Ollama (AI Model)

Ollama runs locally for better performance. Choose the installation method for your platform:

#### macOS Installation

```bash
# Install Ollama via Homebrew
brew install ollama

# Start Ollama as a service
brew services start ollama

# Pull the embedding model
ollama pull nomic-embed-text

# Verify the model is available
ollama list
```

#### Windows Installation

**Option 1: Direct Download (Recommended)**
1. Download the Ollama installer from [https://ollama.com/download](https://ollama.com/download)
2. Run the installer and follow the setup wizard
3. Ollama will start automatically as a Windows service

**Option 2: Using Chocolatey**
```powershell
# Install Chocolatey first if you haven't already
# Then install Ollama
choco install ollama

# Ollama starts automatically as a service
```

**After Installation (Both Options)**
```powershell
# Pull the embedding model
ollama pull nomic-embed-text

# Verify the model is available
ollama list
```

#### Linux Installation

```bash
# Install Ollama
curl -fsSL https://ollama.com/install.sh | sh

# Start Ollama service
sudo systemctl start ollama
sudo systemctl enable ollama

# Pull the embedding model
ollama pull nomic-embed-text

# Verify the model is available
ollama list
```

Ollama will be available at:
- **API**: http://localhost:11434

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
- **Embedding Model**: `nomic-embed-text` (274 MB)
- **Vector Dimensions**: 768
- **Embedding Provider**: Ollama

## Troubleshooting

### Cosmos DB issues

- Confirm **vector search** is enabled on the account and wait for the capability to apply.
- Ensure `COSMOS_CONNECTION_STRING` or `COSMOS_ENDPOINT` + `COSMOS_KEY` are set in the same shell session as `dotnet run`.
- Vector indexing applies only to **new** containers; if you change vector settings, use a new container name or database and re-ingest.

### Ollama Issues

#### macOS
```bash
# Check if Ollama service is running
brew services list | grep ollama

# Restart Ollama service
brew services restart ollama

# Check available models
ollama list

# Re-pull the model if needed
ollama pull nomic-embed-text
```

#### Windows
```powershell
# Check if Ollama service is running
Get-Service -Name "Ollama*"

# Restart Ollama service (run as Administrator)
Restart-Service -Name "Ollama*"

# Check available models
ollama list

# Re-pull the model if needed
ollama pull nomic-embed-text
```

#### Linux
```bash
# Check if Ollama service is running
sudo systemctl status ollama

# Restart Ollama service
sudo systemctl restart ollama

# Check available models
ollama list

# Re-pull the model if needed
ollama pull nomic-embed-text
```

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
- **Ollama Local**: Running Ollama locally provides better latency for embedding generation
- **First Run**: Initial blog post processing may take several minutes depending on the number of posts
- **Embeddings**: Stored in Cosmos DB and queried with `VectorDistance`

## Technical Details

### Dependencies
- `Microsoft.Azure.Cosmos` - Cosmos DB for NoSQL client (vector policy + `VectorDistance` queries)
- `Microsoft.Extensions.AI` - AI abstraction layer
- `Microsoft.Extensions.AI.Ollama` - Ollama integration
- `Spectre.Console` - Enhanced console output
- Custom services for Cosmos DB and blog retrieval

### Vector store schema (container items)
- **Partition key**: `/id` (one item per chunk or single-chunk post)
- **Vectors**: `vector`: 768-dimensional `float32`, cosine distance
- **Metadata**: `title`, `url`, `parent_post_id`, `chunk_index`

### Search Algorithm
1. Convert search query to embedding using Ollama
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

## License

This project is for demonstration purposes. Check individual dependencies for their respective licenses.