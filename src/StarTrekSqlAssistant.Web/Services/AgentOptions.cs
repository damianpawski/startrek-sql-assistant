namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Where to find Data API builder's SQL MCP Server.
/// Bound from the "Dab" config section (env var: Dab__McpEndpoint).
/// </summary>
public class DabOptions
{
    public string McpEndpoint { get; set; } = "http://localhost:5000/mcp";
}

/// <summary>
/// Where to find the local Ollama server, and which model to use.
/// Bound from the "Ollama" config section (env vars: Ollama__Endpoint, Ollama__Model).
/// The model must support tool calling - see the Ollama model library.
/// </summary>
public class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.1";
}
