using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// What <see cref="ChatClientFactory"/> hands back: the client itself, plus the
/// bits the agent needs for logging and error messages, plus anything it has to
/// dispose. <paramref name="OwnedDisposable"/> is non-null only for providers
/// where this factory constructs the transport (Ollama's HttpClient); the
/// hosted SDKs manage their own.
/// </summary>
public sealed record ChatBackend(
    IChatClient Client,
    string Description,
    TimeSpan Timeout,
    IDisposable? OwnedDisposable);

/// <summary>
/// Builds the <see cref="IChatClient"/> the agent talks to, for whichever
/// provider <see cref="ModelOptions.Provider"/> selects.
///
/// This is the only place in the app that knows a specific model vendor exists.
/// Everything downstream - the MCP tool list, the automatic tool-calling loop,
/// the chat UI - works against the IChatClient abstraction, which is why adding
/// a provider means adding a case here and nothing else.
///
/// Injected as <see cref="IChatClientFactory"/> rather than called statically,
/// so tests can hand the agent a scripted client instead of a real provider.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly ModelOptions _model;
    private readonly OllamaOptions _ollama;
    private readonly OpenAIOptions _openAi;
    private readonly AnthropicOptions _anthropic;

    public ChatClientFactory(
        IOptions<ModelOptions> model,
        IOptions<OllamaOptions> ollama,
        IOptions<OpenAIOptions> openAi,
        IOptions<AnthropicOptions> anthropic)
    {
        _model = model.Value;
        _ollama = ollama.Value;
        _openAi = openAi.Value;
        _anthropic = anthropic.Value;
    }

    public ChatBackend Create()
        => _model.Provider switch
        {
            ModelProvider.Ollama => CreateOllama(_ollama),
            ModelProvider.OpenAI => CreateOpenAI(_openAi),
            ModelProvider.Anthropic => CreateAnthropic(_anthropic),
            _ => throw new InvalidOperationException(
                $"Unknown Model:Provider '{_model.Provider}'. Valid values: " +
                string.Join(", ", Enum.GetNames<ModelProvider>())),
        };

    private static ChatBackend CreateOllama(OllamaOptions options)
    {
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // Own the HttpClient so its timeout can be raised. OllamaApiClient's own
        // (Uri, model) constructor builds one internally with HttpClient's 100s
        // default, which silently cancels a slow local model mid-answer and
        // surfaces in the UI as a generic "I hit an error" with no clue why.
        var http = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint),
            Timeout = timeout,
        };

        var client = Wrap(new OllamaApiClient(http, options.Model));
        return new ChatBackend(client, $"Ollama {options.Model} at {options.Endpoint}", timeout, http);
    }

    private static ChatBackend CreateOpenAI(OpenAIOptions options)
    {
        RequireApiKey(options.ApiKey, "OpenAI", "OpenAI:ApiKey", "OpenAI__ApiKey");

        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }

        var openAi = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
        var client = Wrap(openAi.GetChatClient(options.Model).AsIChatClient());

        var where = string.IsNullOrWhiteSpace(options.Endpoint) ? "api.openai.com" : options.Endpoint;
        return new ChatBackend(
            client,
            $"OpenAI {options.Model} at {where}",
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            OwnedDisposable: null);
    }

    private static ChatBackend CreateAnthropic(AnthropicOptions options)
    {
        RequireApiKey(options.ApiKey, "Anthropic", "Anthropic:ApiKey", "Anthropic__ApiKey");

        var anthropic = new AnthropicClient
        {
            ApiKey = options.ApiKey,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        var client = Wrap(anthropic.AsIChatClient(options.Model, options.MaxOutputTokens));
        return new ChatBackend(
            client,
            $"Anthropic {options.Model}",
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            OwnedDisposable: null);
    }

    /// <summary>
    /// Model round trips allowed for one question, including the first. Every
    /// sample question answers in one or two tool calls; six leaves room to
    /// recover from a bad argument or two.
    /// </summary>
    public const int MaxToolRounds = 6;

    /// <summary>
    /// UseFunctionInvocation() is what runs the pick-a-tool / call-it /
    /// feed-the-result-back loop. Every provider gets the same wrapper, so
    /// swapping backends never changes how tools are executed.
    ///
    /// The library default is 40 rounds, and nothing else bounds a question:
    /// the provider timeout applies per model call, and DAB reports a bad
    /// filter as an ordinary tool result rather than an exception, so
    /// MaximumConsecutiveErrorsPerRequest never trips. A local model that kept
    /// retrying a malformed date filter ran for over 15 minutes before this cap.
    ///
    /// Public so tests exercise the same wrapper the app runs, rather than a
    /// hand-rolled stand-in for it.
    /// </summary>
    public static IChatClient Wrap(IChatClient inner) =>
        new ChatClientBuilder(inner)
            .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = MaxToolRounds)
            .Build();

    private static void RequireApiKey(string key, string provider, string secretPath, string envVar)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"Model:Provider is set to {provider} but no API key was found. Set it with " +
                $"`dotnet user-secrets set \"{secretPath}\" \"...\"` for local runs, or the " +
                $"{envVar} environment variable in Docker. Never put it in appsettings.json.");
        }
    }
}
