using Microsoft.Extensions.AI;
using StarTrekSqlAssistant.Tests.Fakes;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// What happens when DAB restarts under a running app - the case that used to
/// poison the singleton for the rest of the process's life.
///
/// The tool list is cached, and a restarted DAB invalidates its session without
/// touching that cache, so nothing fails until the model actually calls a tool.
/// These tests therefore all break the connection at *invocation* time, which
/// is where the recovery has to live.
/// </summary>
public class McpReconnectTests
{
    private static AIFunction ReadRecords(FlakyMcpConnectionSource source) =>
        (AIFunction)source.AsToolProvider().GetToolsAsync().Result.First(t => t.Name == "read_records");

    private static AIFunctionArguments SeriesQuery() =>
        new() { ["entity"] = "Series", ["select"] = "title,begin" };

    [Fact]
    public async Task A_dropped_connection_is_rebuilt_and_the_call_still_returns_data()
    {
        var server = new FakeDabMcpServer();
        var source = new FlakyMcpConnectionSource(server);
        var tool = ReadRecords(source);

        source.Drop();
        var result = await tool.InvokeAsync(SeriesQuery(), TestContext.Current.CancellationToken);

        Assert.Contains("Deep Space Nine", result?.ToString());
        Assert.Equal(1, source.InvalidateCount);
        Assert.Equal(2, source.Generation); // rebuilt exactly once
        Assert.Single(server.Calls);        // the retry is what reached the server
    }

    [Fact]
    public async Task The_model_never_sees_the_failure()
    {
        // The point of retrying inside the tool rather than letting the
        // exception out: FunctionInvokingChatClient would otherwise feed the
        // model a generic error result, burning one of only six tool rounds.
        var server = new FakeDabMcpServer();
        var source = new FlakyMcpConnectionSource(server);

        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", select = "title,begin" })
            .ThenSay("Deep Space Nine ran from 1993 to 1999.");

        var agent = TestAgent.Build(ChatClientFactory.Wrap(client), source.AsToolProvider());
        var messages = agent.CreateConversation();
        messages.Add(new ChatMessage(ChatRole.User, "When did DS9 start and end?"));

        source.Drop();
        var reply = await agent.AskAsync(messages, TestContext.Current.CancellationToken);

        Assert.Equal("Deep Space Nine ran from 1993 to 1999.", reply);

        // Two round trips, not three - no error round was spent.
        Assert.Equal(2, client.CallCount);

        var results = client.ReceivedConversations[^1]
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToArray();
        Assert.All(results, r => Assert.Null(r.Exception));
        Assert.Contains(results, r => r.Result?.ToString()?.Contains("Deep Space Nine") == true);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_re_established_surfaces_as_McpConnectionLost()
    {
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer()) { ReconnectFails = true };
        var tool = ReadRecords(source);

        source.Drop();

        await Assert.ThrowsAsync<McpConnectionLostException>(
            async () => await tool.InvokeAsync(SeriesQuery(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_reconnected_connection_that_is_still_dead_surfaces_as_McpConnectionLost()
    {
        // DAB back up but still broken: one retry, then give up rather than
        // loop. The agent turns this into a message that names the connector.
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer()) { StaysDead = true };
        var tool = ReadRecords(source);

        source.Drop();

        await Assert.ThrowsAsync<McpConnectionLostException>(
            async () => await tool.InvokeAsync(SeriesQuery(), TestContext.Current.CancellationToken));
        Assert.Equal(1, source.InvalidateCount); // retried once, not forever
    }

    [Fact]
    public async Task Invalidating_a_generation_that_was_already_replaced_does_nothing()
    {
        // Two circuits failing on the same dead connection: the second one must
        // not discard the replacement the first one just built.
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer());

        await source.InvalidateAsync(1, TestContext.Current.CancellationToken);
        await source.InvalidateAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.InvalidateCount);
        Assert.Equal(2, source.Generation);
    }

    [Fact]
    public async Task A_healthy_connection_is_not_touched()
    {
        var server = new FakeDabMcpServer();
        var source = new FlakyMcpConnectionSource(server);
        var tool = ReadRecords(source);

        await tool.InvokeAsync(SeriesQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(0, source.InvalidateCount);
        Assert.Equal(1, source.Generation);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_mistaken_for_a_dropped_connection()
    {
        // Closing a browser tab cancels the circuit. Tearing down the shared
        // MCP connection because of that would punish every other user.
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer());
        var tool = ReadRecords(source);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tool.InvokeAsync(SeriesQuery(), cts.Token));

        Assert.Equal(0, source.InvalidateCount);
    }
}
