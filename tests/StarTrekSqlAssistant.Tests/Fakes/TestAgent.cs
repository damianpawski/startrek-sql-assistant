using Microsoft.Extensions.AI;
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

/// <summary>Composes the real <see cref="StarTrekAgentService"/> over test doubles.</summary>
public static class TestAgent
{
    /// <summary>The agent with a scripted model and whatever tool source is given.</summary>
    public static StarTrekAgentService Build(
        IChatClient chatClient,
        IMcpToolProvider? toolProvider = null,
        string backendDescription = "Stub",
        int timeoutSeconds = 30) =>
        new(new StubChatClientFactory(chatClient, backendDescription, timeoutSeconds),
            toolProvider ?? new FakeDabMcpServer().AsToolProvider(),
            NullLogger<StarTrekAgentService>.Instance);

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
