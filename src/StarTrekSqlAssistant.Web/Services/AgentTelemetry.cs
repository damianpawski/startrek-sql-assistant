using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// The app's own <see cref="ActivitySource"/> and <see cref="Meter"/>, and the
/// instruments hung off them. One file owns every telemetry name so nothing
/// else in the app hard-codes one - <c>Program.cs</c> subscribes by the same
/// constants the instrumented code publishes under, and a rename cannot leave
/// a dashboard silently empty.
///
/// There are exactly two instrumented seams, and both already existed for other
/// reasons:
///
/// <list type="bullet">
/// <item><see cref="StarTrekAgentService.AskAsync"/> is the only way a question
/// enters the system, and it already sorts every failure into its own catch
/// block - those classifications become the <c>outcome</c> tag, which is what
/// makes the friendly-string failures countable instead of invisible.</item>
/// <item><see cref="ReconnectingMcpTool"/> already wraps every MCP tool, so it
/// sees every tool call, its retry, and its failure without a second wrapper.</item>
/// </list>
///
/// The model call itself is not instrumented here: Microsoft.Extensions.AI's
/// <c>UseOpenTelemetry()</c> (applied in <see cref="ChatClientFactory.Wrap"/>)
/// emits the standard <c>gen_ai.*</c> span with model name, duration and token
/// counts, for whichever provider is configured.
/// </summary>
public static class AgentTelemetry
{
    /// <summary>
    /// Name of both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.
    /// Microsoft.Extensions.AI's chat instrumentation is pointed at this name
    /// too, so one <c>AddSource</c>/<c>AddMeter</c> pair covers the whole app.
    /// </summary>
    public const string Name = "StarTrekSqlAssistant";

    /// <summary>One question, from the chat box to the reply. Parent of the model and tool spans.</summary>
    public const string QuestionActivity = "ask_question";

    /// <summary>One MCP tool invocation, including a reconnect and retry if there was one.</summary>
    public const string ToolActivity = "mcp_tool";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Questions asked, tagged <c>outcome</c> - see <see cref="Outcomes"/>.</summary>
    public static readonly Counter<long> Questions =
        Meter.CreateCounter<long>("startrek.questions", "{question}", "Questions asked, by outcome.");

    /// <summary>End-to-end question latency in seconds, tagged <c>outcome</c>.</summary>
    public static readonly Histogram<double> QuestionDuration =
        Meter.CreateHistogram<double>("startrek.question.duration", "s", "How long a question took, by outcome.");

    /// <summary>
    /// Questions being answered right now. Unlike a span, which only reaches the
    /// exporter once it ends, an UpDownCounter is exported on the metric interval
    /// - so a question that is still thinking is visible as a live 1 rather than
    /// as nothing at all. A local thinking model can take minutes, and "is it
    /// working or is it wedged" was otherwise a question only `curl`ing Ollama
    /// could answer.
    /// </summary>
    public static readonly UpDownCounter<long> QuestionsActive =
        Meter.CreateUpDownCounter<long>("startrek.questions.active", "{question}", "Questions being answered right now.");

    /// <summary>MCP tool calls, tagged <c>tool.name</c> and <c>outcome</c>. Which tool the model actually picked.</summary>
    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("startrek.tool.calls", "{call}", "MCP tool calls, by tool and outcome.");

    /// <summary>MCP tool latency in seconds, tagged <c>tool.name</c> and <c>outcome</c>.</summary>
    public static readonly Histogram<double> ToolCallDuration =
        Meter.CreateHistogram<double>("startrek.tool.duration", "s", "How long an MCP tool call took, by tool and outcome.");

    /// <summary>
    /// Values of the <c>outcome</c> tag. The question outcomes are one per
    /// return path in <see cref="StarTrekAgentService.AskAsync"/>; keep them in
    /// step with it, since an unrecorded path is a question that vanishes from
    /// the counters rather than one that shows up as an error.
    /// </summary>
    public static class Outcomes
    {
        // Question outcomes.
        public const string Answered = "answered";
        public const string NoAnswer = "no_answer";
        public const string DabUnreachable = "dab_unreachable";
        public const string Timeout = "timeout";
        public const string Error = "error";
        public const string Cancelled = "cancelled";

        // Turned away by QuestionLimiter before reaching the model. Two outcomes
        // rather than one because they call for different responses: "busy"
        // means wait a moment, "rate_limited" means the minute's budget is gone.
        public const string Busy = "busy";
        public const string RateLimited = "rate_limited";

        // Tool outcomes. ConnectionLost is shared: it is what the tool throws
        // and what the question it was part of ends as.
        public const string Ok = "ok";
        public const string Retried = "retried";
        public const string ConnectionLost = "connection_lost";
    }

    /// <summary>Tag keys, so the emitting and the querying side can't drift apart.</summary>
    public static class Tags
    {
        public const string Outcome = "outcome";
        public const string ToolName = "tool.name";
        public const string Generation = "mcp.connection.generation";
        public const string ToolCount = "startrek.tool_count";
        public const string QuestionLength = "startrek.question.length";
    }
}
