using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OllamaSharp;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Answers plain-English questions about the Star Trek database by handing an
/// Ollama model the MCP tools exposed by Data API builder's SQL MCP Server.
///
/// The model never sees or writes SQL. It picks a tool - read_records,
/// aggregate_records, describe_entities, and so on - DAB turns that tool call
/// into a deterministic, parameterized query against SQL Server, and the
/// result goes back to the model to phrase as an answer. UseFunctionInvocation()
/// below is what runs that pick-a-tool / call-it / read-the-result loop
/// automatically; nothing here does that bookkeeping by hand.
///
/// Registered as a singleton (see Program.cs) so the MCP connection and tool
/// list are set up once and shared across every chat in the app.
/// </summary>
public sealed class StarTrekAgentService : IAsyncDisposable
{
    private const string SystemPrompt = """
        You are a research assistant for the Star Trek franchise. You answer
        questions using a SQL database reached only through tools - you never
        see or write SQL directly.

        The database has these entities: Series (TV shows), Episode (linked to
        Series via series_id), Movie (the films, not linked to a series),
        MediaSet (a DVD/Blu-ray/HD DVD release for a series+season), MediumVolume
        (a disc within a MediaSet), and MediumVolumeEpisode (which episodes are
        on which disc).

        Always use the tools to look up facts rather than relying on your own
        knowledge of Star Trek - the database is the source of truth for this
        conversation. If you are unsure what fields an entity has, call
        describe_entities first. Prefer aggregate_records for counts, sums, or
        "how many" questions rather than pulling every row yourself.

        Give clear, concise answers in plain English. Mention the specific
        titles, dates, numbers, or stardates you found so the answer is
        checkable against the data.
        """;

    private readonly Uri _mcpEndpoint;
    private readonly IChatClient _chatClient;
    private readonly ILogger<StarTrekAgentService> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private McpClient? _mcpClient;
    private IList<McpClientTool>? _tools;

    public StarTrekAgentService(
        IOptions<DabOptions> dabOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<StarTrekAgentService>();
        _mcpEndpoint = new Uri(dabOptions.Value.McpEndpoint);

        var ollama = new OllamaApiClient(new Uri(ollamaOptions.Value.Endpoint), ollamaOptions.Value.Model);

        _chatClient = new ChatClientBuilder(ollama)
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>Starts a new conversation, seeded with the system prompt.</summary>
    public static List<ChatMessage> CreateConversation() =>
        [new ChatMessage(ChatRole.System, SystemPrompt)];

    /// <summary>
    /// Sends the full conversation (including the newest user message) to the
    /// model and returns its reply. Does not mutate <paramref name="messages"/> -
    /// the caller is responsible for appending both the user's question and
    /// this method's reply to the conversation history.
    /// </summary>
    public async Task<string> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach the DAB MCP endpoint at {Endpoint}", _mcpEndpoint);
            return "I can't reach the database connector yet - it may still be starting up in Docker. " +
                   "Give it a few more seconds and ask again.";
        }

        var options = new ChatOptions { Tools = [.. _tools!] };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            return response.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The model or Ollama call failed");
            return "I hit an error talking to the model. Make sure Ollama is running and that " +
                   "the configured model has been pulled (see the README), then try again.";
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tools is not null)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null)
            {
                return;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = _mcpEndpoint,
                Name = "StarTrekSqlAssistant",
            });

            _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            _tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Connected to DAB MCP server at {Endpoint} - {Count} tools available",
                _mcpEndpoint, _tools.Count);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }
        _connectLock.Dispose();
    }
}
