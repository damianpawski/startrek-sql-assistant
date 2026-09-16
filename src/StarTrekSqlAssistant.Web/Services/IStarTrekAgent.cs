using Microsoft.Extensions.AI;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// The agent as the UI sees it: start a conversation, ask a question, get a
/// reply. The seam exists so <c>Home.razor</c> can be exercised - and the
/// agent's behaviour pinned - without a live Ollama or a live DAB behind it.
/// </summary>
public interface IStarTrekAgent
{
    /// <summary>Starts a new conversation, seeded with the system prompt.</summary>
    List<ChatMessage> CreateConversation();

    /// <summary>
    /// Sends the full conversation (including the newest user message) to the
    /// model and returns its reply. Must not mutate <paramref name="messages"/> -
    /// the caller owns the history and appends both sides itself.
    /// </summary>
    Task<string> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}
