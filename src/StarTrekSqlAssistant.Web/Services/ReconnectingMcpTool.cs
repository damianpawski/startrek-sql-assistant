using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Thrown when an MCP tool call failed, the connection was rebuilt, and the
/// retry failed too. Distinct from every other failure the agent can hit so
/// <see cref="StarTrekAgentService"/> can say "the connector went away" instead
/// of blaming the model.
/// </summary>
public sealed class McpConnectionLostException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Where a tool comes from, and how to throw away the connection it came from.
/// <see cref="DabMcpToolProvider"/> is the real implementation; the seam exists
/// so <see cref="ReconnectingMcpTool"/>'s recovery can be tested in process,
/// without a container to restart.
/// </summary>
public interface IMcpConnectionSource
{
    /// <summary>
    /// The named tool on the current connection, with the generation it belongs
    /// to. Connects if there is no connection yet.
    /// </summary>
    Task<(int Generation, AIFunction Tool)> ResolveAsync(string toolName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the connection identified by <paramref name="generation"/>, so
    /// the next resolve builds a fresh one. A no-op if the current connection is
    /// already newer - two callers failing on the same dead connection must not
    /// tear down each other's replacement.
    /// </summary>
    Task InvalidateAsync(int generation, CancellationToken cancellationToken = default);
}

/// <summary>
/// One MCP tool, wrapped so a dropped connection heals instead of poisoning the
/// app for the rest of its life.
///
/// This has to sit at the *invocation*, not at the tool-list fetch. A restarted
/// `dab` invalidates the streamable-HTTP session, but the cached tool list is
/// still a perfectly good object: nothing fails until the model actually calls
/// one. And FunctionInvokingChatClient does not surface that failure either -
/// it feeds the exception back to the model as a generic error result and keeps
/// going, rethrowing only once MaximumConsecutiveErrorsPerRequest (3) is hit.
/// So without this wrapper, a `docker compose restart dab` costs the user three
/// wasted tool rounds and then a misleading error, with no way back except
/// restarting the app.
///
/// Any exception counts as a lost connection. That reads as over-broad, but DAB
/// reports *data* problems - an unparseable filter, an unknown column, a write
/// blocked by the anonymous/read permission - as ordinary tool results rather
/// than exceptions, so an exception escaping an MCP tool is a transport problem
/// by construction. Matching on types would also be fragile: the plausible set
/// spans ClientTransportClosedException (an IOException), McpException, two
/// different HttpRequestException types, and ObjectDisposedException.
/// </summary>
public sealed class ReconnectingMcpTool(
    AIFunction inner,
    IMcpConnectionSource source,
    ILogger logger) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Every MCP tool is already wrapped in one of these, which makes this the
        // one place that sees which tool the model picked, how long DAB took, and
        // whether the call survived on the first connection or needed a rebuilt
        // one. Arguments are never tagged: a filter value is data from the
        // database's own domain, and the tool name plus the outcome is what a
        // dashboard is actually read for.
        using var activity = AgentTelemetry.Source.StartActivity(AgentTelemetry.ToolActivity);
        activity?.SetTag(AgentTelemetry.Tags.ToolName, Name);
        var started = Stopwatch.GetTimestamp();

        var (generation, tool) = await source.ResolveAsync(Name, cancellationToken);
        activity?.SetTag(AgentTelemetry.Tags.Generation, generation);

        try
        {
            var result = await tool.InvokeAsync(arguments, cancellationToken);
            Record(activity, started, AgentTelemetry.Outcomes.Ok);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The log line stays: it carries *why* the connection went, which no
            // counter can.
            logger.LogWarning(ex,
                "Tool {Tool} failed on MCP connection {Generation}; reconnecting and retrying once", Name, generation);

            await source.InvalidateAsync(generation, cancellationToken);

            AIFunction fresh;
            try
            {
                (_, fresh) = await source.ResolveAsync(Name, cancellationToken);
            }
            catch (Exception reconnectFailure) when (
                reconnectFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Record(activity, started, AgentTelemetry.Outcomes.ConnectionLost, reconnectFailure);
                throw new McpConnectionLostException(
                    $"Could not reconnect to the MCP server after tool '{Name}' failed.", reconnectFailure);
            }

            try
            {
                var result = await fresh.InvokeAsync(arguments, cancellationToken);

                // Answered, but only because the connection was rebuilt underneath
                // it - the difference between "DAB restarted and nobody noticed"
                // and "nothing happened at all".
                Record(activity, started, AgentTelemetry.Outcomes.Retried);
                return result;
            }
            catch (Exception retryFailure) when (
                retryFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Record(activity, started, AgentTelemetry.Outcomes.ConnectionLost, retryFailure);
                throw new McpConnectionLostException(
                    $"Tool '{Name}' failed again on a freshly established MCP connection.", retryFailure);
            }
        }
    }

    private void Record(Activity? activity, long started, string outcome, Exception? failure = null)
    {
        var tags = new TagList
        {
            { AgentTelemetry.Tags.ToolName, Name },
            { AgentTelemetry.Tags.Outcome, outcome },
        };

        AgentTelemetry.ToolCalls.Add(1, tags);
        AgentTelemetry.ToolCallDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);

        activity?.SetTag(AgentTelemetry.Tags.Outcome, outcome);
        if (failure is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
            activity?.AddException(failure);
        }
    }
}
