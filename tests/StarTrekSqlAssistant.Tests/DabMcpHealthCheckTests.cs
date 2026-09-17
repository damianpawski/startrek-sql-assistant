using Microsoft.Extensions.Diagnostics.HealthChecks;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// /health exists so a dropped connector is visible to something other than a
/// person reading a chat bubble. The three states have to stay distinguishable:
/// "starting up" is not the same failure as "DAB refused the connection", and
/// only the second one should page anyone.
/// </summary>
public class DabMcpHealthCheckTests
{
    private sealed record State(
        bool Connected, int Generation, int ToolCount, Exception? LastFailure) : IMcpConnectionState
    {
        public Uri Endpoint => new("http://dab:5000/mcp");

        (bool, int, int, Exception?) IMcpConnectionState.State => (Connected, Generation, ToolCount, LastFailure);
    }

    private static Task<HealthCheckResult> Check(IMcpConnectionState state) =>
        new DabMcpHealthCheck(state).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Connected_is_healthy_and_reports_the_tool_count()
    {
        var result = await Check(new State(Connected: true, Generation: 2, ToolCount: 6, LastFailure: null));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(6, result.Data["tools"]);
        Assert.Equal(2, result.Data["generation"]);
    }

    [Fact]
    public async Task Not_connected_yet_is_degraded_rather_than_unhealthy()
    {
        // A cold `docker compose up` passes through here every time: DAB is not
        // listening yet and nothing has actually failed.
        var result = await Check(new State(Connected: false, Generation: 0, ToolCount: 0, LastFailure: null));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task A_failed_connect_is_unhealthy_and_says_why()
    {
        var failure = new HttpRequestException("Connection refused (dab:5000)");

        var result = await Check(new State(Connected: false, Generation: 0, ToolCount: 0, LastFailure: failure));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Connection refused", result.Description);
        Assert.Same(failure, result.Exception);
    }
}
