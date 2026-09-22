using Microsoft.Extensions.AI;
using StarTrekSqlAssistant.Tests.Fakes;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The agent's contract with the UI: it seeds the conversation, it never
/// mutates the history it is handed, and every way the stack can break comes
/// back as a chat reply rather than an exception - because an exception here
/// tears down the Blazor circuit and loses the conversation.
/// </summary>
public class StarTrekAgentServiceTests
{
    private static List<ChatMessage> Conversation(StarTrekAgentService agent, string question)
    {
        var messages = agent.CreateConversation();
        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    [Fact]
    public void CreateConversation_seeds_the_system_prompt_and_nothing_else()
    {
        using var client = new ScriptedChatClient();
        var agent = TestAgent.Build(client);

        var conversation = agent.CreateConversation();

        var only = Assert.Single(conversation);
        Assert.Equal(ChatRole.System, only.Role);
        Assert.Equal(StarTrekPrompt.SystemPrompt, only.Text);
    }

    [Fact]
    public async Task AskAsync_returns_the_model_reply()
    {
        using var client = new ScriptedChatClient().ThenSay("Deep Space Nine ran from 1993 to 1999.");
        var agent = TestAgent.Build(client);

        var reply = await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        Assert.Equal("Deep Space Nine ran from 1993 to 1999.", reply);
    }

    [Fact]
    public async Task AskAsync_does_not_mutate_the_conversation_it_is_given()
    {
        // Home.razor owns the history and appends both sides itself; an agent
        // that also appended would double every message in the UI.
        using var client = new ScriptedChatClient().ThenSay("42 episodes.");
        var agent = TestAgent.Build(client);
        var messages = Conversation(agent, "How many?");
        var before = messages.Count;

        await agent.AskAsync(messages, TestContext.Current.CancellationToken);

        Assert.Equal(before, messages.Count);
        Assert.Equal(ChatRole.User, messages[^1].Role);
    }

    [Fact]
    public async Task AskAsync_sends_the_whole_conversation_and_the_tool_list()
    {
        var server = new FakeDabMcpServer();
        using var client = new ScriptedChatClient().ThenSay("ok");
        var agent = TestAgent.Build(client, server.AsToolProvider());
        var messages = Conversation(agent, "Which series premiered in the 1990s?");

        await agent.AskAsync(messages, TestContext.Current.CancellationToken);

        var sent = Assert.Single(client.ReceivedConversations);
        Assert.Equal([ChatRole.System, ChatRole.User], sent.Select(m => m.Role));

        var options = Assert.Single(client.ReceivedOptions);
        Assert.Equal(
            server.Tools.Select(t => t.Name).Order(),
            options!.Tools!.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task AskAsync_says_the_connector_is_not_up_when_DAB_is_unreachable()
    {
        using var client = new ScriptedChatClient();
        var agent = TestAgent.Build(client, new UnreachableToolProvider());

        var reply = await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        Assert.Contains("can't reach the database connector", reply);
        Assert.Equal(0, client.CallCount); // never bothered the model
    }

    [Fact]
    public async Task AskAsync_reports_the_round_cap_when_the_model_produces_no_text()
    {
        // What a tool loop cut off at MaxToolRounds leaves behind: a final
        // message with no answer in it. A blank bubble would look like a bug.
        using var client = new ScriptedChatClient().ThenSayNothing();
        var agent = TestAgent.Build(client);

        var reply = await agent.AskAsync(Conversation(agent, "something hard"), TestContext.Current.CancellationToken);

        Assert.Contains($"{ChatClientFactory.MaxToolRounds} database lookups", reply);
    }

    [Fact]
    public async Task AskAsync_explains_a_provider_timeout_and_names_the_limit()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException with no
        // token cancelled - the one case that must not read as a generic error.
        using var client = new ScriptedChatClient().ThenThrow(new TaskCanceledException("The request was canceled due to timeout."));
        var agent = TestAgent.Build(client, timeoutSeconds: 600);

        var reply = await agent.AskAsync(Conversation(agent, "slow question"), TestContext.Current.CancellationToken);

        Assert.Contains("600 seconds", reply);
        Assert.Contains("TimeoutSeconds", reply);
    }

    [Fact]
    public async Task AskAsync_returns_a_friendly_error_when_the_model_call_fails()
    {
        using var client = new ScriptedChatClient().ThenThrow(new HttpRequestException("Connection refused (localhost:11434)"));
        var agent = TestAgent.Build(client);

        var reply = await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        Assert.Contains("error talking to the model", reply);
    }

    [Fact]
    public async Task AskAsync_names_the_connector_when_the_MCP_connection_is_lost_mid_answer()
    {
        // Regression: a restarted DAB used to land in the generic catch below
        // and tell the user to go and check their Ollama install, which is the
        // wrong thing to go and check. The tool wrapper has already reconnected
        // and retried by the time this exception gets here.
        using var client = new ScriptedChatClient()
            .ThenThrow(new McpConnectionLostException("Tool 'read_records' failed again on a fresh connection."));
        var agent = TestAgent.Build(client);

        var reply = await agent.AskAsync(Conversation(agent, "anything"), TestContext.Current.CancellationToken);

        Assert.Contains("lost the connection to the database connector", reply);
        Assert.DoesNotContain("Ollama", reply);
    }

    [Fact]
    public async Task AskAsync_propagates_a_real_cancellation()
    {
        // A cancelled circuit is not a failed answer: swallowing it here would
        // turn a closed browser tab into a chat bubble no one ever reads.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = new ScriptedChatClient().ThenSay("never reached");
        var agent = TestAgent.Build(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.AskAsync(Conversation(agent, "anything"), cts.Token));
    }
}
