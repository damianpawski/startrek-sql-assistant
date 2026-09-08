using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Answers plain-English questions about the Star Trek database by handing a
/// model the MCP tools exposed by Data API builder's SQL MCP Server. Which
/// model - a local one via Ollama, or OpenAI/Anthropic over their APIs - is a
/// config choice resolved by ChatClientFactory; nothing below this constructor
/// knows or cares which one answered.
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

        Their columns are:
          Series(series_id, title, begin, end)
          Episode(episode_id, series_id, title, airdate, remastered_airdate,
                  season, episode_number, production_code, stardate, date,
                  vignette)
          Movie(movie_id, title, release_date, stardate)
          MediaSet(media_set_id, series_id, type, season)
          MediumVolume(medium_volume_id, media_set_id, sequence)
          MediumVolumeEpisode(medium_volume_id, episode_id)

        Series.begin and Series.end are the dates a show first and last aired.

        Two things about this deployment will otherwise mislead you.
        describe_entities returns an empty field list for every entity here, so
        trust the schema above rather than concluding a column does not exist.
        And titles are stored in full - "Star Trek: Deep Space Nine", not "Deep
        Space Nine" - so if an exact-match filter on a name returns no rows,
        read the table instead (Series has 15 rows) and pick the row yourself.

        Always use the tools to look up facts rather than relying on your own
        knowledge of Star Trek - the database is the source of truth for this
        conversation. Prefer aggregate_records for counts, sums, or
        "how many" questions rather than pulling every row yourself.

        Give clear, concise answers in plain English. Mention the specific
        titles, dates, numbers, or stardates you found so the answer is
        checkable against the data.
        """;

    private readonly Uri _mcpEndpoint;
    private readonly ChatBackend _backend;
    private readonly IChatClient _chatClient;
    private readonly ILogger<StarTrekAgentService> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private McpClient? _mcpClient;
    private IList<McpClientTool>? _tools;

    public StarTrekAgentService(
        IOptions<DabOptions> dabOptions,
        IOptions<ModelOptions> modelOptions,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<OpenAIOptions> openAiOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<StarTrekAgentService>();
        _mcpEndpoint = new Uri(dabOptions.Value.McpEndpoint);

        _backend = ChatClientFactory.Create(
            modelOptions.Value,
            ollamaOptions.Value,
            openAiOptions.Value,
            anthropicOptions.Value);
        _chatClient = _backend.Client;

        _logger.LogInformation("Chat backend: {Backend}", _backend.Description);
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
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException. The
            // when-clause keeps a real user/circuit cancellation out of here.
            _logger.LogWarning(ex, "{Backend} did not answer within {Timeout}", _backend.Description, _backend.Timeout);
            return $"That question took longer than {_backend.Timeout.TotalSeconds:N0} seconds and I gave up " +
                   "waiting. A local thinking model on a small GPU can be slow - try a simpler question, or " +
                   "raise the configured provider's TimeoutSeconds.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The call to {Backend} failed", _backend.Description);
            return "I hit an error talking to the model. For a local model, make sure Ollama is running " +
                   "and the configured model has been pulled; for a hosted one, check the API key and " +
                   "model id (see the README), then try again.";
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
        _backend.OwnedDisposable?.Dispose();
        _connectLock.Dispose();
    }
}
