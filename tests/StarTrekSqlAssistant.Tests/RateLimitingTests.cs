using System.Diagnostics;
using Microsoft.Extensions.AI;
using StarTrekSqlAssistant.Tests.Fakes;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The question limits: five a minute across the whole app, one at a time. They
/// exist to keep the local GPU usable - see <see cref="RateLimitOptions"/>.
///
/// Nothing here waits out a real minute. What these pin is the admission logic
/// and its ordering, which is this app's own code; that a sliding window
/// replenishes on schedule is System.Threading.RateLimiting's job.
/// </summary>
public class RateLimitingTests
{
    // ---- QuestionLimiter on its own ----------------------------------------

    [Fact]
    public void Five_questions_a_minute_are_admitted_and_the_sixth_is_not()
    {
        using var limiter = TestAgent.Limited(questionsPerMinute: 5, concurrentQuestions: 1);

        for (var i = 1; i <= 5; i++)
        {
            // Disposed straight away, so the one-at-a-time slot is never what
            // turns a question away - only the per-minute budget can.
            using var admitted = limiter.TryAdmit();
            Assert.True(admitted.IsAdmitted, $"Question {i} of 5 should have been admitted.");
        }

        using var sixth = limiter.TryAdmit();
        Assert.False(sixth.IsAdmitted);
        Assert.Equal(AgentTelemetry.Outcomes.RateLimited, sixth.RejectedAs);
    }

    [Fact]
    public void A_second_question_while_one_is_running_is_turned_away_as_busy()
    {
        using var limiter = TestAgent.Limited(questionsPerMinute: 5, concurrentQuestions: 1);

        using var running = limiter.TryAdmit();
        using var second = limiter.TryAdmit();

        Assert.True(running.IsAdmitted);
        Assert.False(second.IsAdmitted);
        Assert.Equal(AgentTelemetry.Outcomes.Busy, second.RejectedAs);
    }

    [Fact]
    public void The_slot_is_free_again_once_the_running_question_finishes()
    {
        using var limiter = TestAgent.Limited(questionsPerMinute: 5, concurrentQuestions: 1);

        limiter.TryAdmit().Dispose();

        using var next = limiter.TryAdmit();
        Assert.True(next.IsAdmitted);
    }

    [Fact]
    public void A_question_turned_away_as_busy_spends_none_of_the_minutes_budget()
    {
        // The reason the in-flight slot is checked before the budget. A visitor
        // who clicks five more times while a slow local model is still thinking
        // must not come back to find all five of their questions gone.
        using var limiter = TestAgent.Limited(questionsPerMinute: 5, concurrentQuestions: 1);

        using (var running = limiter.TryAdmit())
        {
            for (var i = 0; i < 10; i++)
            {
                using var impatient = limiter.TryAdmit();
                Assert.Equal(AgentTelemetry.Outcomes.Busy, impatient.RejectedAs);
            }
        }

        // One spent by the question that ran; the ten busy ones spent nothing,
        // so four remain.
        for (var i = 1; i <= 4; i++)
        {
            using var admitted = limiter.TryAdmit();
            Assert.True(admitted.IsAdmitted, $"Question {i} of the remaining 4 should have been admitted.");
        }
    }

    [Fact]
    public void A_question_turned_away_by_the_budget_does_not_keep_the_slot()
    {
        // The budget is checked after the slot is taken, so a rejection there has
        // to hand the slot back - otherwise the first rate-limited question would
        // leave every later one "busy" for the rest of the app's life.
        using var limiter = TestAgent.Limited(questionsPerMinute: 1, concurrentQuestions: 1);

        limiter.TryAdmit().Dispose();                        // the one question this minute allows
        using var overBudget = limiter.TryAdmit();
        Assert.Equal(AgentTelemetry.Outcomes.RateLimited, overBudget.RejectedAs);

        using var after = limiter.TryAdmit();
        Assert.Equal(AgentTelemetry.Outcomes.RateLimited, after.RejectedAs); // still the budget, never "busy"
    }

    // ---- Through the agent -------------------------------------------------

