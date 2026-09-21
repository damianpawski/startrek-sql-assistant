using System.Diagnostics;
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
    private readonly QuestionLimiter _limiter;
    private readonly ChatBackend _backend;
    private readonly IChatClient _chatClient;
    private readonly ILogger<StarTrekAgentService> _logger;

    public StarTrekAgentService(
        IChatClientFactory chatClientFactory,
        IMcpToolProvider toolProvider,
        QuestionLimiter limiter,
        ILogger<StarTrekAgentService> logger)
    {
        _logger = logger;
        _toolProvider = toolProvider;
        _limiter = limiter;

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
        // The question span is the parent of everything the answer costs: the
        // gen_ai span Microsoft.Extensions.AI emits per model round trip, and a
        // mcp_tool span per tool the model picked. The outcome tag is what makes
        // the friendly-string failures below countable - without it a stack that
        // is answering nothing at all looks, in the metrics, exactly like one
        // that is answering everything.
        using var activity = AgentTelemetry.Source.StartActivity(AgentTelemetry.QuestionActivity);
        var started = Stopwatch.GetTimestamp();

        // Only an escaping OperationCanceledException can leave this unset - every
        // other path below assigns it before returning. Deliberately never the
        // question text: content on a span is governed solely by
        // Telemetry:CaptureMessageContent, which this does not consult.
        var outcome = AgentTelemetry.Outcomes.Cancelled;
        activity?.SetTag(AgentTelemetry.Tags.QuestionLength, messages[^1].Text?.Length ?? 0);

        // Before anything that implies the question is being answered - the
        // active counter, the "asking {Backend}" log line - because a rejected one
        // is not. Held for the whole method: disposing it is what frees the
        // in-flight slot, so it must outlive the model call. See QuestionLimiter
        // for why this lives here rather than in HTTP middleware.
        using var admission = _limiter.TryAdmit();
        if (!admission.IsAdmitted)
        {
            outcome = admission.RejectedAs!;
            _logger.LogInformation("Question turned away ({Outcome})", outcome);
            Record(activity, started, outcome);
            return outcome == AgentTelemetry.Outcomes.Busy
                ? "Another question is being answered right now, and this assistant takes one at a time. " +
                  "Try again when it finishes."
                // "Within", not "in": a sliding window frees a slot somewhere in
                // the next minute and cannot say exactly when - see QuestionLimiter.
                : $"This assistant answers at most {_limiter.QuestionsPerMinute} questions a minute, and " +
                  "that limit has been reached. Try again within a minute.";
        }

        // Logged and counted before the slow part, not after: a question that is
        // still being answered produces no span (spans export on completion) and
        // no log of its own, so without these two the dashboard cannot tell a
        // model thinking for four minutes from one that never started. The length,
        // never the text - Telemetry:CaptureMessageContent is the only switch that
        // puts what someone typed anywhere.
        AgentTelemetry.QuestionsActive.Add(1);
        _logger.LogInformation(
            "Question received ({Length} characters); asking {Backend}",
            messages[^1].Text?.Length ?? 0, _backend.Description);

        try
        {
            IReadOnlyList<AITool> tools;
            try
            {
                tools = await _toolProvider.GetToolsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                outcome = AgentTelemetry.Outcomes.DabUnreachable;
                Fail(activity, ex);
                _logger.LogWarning(ex, "Could not reach the DAB MCP endpoint");
                return "I can't reach the database connector yet - it may still be starting up in Docker. " +
                       "Give it a few more seconds and ask again.";
            }

            activity?.SetTag(AgentTelemetry.Tags.ToolCount, tools.Count);
            var options = new ChatOptions { Tools = [.. tools] };

            try
            {
                var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);

                // Empty text means the model never produced an answer - typically it
                // was still asking for tools when ChatClientFactory.MaxToolRounds cut
                // the loop off. Say so instead of rendering a blank bubble.
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    outcome = AgentTelemetry.Outcomes.NoAnswer;
                    _logger.LogWarning(
                        "{Backend} returned no answer text after up to {Rounds} tool rounds",
                        _backend.Description, ChatClientFactory.MaxToolRounds);
                    return $"I couldn't work that out within {ChatClientFactory.MaxToolRounds} database lookups. " +
                           "Try asking in a simpler or more specific way.";
                }

                outcome = AgentTelemetry.Outcomes.Answered;
                return response.Text;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient surfaces its own timeout as TaskCanceledException. The
                // when-clause keeps a real user/circuit cancellation out of here.
                outcome = AgentTelemetry.Outcomes.Timeout;
                Fail(activity, ex);
                _logger.LogWarning(ex, "{Backend} did not answer within {Timeout}", _backend.Description, _backend.Timeout);
                return $"That question took longer than {_backend.Timeout.TotalSeconds:N0} seconds and I gave up " +
                       "waiting. A local thinking model on a small GPU can be slow - try a simpler question, or " +
                       "raise the configured provider's TimeoutSeconds.";
            }
            catch (McpConnectionLostException ex)
            {
                // The tool wrapper already reconnected and retried once, and that
                // retry failed too - so this is DAB being genuinely gone, not a
                // stale session. Distinguished from the generic catch below because
                // that one tells the user to go and check their Ollama install,
                // which is the wrong thing to go and check.
                outcome = AgentTelemetry.Outcomes.ConnectionLost;
                Fail(activity, ex);
                _logger.LogWarning(ex, "Lost the MCP connection mid-answer and could not re-establish it");
                return "I lost the connection to the database connector - it may have restarted. " +
                       "Give it a few seconds and ask again.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                outcome = AgentTelemetry.Outcomes.Error;
                Fail(activity, ex);
                _logger.LogWarning(ex, "The call to {Backend} failed", _backend.Description);
                return "I hit an error talking to the model. For a local model, make sure Ollama is running " +
                       "and the configured model has been pulled; for a hosted one, check the API key and " +
                       "model id (see the README), then try again.";
            }
        }
        finally
        {
            // In a finally so no return path can forget, including the cancelled
            // one that never reaches a catch block at all.
            AgentTelemetry.QuestionsActive.Add(-1);
            Record(activity, started, outcome);
        }
    }

    /// <summary>
    /// Tags the span and counts the question under its outcome. One method so
    /// the admitted path (in AskAsync's finally) and the turned-away path record
    /// a question identically - two copies of these lines is how a dashboard
    /// ends up counting one kind of question and quietly missing the other.
    /// </summary>
    private static void Record(Activity? activity, long started, string outcome)
    {
        var tag = new KeyValuePair<string, object?>(AgentTelemetry.Tags.Outcome, outcome);
        activity?.SetTag(AgentTelemetry.Tags.Outcome, outcome);
        AgentTelemetry.Questions.Add(1, tag);
        AgentTelemetry.QuestionDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag);
    }


    /// <summary>
    /// Marks the span failed. The friendly string the user gets back is not an
    /// error as far as the chat is concerned, but it is as far as a trace is -
    /// otherwise every one of these looks like a successful answer.
    /// </summary>
    private static void Fail(Activity? activity, Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.AddException(ex);
    }

    public ValueTask DisposeAsync()
    {
        _backend.OwnedDisposable?.Dispose();
        return ValueTask.CompletedTask;
    }
}
