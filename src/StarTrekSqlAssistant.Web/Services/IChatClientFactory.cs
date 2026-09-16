namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Builds the chat backend the agent talks to. An interface rather than a
/// static call so a test can hand the agent a scripted <c>IChatClient</c>
/// instead of reaching a real provider.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Creates the client for whichever provider <see cref="ModelOptions.Provider"/>
    /// selects. Throws if the selected provider is misconfigured (unknown
    /// provider, missing API key) - callers resolve this at startup so that
    /// failure lands in the startup log.
    /// </summary>
    ChatBackend Create();
}