    private static List<ChatMessage> Conversation(IStarTrekAgent agent, string question)
    {
        var messages = agent.CreateConversation();
        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    [Fact]
    public async Task A_question_over_the_limit_never_reaches_the_model()
    {
        // The point of the whole thing: a rejected question costs the GPU nothing.
        using var client = new ScriptedChatClient().ThenSay("Deep Space Nine ran from 1993 to 1999.");
        using var limiter = TestAgent.Limited(questionsPerMinute: 1);
        var agent = TestAgent.Build(client, limiter: limiter);

        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);
        var reply = await agent.AskAsync(Conversation(agent, "And Voyager?"), TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Contains("at most 1 questions a minute", reply);
        // "Within", because a sliding window cannot say exactly when a slot frees.
        Assert.Contains("Try again within a minute", reply);
    }

    [Fact]
    public async Task A_question_while_another_is_running_is_told_to_wait_for_it()
    {
        using var client = new ScriptedChatClient().ThenSay("unreachable");
        using var limiter = TestAgent.Limited(questionsPerMinute: 5, concurrentQuestions: 1);
        var agent = TestAgent.Build(client, limiter: limiter);

        // Another circuit's question, still in progress.
        using var running = limiter.TryAdmit();

        var reply = await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        Assert.Equal(0, client.CallCount);
        Assert.Contains("one at a time", reply);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("rate_limited")]
    public async Task A_turned_away_question_is_counted_under_its_own_outcome(string expected)
    {
        // A rejection is a friendly string like every other failure, so without
        // its own outcome a GPU being protected would look, on the dashboard,
        // exactly like a GPU being idle.
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient();
        using var limiter = TestAgent.Limited(questionsPerMinute: 1, concurrentQuestions: 1);
        var agent = TestAgent.Build(client, limiter: limiter);

        // Busy: another question is holding the slot. Rate limited: the one
        // question this minute allows has already been asked and has finished.
        using var held = limiter.TryAdmit();
        if (expected == AgentTelemetry.Outcomes.RateLimited)
        {
            held.Dispose();
        }

        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        var span = recorder.Single(AgentTelemetry.QuestionActivity);
        Assert.Equal(expected, span.GetTagItem(AgentTelemetry.Tags.Outcome)?.ToString());

        var counted = Assert.Single(recorder.Of("startrek.questions"));
        Assert.Equal(expected, counted.Tag(AgentTelemetry.Tags.Outcome));
        Assert.Single(recorder.Of("startrek.question.duration"));
    }

    [Fact]
    public async Task A_turned_away_question_is_never_counted_as_being_answered()
    {
        // startrek.questions.active is the dashboard's only view of a question
        // still in progress. A rejected one never was, so it must not blip it.
        using var recorder = new TelemetryRecorder();
        using var client = new ScriptedChatClient();
        using var limiter = TestAgent.Limited(concurrentQuestions: 1);
        var agent = TestAgent.Build(client, limiter: limiter);

        using var running = limiter.TryAdmit();
        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        Assert.Empty(recorder.Of("startrek.questions.active"));
    }

    [Fact]
    public async Task A_turned_away_question_is_not_logged_as_being_asked()
    {
        // "asking Ollama llama3.1" in the log for a question that never reached
        // Ollama would send whoever reads it looking in the wrong place.
        var logger = new CapturingLogger<StarTrekAgentService>();
        using var client = new ScriptedChatClient();
        using var limiter = TestAgent.Limited(concurrentQuestions: 1);
        var agent = TestAgent.Build(client, logger: logger, limiter: limiter);

        using var running = limiter.TryAdmit();
        await agent.AskAsync(Conversation(agent, "When did DS9 start and end?"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Messages, message => message.Contains("asking", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("turned away", StringComparison.Ordinal));
    }

    // ---- The defaults ------------------------------------------------------

    [Fact]
    public void The_defaults_are_five_a_minute_one_at_a_time()
    {
        // Deliberately tight: this is a local GPU's budget, not a web service's.
        // A change here should be a decision, and this test makes it one.
        var defaults = new RateLimitOptions();

        Assert.Equal(5, defaults.QuestionsPerMinute);
        Assert.Equal(1, defaults.ConcurrentQuestions);
    }
}
