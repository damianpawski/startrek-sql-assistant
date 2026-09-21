using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Decides whether a question may run. Two app-wide limits, both there to keep
/// the local GPU usable - see <see cref="RateLimitOptions"/> for why each one
/// exists and why neither is per visitor.
///
/// This is not ASP.NET's rate-limiting middleware, and cannot be. A question in
/// Blazor Server is a message on a SignalR connection opened once, when the page
/// loaded, so it never passes through the HTTP pipeline at all - middleware
/// would be visible in Program.cs and would limit nothing. The check has to sit
/// where every question actually goes, which is
/// <see cref="StarTrekAgentService.AskAsync"/>. That is also why a rejection is
/// counted: AskAsync is one of the two telemetry seams, so a turned-away
/// question gets the same span and outcome tag as an answered one without a
/// third place having to record it.
/// </summary>
public sealed class QuestionLimiter : IDisposable
{
    private readonly ConcurrencyLimiter _inFlight;
    private readonly SlidingWindowRateLimiter _perMinute;

    public QuestionLimiter(IOptions<RateLimitOptions> options)
    {
        var limits = options.Value;
        QuestionsPerMinute = limits.QuestionsPerMinute;

        _inFlight = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = limits.ConcurrentQuestions,
            // No queue: see RateLimitOptions.ConcurrentQuestions.
            QueueLimit = 0,
        });

        // Sliding rather than fixed, because a fixed window allows twice the
        // limit across a boundary - five at 0:59 and five more at 1:00 is ten
        // in two seconds, which is exactly the burst this is meant to stop.
        //
        // The cost: a sliding window reports no RetryAfter (verified - a
        // FixedWindowRateLimiter does, this does not), so a rejected visitor is
        // told "within a minute" rather than an exact wait. Switching to a fixed
        // window to get the number back would reintroduce the burst; don't.
        _perMinute = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limits.QuestionsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>The configured per-minute cap, for the message a rejected visitor reads.</summary>
    public int QuestionsPerMinute { get; }

    /// <summary>
    /// Admits the question or says why not. Never waits.
    ///
    /// The order of the two checks is deliberate. The in-flight slot is taken
    /// first, so a question turned away because another one is running spends
    /// none of the per-minute budget - otherwise a visitor who clicked twice
    /// while waiting would burn two of their five on answers they never got. If
    /// the budget then refuses, the slot is handed straight back, since nothing
    /// ran. Only an admitted question counts toward the five.
    /// </summary>
    public QuestionAdmission TryAdmit()
    {
        var slot = _inFlight.AttemptAcquire();
        if (!slot.IsAcquired)
        {
            slot.Dispose();
            return QuestionAdmission.Busy;
        }

        using var budget = _perMinute.AttemptAcquire();
        if (!budget.IsAcquired)
        {
            slot.Dispose();
            return QuestionAdmission.RateLimited;
        }

        // The budget lease is released at the end of this method: a window
        // limiter does not return a permit on release - the permit is spent
        // either way - so there is nothing to hold it for. The slot is different,
        // and travels with the admission until the question finishes.
        return QuestionAdmission.Admitted(slot);
    }

    public void Dispose()
    {
        _inFlight.Dispose();
        _perMinute.Dispose();
    }
}

/// <summary>
/// The result of <see cref="QuestionLimiter.TryAdmit"/>. Dispose it when the
/// question is finished: an admitted question is holding the in-flight slot,
/// and disposing is what frees it for the next one.
/// </summary>
public sealed class QuestionAdmission : IDisposable
{
    private readonly RateLimitLease? _slot;

    private QuestionAdmission(RateLimitLease? slot, string? rejectedAs)
    {
        _slot = slot;
        RejectedAs = rejectedAs;
    }

    /// <summary>Another question already holds the in-flight slot.</summary>
    public static QuestionAdmission Busy { get; } = new(null, AgentTelemetry.Outcomes.Busy);

    /// <summary>This minute's budget of questions is spent.</summary>
    public static QuestionAdmission RateLimited { get; } = new(null, AgentTelemetry.Outcomes.RateLimited);

    public static QuestionAdmission Admitted(RateLimitLease slot) => new(slot, null);

    public bool IsAdmitted => RejectedAs is null;

    /// <summary>The telemetry outcome a rejection is recorded as; null when admitted.</summary>
    public string? RejectedAs { get; }

    public void Dispose() => _slot?.Dispose();
}
