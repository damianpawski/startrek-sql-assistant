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
/// </summary>
public sealed class DabMcpToolProvider : IMcpToolProvider, IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly ILogger<DabMcpToolProvider> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private McpClient? _mcpClient;
    private IReadOnlyList<AITool>? _tools;

    public DabMcpToolProvider(IOptions<DabOptions> dabOptions, ILogger<DabMcpToolProvider> logger)
    {
        _endpoint = new Uri(dabOptions.Value.McpEndpoint);
        _logger = logger;
    }

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_tools is not null)
        {
            return _tools;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null)
            {
                return _tools;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = _endpoint,
                Name = "StarTrekSqlAssistant",
            });

            _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Connected to DAB MCP server at {Endpoint} - {Count} tools available",
                _endpoint, tools.Count);

            return _tools = [.. tools];
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
