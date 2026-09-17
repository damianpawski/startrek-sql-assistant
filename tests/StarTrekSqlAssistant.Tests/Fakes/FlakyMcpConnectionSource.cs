using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests.Fakes;

/// <summary>
/// A connection to <see cref="FakeDabMcpServer"/> that can be killed the way a
/// `docker compose restart dab` kills the real one: the tools handed out stay
/// resolvable, but calling one throws until the connection is invalidated and
/// re-established.
///
/// Generations are modelled exactly as the real provider does them, so the
/// "don't tear down the replacement someone else just built" rule is testable.
/// </summary>
public sealed class FlakyMcpConnectionSource(FakeDabMcpServer server) : IMcpConnectionSource
{
    private int _generation = 1;
    private bool _dead;

    /// <summary>When true, every newly established connection is dead on arrival.</summary>
    public bool StaysDead { get; set; }

    /// <summary>When true, re-establishing the connection fails outright.</summary>
    public bool ReconnectFails { get; set; }

    public int Generation => _generation;

    public int ResolveCount { get; private set; }

    public int InvalidateCount { get; private set; }

    /// <summary>Kills the current connection - tools resolve but fail when called.</summary>
    public void Drop() => _dead = true;

    public Task<(int Generation, AIFunction Tool)> ResolveAsync(
        string toolName, CancellationToken cancellationToken = default)
    {
        // The real provider waits on a semaphore with this token, so a cancelled
        // circuit surfaces here rather than as a tool failure.
        cancellationToken.ThrowIfCancellationRequested();
        ResolveCount++;

        if (ReconnectFails && InvalidateCount > 0)
        {
            throw new HttpRequestException("Connection refused (localhost:5000)");
        }

        var tool = _dead
            ? Broken(toolName)
            : (AIFunction)server.Tools.First(t => t.Name == toolName);

        return Task.FromResult((_generation, tool));
    }

    public Task InvalidateAsync(int generation, CancellationToken cancellationToken = default)
    {
        // The generation guard: a caller still holding a dead generation must
        // not discard a connection that has already been replaced.
        if (generation != _generation)
        {
            return Task.CompletedTask;
        }

        InvalidateCount++;
        _generation++;
        _dead = StaysDead;
        return Task.CompletedTask;
    }

    /// <summary>The tools as the agent would see them - each one self-healing.</summary>
    public IMcpToolProvider AsToolProvider(ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        IReadOnlyList<AITool> tools =
            [.. server.Tools.Select(AITool (t) => new ReconnectingMcpTool((AIFunction)t, this, log))];
        return new StubProvider(tools);
    }

    /// <summary>A tool that fails the way one bound to a dropped session fails.</summary>
    private static AIFunction Broken(string toolName) =>
        AIFunctionFactory.Create(
            string () => throw new IOException($"The MCP transport was closed (tool '{toolName}')."),
            toolName,
            "A tool on a connection that is no longer alive.");

    private sealed class StubProvider(IReadOnlyList<AITool> tools) : IMcpToolProvider
    {
        public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(tools);
    }
}
