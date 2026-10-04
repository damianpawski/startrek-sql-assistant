using System.Text.Json;
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

    // ---- What /health actually returns ------------------------------------
    //
    // The checks above assert what DabMcpHealthCheck computes. These assert
    // that it survives the trip to the wire: the framework's default response
    // writer emits the status and drops every check's data, so all of the above
    // was unreadable from outside until HealthResponse existed.

    private static async Task<JsonElement> Body(IMcpConnectionState state)
    {
        var result = await Check(state);
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["dab-mcp"] = new(result.Status, result.Description, TimeSpan.Zero, result.Exception, result.Data),
            },
            TimeSpan.Zero);

        return JsonDocument.Parse(HealthResponse.Serialize(report)).RootElement;
    }

    [Fact]
    public async Task The_response_carries_the_connection_generation()
    {
        // The number that says whether the MCP connection has been rebuilt.
        // Watching it go 1 -> 2 after `docker compose restart dab` is the
        // cheapest proof ReconnectingMcpTool did its job.
        var body = await Body(new State(Connected: true, Generation: 2, ToolCount: 7, LastFailure: null));

        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        var check = body.GetProperty("checks").GetProperty("dab-mcp");
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.Equal(2, check.GetProperty("data").GetProperty("generation").GetInt32());
        Assert.Equal(7, check.GetProperty("data").GetProperty("tools").GetInt32());
        Assert.Equal("http://dab:5000/mcp", check.GetProperty("data").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task The_response_says_why_a_failed_connect_failed()
    {
        var body = await Body(new State(
            Connected: false, Generation: 0, ToolCount: 0,
            LastFailure: new HttpRequestException("Connection refused (dab:5000)")));

        var check = body.GetProperty("checks").GetProperty("dab-mcp");
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.Contains("Connection refused", check.GetProperty("description").GetString()!);
    }

    [Fact]
    public async Task The_response_never_carries_a_stack_trace()
    {
        // /health is pollable by anything that can reach the app. The
        // description carries the exception's message, which is the useful
        // part; the stack trace is not something to hand out.
        var body = await Body(new State(
            Connected: false, Generation: 0, ToolCount: 0,
            LastFailure: new HttpRequestException("Connection refused (dab:5000)")));

        var json = body.GetRawText();
        Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpRequestException", json, StringComparison.Ordinal);
    }
}
