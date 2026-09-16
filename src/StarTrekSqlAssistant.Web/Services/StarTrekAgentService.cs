using Microsoft.Extensions.AI;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Answers plain-English questions about the Star Trek database by handing a
/// model the MCP tools exposed by Data API builder's SQL MCP Server. Which
/// model - a local one via Ollama, or OpenAI/Anthropic over their APIs - is a
/// config choice resolved by the injected <see cref="IChatClientFactory"/>;
/// nothing below this constructor knows or cares which one answered. Where the
/// tools come from is likewise an <see cref="IMcpToolProvider"/> detail.
///
/// The model never sees or writes SQL. It picks a tool - read_records,
/// aggregate_records, describe_entities, and so on - DAB turns that tool call
/// into a deterministic, parameterized query against SQL Server, and the
/// result goes back to the model to phrase as an answer.
/// UseFunctionInvocation(), applied in ChatClientFactory, is what runs that
/// pick-a-tool / call-it / read-the-result loop automatically; nothing here
/// does that bookkeeping by hand.
///
/// Registered as a singleton (see Program.cs) so the MCP connection and tool
/// list are set up once and shared across every chat in the app.
/// </summary>
public sealed class StarTrekAgentService : IStarTrekAgent, IAsyncDisposable
{
    private readonly IMcpToolProvider _toolProvider;
    private readonly ChatBackend _backend;
    private readonly IChatClient _chatClient;
    private readonly ILogger<StarTrekAgentService> _logger;

    public StarTrekAgentService(
        IChatClientFactory chatClientFactory,
        IMcpToolProvider toolProvider,
        ILogger<StarTrekAgentService> logger)
    {
        _logger = logger;
        _toolProvider = toolProvider;

        _backend = chatClientFactory.Create();
        _chatClient = _backend.Client;

        _logger.LogInformation("Chat backend: {Backend}", _backend.Description);
    }

    /// <inheritdoc />
    public List<ChatMessage> CreateConversation() =>
        [new ChatMessage(ChatRole.System, StarTrekPrompt.SystemPrompt)];

    /// <inheritdoc />
    /// <remarks>
    /// Every failure below returns a friendly string rather than throwing: a
    /// broken stack should read as a normal reply in the chat instead of
    /// killing the Blazor circuit. The real exception is in the app log.
    /// </remarks>
    public async Task<string> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AITool> tools;
        try
        {
            tools = await _toolProvider.GetToolsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not reach the DAB MCP endpoint");
            return "I can't reach the database connector yet - it may still be starting up in Docker. " +
                   "Give it a few more seconds and ask again.";
        }

        var options = new ChatOptions { Tools = [.. tools] };

        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);

            // Empty text means the model never produced an answer - typically it
            // was still asking for tools when ChatClientFactory.MaxToolRounds cut
            // the loop off. Say so instead of rendering a blank bubble.
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogWarning(
                    "{Backend} returned no answer text after up to {Rounds} tool rounds",
                    _backend.Description, ChatClientFactory.MaxToolRounds);
                return $"I couldn't work that out within {ChatClientFactory.MaxToolRounds} database lookups. " +
                       "Try asking in a simpler or more specific way.";
            }

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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The call to {Backend} failed", _backend.Description);
            return "I hit an error talking to the model. For a local model, make sure Ollama is running " +
                   "and the configured model has been pulled; for a hosted one, check the API key and " +
                   "model id (see the README), then try again.";
        }
    }

    public ValueTask DisposeAsync()
    {
        _backend.OwnedDisposable?.Dispose();
        return ValueTask.CompletedTask;
    }
}
