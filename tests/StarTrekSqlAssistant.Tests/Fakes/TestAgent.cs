using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests.Fakes;

/// <summary>Hands the agent a ready-made client instead of building a real one.</summary>
public sealed class StubChatClientFactory(IChatClient client, string description = "Stub", int timeoutSeconds = 30)
    : IChatClientFactory
{
    public ChatBackend Create() =>
        new(client, description, TimeSpan.FromSeconds(timeoutSeconds), OwnedDisposable: null);
}

/// <summary>Stands in for DAB being down - the case the UI has a dedicated reply for.</summary>
public sealed class UnreachableToolProvider(Exception? failure = null) : IMcpToolProvider
{
    private readonly Exception _failure = failure ?? new HttpRequestException("Connection refused (localhost:5000)");

    public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<AITool>>(_failure);
}

/// <summary>
/// Captures what the agent logged. The agent logs a line when a question starts
/// - the only evidence a still-running question leaves anywhere - so what that
/// line does and does not contain is worth asserting on.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// <summary>Composes the real <see cref="StarTrekAgentService"/> over test doubles.</summary>
public static class TestAgent
{
    /// <summary>The agent with a scripted model and whatever tool source is given.</summary>
    public static StarTrekAgentService Build(
        IChatClient chatClient,
        IMcpToolProvider? toolProvider = null,
        string backendDescription = "Stub",
        int timeoutSeconds = 30,
        ILogger<StarTrekAgentService>? logger = null) =>
        new(new StubChatClientFactory(chatClient, backendDescription, timeoutSeconds),
            toolProvider ?? new FakeDabMcpServer().AsToolProvider(),
            logger ?? NullLogger<StarTrekAgentService>.Instance);

    /// <summary>
    /// The agent with the app's real tool-calling loop around the scripted
    /// model, so tool calls the script emits are actually executed against the
    /// fake DAB server and their results fed back.
    /// </summary>
    public static StarTrekAgentService BuildWithToolLoop(
        ScriptedChatClient chatClient,
        FakeDabMcpServer server) =>
        Build(ChatClientFactory.Wrap(chatClient), server.AsToolProvider());
}
