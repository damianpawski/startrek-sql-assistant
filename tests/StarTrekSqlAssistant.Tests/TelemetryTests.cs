using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using StarTrekSqlAssistant.Tests.Fakes;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// What the stack reports about itself. The friendly strings in
/// <see cref="StarTrekAgentService.AskAsync"/> mean a broken stack looks like a
/// working one from the outside - these pin the telemetry that tells the two
/// apart, and pin that the outcome tag actually distinguishes them rather than
/// lumping every failure under "error".
///
/// <see cref="TelemetryRecorder"/> stands in for an exporter; nothing here
/// touches a network or a collector.
/// </summary>
public class TelemetryTests
{
    private static List<ChatMessage> Conversation(StarTrekAgentService agent, string question)
    {
        var messages = agent.CreateConversation();
        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    private static string? Outcome(Activity activity) => activity.GetTagItem(AgentTelemetry.Tags.Outcome)?.ToString();

    [Fact]
    public async Task An_answered_question_is_one_span_and_one_counted_outcome()
    {
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient().ThenSay("Deep Space Nine ran from 1993 to 1999.");
        var agent = TestAgent.Build(client);

        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        var span = recorder.Single(AgentTelemetry.QuestionActivity);
        Assert.Equal(AgentTelemetry.Outcomes.Answered, Outcome(span));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);

        var counted = Assert.Single(recorder.Of("startrek.questions"));
        Assert.Equal(AgentTelemetry.Outcomes.Answered, counted.Tag(AgentTelemetry.Tags.Outcome));
        Assert.Equal(1, counted.Value);

        // The latency of a question is the number the model provider is judged
        // on, so it is recorded on every path, not only the happy one.
        Assert.Single(recorder.Of("startrek.question.duration"));
    }

