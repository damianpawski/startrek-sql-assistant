using Bunit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using StarTrekSqlAssistant.Web.Components.Pages;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The chat page, rendered against a stub agent - no Ollama, no DAB, no SQL
/// Server. This is what the IStarTrekAgent seam buys: the UI's own rules (the
/// history it owns, the thinking state, the empty-state panel) are testable on
/// their own.
/// </summary>
public class HomePageTests : BunitContext
{
    /// <summary>Records what the page asked, and replies with whatever it is told to.</summary>
    private sealed class StubAgent(string reply = "Answer.") : IStarTrekAgent
    {
        public List<IReadOnlyList<ChatMessage>> Asked { get; } = [];

        public List<ChatMessage> CreateConversation() =>
            [new ChatMessage(ChatRole.System, StarTrekPrompt.SystemPrompt)];

        public Task<string> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Asked.Add([.. messages]);
            return Task.FromResult(reply);
        }
    }

    private StubAgent Register(string reply = "Answer.")
    {
        var agent = new StubAgent(reply);
        Services.AddSingleton<IStarTrekAgent>(agent);
        JSInterop.Mode = JSRuntimeMode.Loose; // the only interop is the scroll helper
        return agent;
    }

    [Fact]
    public void The_empty_state_offers_sample_questions_and_says_what_is_not_in_the_database()
    {
        Register();

        var page = Render<Home>();

        Assert.NotEmpty(page.FindAll(".sample-chip"));
        Assert.Contains("Not in this database", page.Markup);
        Assert.Empty(page.FindAll(".bubble-row"));
    }

    [Fact]
    public void Asking_a_question_shows_both_the_question_and_the_answer()
    {
        Register("Deep Space Nine ran from 1993 to 1999.");
        var page = Render<Home>();

        page.Find("input").Input("When did DS9 start and end?");
        page.Find("form").Submit();

        var bubbles = page.FindAll(".bubble-row");
        Assert.Equal(2, bubbles.Count);
        Assert.Contains("When did DS9 start and end?", bubbles[0].TextContent);
        Assert.Contains("1993 to 1999", bubbles[1].TextContent);
    }

    [Fact]
    public void The_page_sends_the_agent_the_system_prompt_and_the_question()
    {
        // Home.razor owns the conversation: it seeds it from the agent and
        // appends both sides itself, so what arrives must be system-then-user.
        var agent = Register();
        var page = Render<Home>();

        page.Find("input").Input("How many movies are there?");
        page.Find("form").Submit();

        var sent = Assert.Single(agent.Asked);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal(StarTrekPrompt.SystemPrompt, sent[0].Text);
        Assert.Equal("How many movies are there?", sent[^1].Text);
    }

    [Fact]
    public void A_second_question_carries_the_first_exchange_with_it()
    {
        var agent = Register();
        var page = Render<Home>();

        page.Find("input").Input("First question?");
        page.Find("form").Submit();
        page.Find("input").Input("And a follow-up?");
        page.Find("form").Submit();

        Assert.Equal(2, agent.Asked.Count);
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            agent.Asked[1].Select(m => m.Role));
    }

    [Fact]
    public void A_sample_chip_asks_its_own_question()
    {
        var agent = Register();
        var page = Render<Home>();
        var chip = page.FindAll(".sample-chip")[0];
        var question = chip.TextContent.Trim();

        chip.Click();

        Assert.Equal(question, Assert.Single(agent.Asked)[^1].Text);
    }

    [Fact]
    public void Blank_input_is_ignored()
    {
        var agent = Register();
        var page = Render<Home>();

        page.Find("input").Input("   ");
        page.Find("form").Submit();

        Assert.Empty(agent.Asked);
        Assert.Empty(page.FindAll(".bubble-row"));
    }

    [Fact]
    public void While_an_answer_is_pending_the_input_is_disabled_and_a_thinking_bubble_shows()
    {
        // The guard that stops a second question racing the first - and the only
        // feedback the user gets while a local model thinks for a minute.
        var gate = new TaskCompletionSource<string>();
        Services.AddSingleton<IStarTrekAgent>(new GatedAgent(gate.Task));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var page = Render<Home>();

        page.Find("input").Input("Something slow.");
        page.Find("form").Submit();

        Assert.NotNull(page.Find("input").GetAttribute("disabled"));
        Assert.NotEmpty(page.FindAll(".bubble-thinking"));

        gate.SetResult("Done.");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".bubble-thinking")));
        Assert.Null(page.Find("input").GetAttribute("disabled"));
    }

    private sealed class GatedAgent(Task<string> reply) : IStarTrekAgent
    {
        public List<ChatMessage> CreateConversation() =>
            [new ChatMessage(ChatRole.System, StarTrekPrompt.SystemPrompt)];

        public Task<string> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
            reply;
    }
}
