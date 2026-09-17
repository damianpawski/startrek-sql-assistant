using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// The connection state <see cref="DabMcpHealthCheck"/> reports.
/// <see cref="DabMcpToolProvider"/> is the real implementation; the seam keeps
/// the health check testable without an MCP server to be healthy against.
/// </summary>
public interface IMcpConnectionState
{
    /// <summary>Where the provider is pointed.</summary>
    Uri Endpoint { get; }

    /// <summary>
    /// A snapshot: whether a connection is currently established, which
    /// generation it is, how many tools it carries, and why the last connect
    /// attempt failed if one did.
    /// </summary>
    (bool Connected, int Generation, int ToolCount, Exception? LastFailure) State { get; }
}

/// <summary>
/// Reports the MCP connection at /health, so "the connector is down" is
/// observable from outside rather than only readable as a chat bubble.
///
/// This reads the provider's cached state and never connects. A health endpoint
/// that dialled DAB would hang for the connect timeout exactly when DAB is
/// down - which is the one moment a probe has to answer quickly.
/// </summary>
public sealed class DabMcpHealthCheck(IMcpConnectionState provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var (connected, generation, toolCount, lastFailure) = provider.State;

        var data = new Dictionary<string, object>
        {
            ["endpoint"] = provider.Endpoint.ToString(),
            ["generation"] = generation,
            ["tools"] = toolCount,
        };

        if (connected)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"Connected to the DAB MCP server; {toolCount} tools available.", data));
        }

        if (lastFailure is not null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The last attempt to reach the DAB MCP server failed: {lastFailure.Message}", lastFailure, data));
        }

        // No connection and nothing has failed: still starting up. Degraded, not
        // Unhealthy - a cold `docker compose up` passes through here normally.
        return Task.FromResult(HealthCheckResult.Degraded(
            "Not connected to the DAB MCP server yet.", data: data));
    }
}
