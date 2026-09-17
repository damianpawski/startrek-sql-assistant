using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// The real tool source: Data API builder's SQL MCP Server. DAB turns the
/// entities in dab/dab-config.json into tools (read_records, aggregate_records,
/// describe_entities, ...) and generates the parameterized T-SQL itself, which
/// is why the model never writes SQL.
///
/// Registered as a singleton so the connection and tool list are established
/// once, lazily, and shared across every Blazor circuit. The double-checked
/// lock is what keeps two simultaneous first questions from opening two
/// connections.
///
/// The connection is *replaceable*, not permanent. Restarting `dab` - the
/// documented way to pick up a dab-config.json change - invalidates its
/// streamable-HTTP session and kills every tool bound to it. Each connection
/// therefore carries a generation number: a tool that fails asks for its
/// generation to be invalidated (see <see cref="ReconnectingMcpTool"/>), the
/// next resolve builds a fresh connection, and the generation check makes a
/// second caller failing on the same dead connection a no-op instead of a
/// teardown of the replacement someone else just built.
/// </summary>
public sealed class DabMcpToolProvider : IMcpToolProvider, IMcpConnectionSource, IMcpConnectionState, IAsyncDisposable
{
    /// <summary>One established MCP session: the client, its tools, and which generation it is.</summary>
    private sealed record McpConnection(
        int Generation,
        McpClient Client,
        IReadOnlyList<AIFunction> RawTools,
        IReadOnlyList<AITool> Tools);

    private readonly Uri _endpoint;
    private readonly ILogger<DabMcpToolProvider> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private McpConnection? _connection;
    private int _generation;
    private Exception? _lastConnectFailure;
    private bool _disposed;

    public DabMcpToolProvider(IOptions<DabOptions> dabOptions, ILogger<DabMcpToolProvider> logger)
    {
        _endpoint = new Uri(dabOptions.Value.McpEndpoint);
        _logger = logger;
    }

    /// <summary>Where this provider is pointed, for the health check to report.</summary>
    public Uri Endpoint => _endpoint;

    /// <summary>
    /// A snapshot of the connection state for <see cref="DabMcpHealthCheck"/>.
    /// Deliberately a plain field read - the health endpoint must never connect,
    /// or a probe against a down DAB hangs instead of reporting it.
    /// </summary>
    public (bool Connected, int Generation, int ToolCount, Exception? LastFailure) State
    {
        get
        {
            var connection = _connection;
            return (connection is not null, connection?.Generation ?? 0, connection?.Tools.Count ?? 0, _lastConnectFailure);
        }
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        (await GetConnectionAsync(cancellationToken)).Tools;

    /// <inheritdoc />
    public async Task<(int Generation, AIFunction Tool)> ResolveAsync(
        string toolName, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);

        // The raw tool, never the ReconnectingMcpTool wrapper around it -
        // resolving the wrapper here would make every retry recurse.
        var tool = connection.RawTools.FirstOrDefault(t => t.Name == toolName)
            ?? throw new McpConnectionLostException(
                $"The MCP server no longer offers a tool named '{toolName}'.");

        return (connection.Generation, tool);
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(int generation, CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null || _connection.Generation != generation)
            {
                // Someone already replaced it. Leaving the newer connection
                // alone is the whole point of the generation number.
                return;
            }

            _logger.LogWarning("Discarding MCP connection {Generation} to {Endpoint}", generation, _endpoint);

            var dead = _connection;
            _connection = null;
            await DisposeQuietlyAsync(dead.Client);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Opens the connection if there isn't one, otherwise hands back the current
    /// one. Public so the startup warm-up can establish it before the first
    /// question rather than making someone wait for it.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await GetConnectionAsync(cancellationToken);

    private async Task<McpConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } current)
        {
            return current;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { } established)
            {
                return established;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = _endpoint,
                Name = "StarTrekSqlAssistant",
            });

            McpClient client;
            IList<McpClientTool> tools;
            try
            {
                client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Remembered so /health can say *why* rather than just "down".
                _lastConnectFailure = ex;
                throw;
            }

            var generation = ++_generation;
            var wrapped = tools
                .Select(AITool (t) => new ReconnectingMcpTool(t, this, _logger))
                .ToArray();

            _lastConnectFailure = null;

            _logger.LogInformation(
                "Connected to DAB MCP server at {Endpoint} - {Count} tools available (connection {Generation})",
                _endpoint, tools.Count, generation);

            return _connection = new McpConnection(generation, client, [.. tools], wrapped);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            _disposed = true;
            var connection = _connection;
            _connection = null;
            if (connection is not null)
            {
                await DisposeQuietlyAsync(connection.Client);
            }
        }
        finally
        {
            _connectLock.Release();
            _connectLock.Dispose();
        }
    }

    /// <summary>
    /// Disposing a client whose transport is already dead can throw, and by that
    /// point we have already decided to abandon it - letting that escape would
    /// turn recovery into the very failure it is recovering from.
    /// </summary>
    private async ValueTask DisposeQuietlyAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure while disposing a dead MCP client");
        }
    }
}
