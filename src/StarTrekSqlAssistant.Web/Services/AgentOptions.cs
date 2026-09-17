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
/// Where telemetry goes and how much of it there is. Bound from the "Telemetry"
/// config section (env vars: Telemetry__OtlpEndpoint, Telemetry__CaptureMessageContent).
/// </summary>
public class TelemetryOptions
{
    /// <summary>What this app calls itself in traces, metrics and logs.</summary>
    public string ServiceName { get; set; } = "startrek-sql-assistant";

    /// <summary>
    /// OTLP collector to export to - the Aspire Dashboard's ingest port
    /// (http://aspire-dashboard:18889) under docker compose.
    ///
    /// Empty disables the exporter entirely, which is the default for
    /// appsettings.json: `dotnet run` on the host is the fast-iteration mode and
    /// usually has nothing listening, so it must not spend a background thread
    /// retrying connections to a collector that isn't there. Instrumentation is
    /// still registered either way, so the cost of leaving it empty is only that
    /// nothing is shipped anywhere.
    /// </summary>
    public string OtlpEndpoint { get; set; } = "";

    /// <summary>
    /// Whether prompts and model responses are recorded on the gen_ai spans.
    /// Off by default: the conversation includes whatever the user typed, and a
    /// dashboard is a much easier thing to read than a log file. Turn it on while
    /// debugging a bad answer - it is the only way to see what the model was
    /// actually told - and turn it back off afterwards.
    /// </summary>
    public bool CaptureMessageContent { get; set; }
}

/// <summary>
/// Which chat backend the agent talks to. Everything downstream of the
/// constructor is <c>IChatClient</c>, so this is the only thing that changes
/// between a local model and a hosted one.
/// </summary>
public enum ModelProvider
{
    /// <summary>A model served by a local Ollama install. No API key, no data leaves the machine.</summary>
    Ollama,

    /// <summary>OpenAI (or an OpenAI-compatible endpoint, via <see cref="OpenAIOptions.Endpoint"/>).</summary>
    OpenAI,

    /// <summary>Anthropic's API.</summary>
    Anthropic,
}

/// <summary>
/// Picks the chat backend. Bound from the "Model" config section
/// (env var: Model__Provider). Defaults to Ollama so a clone of this repo
/// still runs fully locally with no credentials.
/// </summary>
public class ModelOptions
{
    public ModelProvider Provider { get; set; } = ModelProvider.Ollama;
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

    /// <summary>
    /// How long to wait on a single Ollama call before giving up.
    /// HttpClient's built-in default is 100 seconds, which is not enough here:
    /// answering one question runs several round trips (pick a tool, read the
    /// result, phrase the answer), and a local thinking model can spend more
    /// than 100s on a single one of them. Env var: Ollama__TimeoutSeconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// OpenAI settings, used when Model__Provider is "OpenAI".
/// Bound from the "OpenAI" config section (env vars: OpenAI__ApiKey, OpenAI__Model).
/// Keep the key out of appsettings.json - use user-secrets locally
/// (<c>dotnet user-secrets set "OpenAI:ApiKey" "sk-..."</c>) or an env var.
/// </summary>
public class OpenAIOptions
{
    public string ApiKey { get; set; } = "";

    /// <summary>Must be a tool-calling-capable model.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Optional base URL, for Azure OpenAI or any OpenAI-compatible gateway.
    /// Empty means api.openai.com.
    /// </summary>
    public string Endpoint { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Anthropic settings, used when Model__Provider is "Anthropic".
/// Bound from the "Anthropic" config section (env vars: Anthropic__ApiKey,
/// Anthropic__Model). Keep the key out of appsettings.json - use user-secrets
/// locally (<c>dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."</c>)
/// or an env var.
/// </summary>
public class AnthropicOptions
{
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Claude model id, exactly as the API names it - no date suffix.
    /// Haiku 4.5 is the cheapest current model that handles this app's
    /// tool-calling reliably; "claude-sonnet-5" and "claude-opus-5" are
    /// drop-in swaps if it struggles.
    /// </summary>
    public string Model { get; set; } = "claude-haiku-4-5";

    /// <summary>
    /// Cap on tokens generated per response. This is a ceiling, not a spend -
    /// short answers cost what they cost. Sized so a long answer can't be
    /// truncated mid-sentence.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 16000;

    public int TimeoutSeconds { get; set; } = 300;
}
