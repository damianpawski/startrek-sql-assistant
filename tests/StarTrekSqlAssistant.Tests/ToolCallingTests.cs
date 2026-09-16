using System.Text.Json;
using Microsoft.Extensions.AI;
using StarTrekSqlAssistant.Tests.Fakes;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The tool loop itself, end to end: a scripted model, the app's real
/// UseFunctionInvocation() wrapper, and a fake DAB that enforces the same
/// argument rules the live one does.
///
/// Each test here pins one rule the SystemPrompt asserts. The model is scripted
/// rather than real, so these do not prove a given model obeys the prompt -
/// they prove the rules are real (the fake rejects the shapes DAB rejects), that
/// a rejection reaches the model as a result it can act on rather than as an
/// exception, and that a well-shaped call returns the right rows.
/// </summary>
public class ToolCallingTests
{
    [Fact]
    public async Task A_tool_call_runs_and_its_result_comes_back_to_the_model()
    {
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", filter = "title eq 'Star Trek: Deep Space Nine'" })
            .Then(messages =>
            {
                // The rows DAB returned are in the conversation by now - this is
                // the "feed-the-result-back" half of the loop.
                var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
                Assert.Contains("1993-01-03", result.Result?.ToString());
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "DS9 premiered on 1993-01-03."));
            });

        var agent = TestAgent.BuildWithToolLoop(client, server);
        var reply = await agent.AskAsync(Ask(agent, "When did DS9 premiere?"));

        Assert.Equal("DS9 premiered on 1993-01-03.", reply);
        Assert.Equal("read_records", server.LastCall.Tool);
        Assert.False(server.LastCall.Failed);
    }

    [Theory]
    [InlineData("begin ge '1990-01-01'", "quoted")]
    [InlineData("begin ge 1990-01-01", "bare")]
    public async Task A_date_filter_that_is_not_a_full_unquoted_UTC_timestamp_is_rejected(string filter, string _)
    {
        // "Filters on date columns must use a full UTC timestamp with no quotes"
        // - the prompt's most specific claim about DAB, and the one a model gets
        // wrong most often.
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", filter })
            .ThenSay("done");

        var agent = TestAgent.BuildWithToolLoop(client, server);
        await agent.AskAsync(Ask(agent, "Which series premiered in the 1990s?"));

        Assert.True(server.LastCall.Failed);
        Assert.Contains("full UTC timestamp", server.LastCall.Result);
    }

    [Fact]
    public async Task The_model_can_recover_from_a_rejected_filter_within_the_same_answer()
    {
        // DAB reports a bad filter as an ordinary tool result, not an exception,
        // so the loop keeps going and a second, well-formed call can succeed.
        // This is why MaximumConsecutiveErrorsPerRequest never trips here.
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", filter = "begin ge '1990-01-01'" })
            .ThenCall(messages =>
            {
                var error = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
                Assert.Contains("full UTC timestamp", error.Result?.ToString());
                return ("read_records", new
                {
                    entity = "Series",
                    filter = "begin ge 1990-01-01T00:00:00Z and begin lt 2000-01-01T00:00:00Z",
                });
            })
            .Then(messages =>
            {
                var rows = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
                var titles = Titles(rows.Result!.ToString()!);
                Assert.Equal(["Star Trek: Deep Space Nine", "Star Trek: Voyager"], titles);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join(" and ", titles)));
            });

        var agent = TestAgent.BuildWithToolLoop(client, server);
        var reply = await agent.AskAsync(Ask(agent, "Which series premiered in the 1990s?"));

        Assert.Equal(2, server.Calls.Count);
        Assert.True(server.Calls[0].Failed);
        Assert.False(server.Calls[1].Failed);
        Assert.Contains("Voyager", reply);
    }

    [Fact]
    public async Task A_still_airing_show_is_found_with_end_eq_null()
    {
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", filter = "end eq null", select = "title" })
            .Then(messages =>
            {
                var rows = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, Titles(rows.Result!.ToString()!).Single()));
            });

        var agent = TestAgent.BuildWithToolLoop(client, server);
        var reply = await agent.AskAsync(Ask(agent, "Which series is still airing?"));

        Assert.False(server.LastCall.Failed);
        Assert.Equal("Star Trek: Strange New Worlds", reply);
    }

    [Fact]
    public async Task A_select_list_with_spaces_after_the_commas_is_rejected()
    {
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("read_records", new { entity = "Series", select = "title, begin, end" })
            .ThenSay("done");

        var agent = TestAgent.BuildWithToolLoop(client, server);
        await agent.AskAsync(Ask(agent, "List the series with their dates."));

        Assert.True(server.LastCall.Failed);
        Assert.Contains("no spaces", server.LastCall.Result);
    }

    [Fact]
    public async Task Aggregate_records_rejects_an_expression_and_a_non_numeric_column()
    {
        // "field must be one column name, never an expression such as
        // 'end - begin', and avg/sum/min/max need a numeric column."
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("aggregate_records", new { entity = "Series", function = "avg", field = "end - begin" })
            .ThenCall("aggregate_records", new { entity = "Series", function = "max", field = "begin" })
            .ThenCall("aggregate_records", new { entity = "Episode", function = "count", field = "episode_id", filter = "series_id eq 3" })
            .Then(messages =>
            {
                var rows = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, rows.Result!.ToString()!));
            });

        var agent = TestAgent.BuildWithToolLoop(client, server);
        var reply = await agent.AskAsync(Ask(agent, "How many DS9 episodes are there?"));

        Assert.Contains("Expressions are not supported", server.Calls[0].Result);
        Assert.Contains("not numeric", server.Calls[1].Result);
        Assert.False(server.Calls[2].Failed);
        Assert.Contains("\"count\":2", reply);
    }

    [Fact]
    public async Task Describe_entities_returns_no_fields_which_is_why_the_prompt_carries_the_schema()
    {
        // The DAB behaviour the whole SystemPrompt exists to work around: a
        // model that follows DAB's own "STEP 1: describe_entities" advice learns
        // nothing about columns.
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient()
            .ThenCall("describe_entities", new { nameOnly = false })
            .Then(messages =>
            {
                var described = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
                using var json = JsonDocument.Parse(described.Result!.ToString()!);
                foreach (var entity in json.RootElement.GetProperty("entities").EnumerateArray())
                {
                    Assert.Empty(entity.GetProperty("fields").EnumerateArray());
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "no fields listed"));
            });

        var agent = TestAgent.BuildWithToolLoop(client, server);
        await agent.AskAsync(Ask(agent, "What can you look up?"));

        Assert.False(server.LastCall.Failed);
    }

    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_cut_off_at_MaxToolRounds()
    {
        // Before this cap a local model retrying a malformed date filter ran for
        // over 15 minutes: the provider timeout is per call, and a rejected
        // filter is a normal result, so nothing else bounds the loop.
        var server = new FakeDabMcpServer();
        var client = new ScriptedChatClient();
        for (var i = 0; i < ChatClientFactory.MaxToolRounds + 5; i++)
        {
            client.ThenCall("read_records", new { entity = "Series", filter = "begin ge '1990-01-01'" });
        }

        var agent = TestAgent.BuildWithToolLoop(client, server);
        var reply = await agent.AskAsync(Ask(agent, "Which series premiered in the 1990s?"));

        // MaximumIterationsPerRequest counts tool-executing rounds: the model is
        // asked once more after the last one, and that reply's tool calls are no
        // longer run. So MaxToolRounds tool calls reach DAB, and the answer the
        // user gets is the agent's "I couldn't work that out" rather than blank.
        Assert.Equal(ChatClientFactory.MaxToolRounds, server.Calls.Count);
        Assert.Equal(ChatClientFactory.MaxToolRounds + 1, client.CallCount);
        Assert.Contains($"{ChatClientFactory.MaxToolRounds} database lookups", reply);
        client.Dispose();
    }

    private static List<ChatMessage> Ask(StarTrekAgentService agent, string question)
    {
        var messages = agent.CreateConversation();
        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    private static string[] Titles(string json) =>
        [.. JsonDocument.Parse(json).RootElement.GetProperty("value").EnumerateArray()
            .Select(row => row.GetProperty("title").GetString()!)];
}