    [Theory]
    [InlineData("dab_unreachable")]
    [InlineData("timeout")]
    [InlineData("connection_lost")]
    [InlineData("error")]
    [InlineData("no_answer")]
    public async Task Every_failure_the_user_sees_as_a_chat_reply_is_counted_as_that_failure(string expected)
    {
        // Each of these returns an ordinary string to the UI. Without a distinct
        // outcome tag they would all be indistinguishable from an answer.
        using var recorder = new TelemetryRecorder();
        using var client = expected switch
        {
            "timeout" => new ScriptedChatClient().ThenThrow(new TaskCanceledException("timed out")),
            "connection_lost" => new ScriptedChatClient().ThenThrow(new McpConnectionLostException("gone")),
            "error" => new ScriptedChatClient().ThenThrow(new HttpRequestException("Connection refused")),
            "no_answer" => new ScriptedChatClient().ThenSayNothing(),
            _ => new ScriptedChatClient(),
        };

        var agent = expected == "dab_unreachable"
            ? TestAgent.Build(client, new UnreachableToolProvider())
            : TestAgent.Build(client);

        var reply = await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(reply)); // still a chat reply, not a throw
        Assert.Equal(expected, Outcome(recorder.Single(AgentTelemetry.QuestionActivity)));
        Assert.Equal(expected, Assert.Single(recorder.Of("startrek.questions")).Tag(AgentTelemetry.Tags.Outcome));
    }

    [Fact]
    public async Task A_failed_question_is_a_failed_span_even_though_the_chat_reply_is_friendly()
    {
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient().ThenThrow(new HttpRequestException("Connection refused"));
        var agent = TestAgent.Build(client);

        await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        var span = recorder.Single(AgentTelemetry.QuestionActivity);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task A_cancelled_circuit_is_counted_as_cancelled_not_as_an_error()
    {
        // A closed browser tab is not a failed answer, and counting it as one
        // would make the error rate a function of how people browse.
        using var recorder = new TelemetryRecorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = new ScriptedChatClient().ThenSay("never reached");
        var agent = TestAgent.Build(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.AskAsync(Conversation(agent, "anything"), cts.Token));

        Assert.Equal(
            AgentTelemetry.Outcomes.Cancelled,
            Assert.Single(recorder.Of("startrek.questions")).Tag(AgentTelemetry.Tags.Outcome));
    }

    [Fact]
    public async Task Each_tool_the_model_picks_is_named_in_its_own_span_and_counter()
    {
        // Which tool the model chose is the single most useful thing to know
        // about an answer, and it is invisible in the reply itself.
        //
        // Through FlakyMcpConnectionSource, because that is the fake whose tools
        // are wrapped in ReconnectingMcpTool - which is how every tool reaches
        // the model in the real app, and where the instrumentation lives.
        using var recorder = new TelemetryRecorder();
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", select = "title,begin" })
            .ThenSay("Deep Space Nine ran from 1993 to 1999.");

        var tools = new FlakyMcpConnectionSource(server).AsToolProvider(NullLogger.Instance);
        var agent = TestAgent.Build(ChatClientFactory.Wrap(client), tools);
        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        var tool = recorder.Single(AgentTelemetry.ToolActivity);
        Assert.Equal("read_records", tool.GetTagItem(AgentTelemetry.Tags.ToolName)?.ToString());
        Assert.Equal(AgentTelemetry.Outcomes.Ok, Outcome(tool));

        // Nested under the question, so one trace shows the whole answer.
        Assert.Equal(recorder.Single(AgentTelemetry.QuestionActivity).TraceId, tool.TraceId);

        var call = Assert.Single(recorder.Of("startrek.tool.calls"));
        Assert.Equal("read_records", call.Tag(AgentTelemetry.Tags.ToolName));
        Assert.Equal(AgentTelemetry.Outcomes.Ok, call.Tag(AgentTelemetry.Tags.Outcome));
        Assert.Single(recorder.Of("startrek.tool.duration"));
    }

    [Fact]
    public async Task A_tool_call_that_survived_a_DAB_restart_is_counted_as_retried_not_as_ok()
    {
        // The reconnect is invisible to the user and to the model by design, so
        // this counter is the only evidence a `docker compose restart dab`
        // happened at all.
        using var recorder = new TelemetryRecorder();
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer());
        var tool = (AIFunction)(await source.AsToolProvider(NullLogger.Instance).GetToolsAsync(TestContext.Current.CancellationToken))
            .First(t => t.Name == "read_records");

        source.Drop();
        await tool.InvokeAsync(new AIFunctionArguments { ["entity"] = "Series", ["select"] = "title,begin" }, TestContext.Current.CancellationToken);

        var call = Assert.Single(recorder.Of("startrek.tool.calls"));
        Assert.Equal(AgentTelemetry.Outcomes.Retried, call.Tag(AgentTelemetry.Tags.Outcome));
        Assert.Equal("read_records", call.Tag(AgentTelemetry.Tags.ToolName));
    }

    [Fact]
    public async Task A_tool_call_that_could_not_be_recovered_is_counted_as_connection_lost()
    {
        using var recorder = new TelemetryRecorder();
        var source = new FlakyMcpConnectionSource(new FakeDabMcpServer()) { StaysDead = true };
        var tool = (AIFunction)(await source.AsToolProvider(NullLogger.Instance).GetToolsAsync(TestContext.Current.CancellationToken))
            .First(t => t.Name == "read_records");

        source.Drop();
        await Assert.ThrowsAsync<McpConnectionLostException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["entity"] = "Series", ["select"] = "title,begin" }, TestContext.Current.CancellationToken));

        var call = Assert.Single(recorder.Of("startrek.tool.calls"));
        Assert.Equal(AgentTelemetry.Outcomes.ConnectionLost, call.Tag(AgentTelemetry.Tags.Outcome));
        Assert.Equal(ActivityStatusCode.Error, recorder.Single(AgentTelemetry.ToolActivity).Status);
    }

    [Fact]
    public async Task A_question_is_counted_as_in_flight_before_the_model_is_called_and_released_after()
    {
        // Spans only reach an exporter when they end, so a question that is still
        // being answered is invisible in Traces. This counter is exported on the
        // metric interval instead, which is what makes "still thinking" and
        // "never started" distinguishable while it is happening.
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient().ThenSay("42 episodes.");
        var agent = TestAgent.Build(client);

        await agent.AskAsync(Conversation(agent, "How many?"), TestContext.Current.CancellationToken);

        // +1 first, -1 second: the order is the whole point - incrementing after
        // the model call would leave the gauge at zero for exactly the minutes
        // it is meant to cover.
        Assert.Equal([1, -1], recorder.Of("startrek.questions.active").Select(m => m.Value));
    }

    [Fact]
    public async Task A_question_that_fails_still_releases_its_in_flight_count()
    {
        // A gauge that only decrements on success climbs forever on a broken
        // stack and stops meaning anything.
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient().ThenThrow(new HttpRequestException("Connection refused"));
        var agent = TestAgent.Build(client);

        await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        Assert.Equal(0, recorder.Of("startrek.questions.active").Sum(m => m.Value));
    }

    [Fact]
    public async Task The_question_is_logged_when_it_starts_but_never_what_was_typed()
    {
        // The log line is what shows up in the dashboard while the model is still
        // thinking, so it has to be emitted up front - and it has to stay free of
        // message content, which only Telemetry:CaptureMessageContent may reveal.
        const string Secret = "warp-core-diagnostic-phrase";

        var logger = new CapturingLogger<StarTrekAgentService>();
        using var client = new ScriptedChatClient().ThenSay("An answer.");
        var agent = TestAgent.Build(client, logger: logger);

        await agent.AskAsync(Conversation(agent, "A question mentioning " + Secret), TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, m => m.StartsWith("Question received"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Secret));
    }

    [Fact]
    public async Task Nothing_the_user_typed_reaches_the_telemetry_by_default()
    {
        // The privacy default is exactly the kind of thing that regresses
        // silently: Telemetry:CaptureMessageContent defaults to false, and
        // ChatClientFactory.Wrap's own default matches it.
        const string Secret = "warp-core-diagnostic-phrase";

        using var recorder = new TelemetryRecorder();
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", select = "title,begin" })
            .ThenSay("An answer mentioning " + Secret);

        var agent = TestAgent.Build(ChatClientFactory.Wrap(client), server.AsToolProvider());
        await agent.AskAsync(Conversation(agent, "A question mentioning " + Secret), TestContext.Current.CancellationToken);

        var tags = recorder.Activities.SelectMany(a => a.TagObjects).Select(t => t.Value?.ToString());
        Assert.DoesNotContain(tags, value => value?.Contains(Secret) == true);

        var events = recorder.Activities.SelectMany(a => a.Events).Select(e => e.Name + string.Concat(e.Tags));
        Assert.DoesNotContain(events, value => value.Contains(Secret));

        // The question still happened - this is not passing because nothing ran.
        Assert.Equal(AgentTelemetry.Outcomes.Answered, Outcome(recorder.Single(AgentTelemetry.QuestionActivity)));
    }

    [Fact]
    public async Task Turning_capture_on_is_what_puts_the_conversation_on_the_trace()
    {
        // The other half of the switch: a flag that is wired but does nothing
        // would look identical to the test above.
        const string Secret = "warp-core-diagnostic-phrase";

        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient().ThenSay("An answer mentioning " + Secret);
        var agent = TestAgent.Build(ChatClientFactory.Wrap(client, captureMessageContent: true));

        await agent.AskAsync(Conversation(agent, "A question mentioning " + Secret), TestContext.Current.CancellationToken);

        var recorded = recorder.Activities
            .SelectMany(a => a.TagObjects.Select(t => t.Value?.ToString())
                .Concat(a.Events.Select(e => string.Concat(e.Tags.Select(t => t.Value?.ToString())))));

        Assert.Contains(recorded, value => value?.Contains(Secret) == true);
    }
}
