using Microsoft.Extensions.Options;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// Swapping the chat backend is meant to be a config switch, not a code change,
/// and Program.cs resolves the agent at startup so a misconfigured provider
/// fails there with a readable message instead of inside a Blazor component.
/// These tests pin both halves of that.
/// </summary>
public class ChatClientFactoryTests
{
    private static ChatClientFactory Factory(
        ModelProvider provider,
        OllamaOptions? ollama = null,
        OpenAIOptions? openAi = null,
        AnthropicOptions? anthropic = null,
        TelemetryOptions? telemetry = null) =>
        new(Options.Create(new ModelOptions { Provider = provider }),
            Options.Create(ollama ?? new OllamaOptions()),
            Options.Create(openAi ?? new OpenAIOptions()),
            Options.Create(anthropic ?? new AnthropicOptions()),
            Options.Create(telemetry ?? new TelemetryOptions()));

    [Fact]
    public void Ollama_is_the_default_so_a_clone_runs_locally_with_no_credentials()
    {
        Assert.Equal(ModelProvider.Ollama, new ModelOptions().Provider);
    }

    [Fact]
    public void Ollama_uses_the_configured_endpoint_model_and_timeout()
    {
        // The timeout matters: HttpClient's 100s default silently cancels a slow
        // local model mid-answer, which surfaced as a generic "I hit an error".
        var options = new OllamaOptions { Endpoint = "http://host.docker.internal:11434", Model = "llama3.1", TimeoutSeconds = 600 };

        var backend = Factory(ModelProvider.Ollama, ollama: options).Create();

        Assert.Equal(TimeSpan.FromSeconds(600), backend.Timeout);
        Assert.Contains("llama3.1", backend.Description);
        Assert.Contains("host.docker.internal:11434", backend.Description);
        Assert.NotNull(backend.OwnedDisposable); // this factory built the HttpClient, so it must dispose it
        backend.OwnedDisposable.Dispose();
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI, "OpenAI__ApiKey")]
    [InlineData(ModelProvider.Anthropic, "Anthropic__ApiKey")]
    public void A_hosted_provider_with_no_API_key_fails_with_a_message_that_says_where_to_put_one(
        ModelProvider provider, string environmentVariable)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Factory(provider).Create());

        Assert.Contains("user-secrets", error.Message);
        Assert.Contains(environmentVariable, error.Message);
        Assert.Contains("Never put it in appsettings.json", error.Message);
    }

    [Fact]
    public void An_unknown_provider_names_the_valid_values()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Factory((ModelProvider)99).Create());

        Assert.Contains("Ollama", error.Message);
        Assert.Contains("OpenAI", error.Message);
        Assert.Contains("Anthropic", error.Message);
    }

    [Fact]
    public void A_configured_hosted_provider_is_built_and_described()
    {
        // No network: constructing the client does not call the API.
        var backend = Factory(ModelProvider.Anthropic,
            anthropic: new AnthropicOptions { ApiKey = "sk-ant-not-a-real-key", Model = "claude-haiku-4-5" }).Create();

        Assert.Contains("claude-haiku-4-5", backend.Description);
        Assert.Null(backend.OwnedDisposable); // the SDK owns its own transport
    }

    [Fact]
    public void An_OpenAI_compatible_endpoint_is_reflected_in_the_description()
    {
        var backend = Factory(ModelProvider.OpenAI,
            openAi: new OpenAIOptions { ApiKey = "sk-not-a-real-key", Endpoint = "http://localhost:1234/v1", Model = "gpt-4o-mini" }).Create();

        Assert.Contains("localhost:1234", backend.Description);
    }
}
