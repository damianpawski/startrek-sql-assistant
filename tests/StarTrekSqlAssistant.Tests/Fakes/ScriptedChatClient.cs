using Microsoft.Extensions.AI;

namespace StarTrekSqlAssistant.Tests.Fakes;

/// <summary>
/// An <see cref="IChatClient"/> that plays a script instead of reaching a
/// model: one entry per round trip, each one free to inspect the conversation
/// it was handed (including the tool results fed back by
/// FunctionInvokingChatClient) and answer with text, a tool call, or an
/// exception.
///
/// This is the whole point of the IChatClientFactory seam - every assertion in
/// this suite about what the agent does with a model's reply runs through here,
/// with no Ollama, no API key and no network.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<IEnumerable<ChatMessage>, ChatResponse>> _script = new();

    /// <summary>The conversation handed to each call, oldest first.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedConversations { get; } = [];

    /// <summary>The options handed to each call, oldest first.</summary>
    public List<ChatOptions?> ReceivedOptions { get; } = [];

    public int CallCount => ReceivedConversations.Count;

    /// <summary>Replies with plain text, as a model does once it has its answer.</summary>
    public ScriptedChatClient ThenSay(string text) =>
        Then(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    /// <summary>Replies with a tool call; the wrapper runs the tool and calls back.</summary>
    public ScriptedChatClient ThenCall(string toolName, object arguments) =>
        Then(_ => ToolCall(toolName, arguments));

    /// <summary>Replies with a tool call whose arguments depend on what came back last time.</summary>
    public ScriptedChatClient ThenCall(Func<IEnumerable<ChatMessage>, (string Tool, object Arguments)> choose) =>
        Then(messages =>
        {
            var (tool, arguments) = choose(messages);
            return ToolCall(tool, arguments);
        });

    /// <summary>Replies with a message that has no text at all - what a cut-off tool loop leaves behind.</summary>
    public ScriptedChatClient ThenSayNothing() =>
        Then(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

    public ScriptedChatClient ThenThrow(Exception exception) =>
        Then(_ => throw exception);

    public ScriptedChatClient Then(Func<IEnumerable<ChatMessage>, ChatResponse> turn)
    {
        _script.Enqueue(turn);
        return this;
    }

    /// <summary>Everything the model said across the whole run, for asserting on tool arguments.</summary>
    public static ChatResponse ToolCall(string toolName, object arguments)
    {
        var args = arguments as IDictionary<string, object?>
            ?? arguments.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(arguments));

        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, args)]));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReceivedConversations.Add([.. messages]);
        ReceivedOptions.Add(options);

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"ScriptedChatClient ran out of script on call {CallCount}. " +
                "Add another Then... step, or the code under test is looping more than expected.");
        }

        return Task.FromResult(_script.Dequeue()(messages));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The app never streams; nothing should call this.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
