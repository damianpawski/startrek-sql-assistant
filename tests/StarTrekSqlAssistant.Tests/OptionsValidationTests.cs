using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests;

/// <summary>
/// The settings validators, and above all the rule that makes them tractable
/// here: a provider's section is only validated when that provider is the one
/// selected.
///
/// Without that rule the obvious implementation - mark every ApiKey [Required]
/// and call ValidateDataAnnotations - refuses to start the configuration this
/// repo ships, where Model:Provider is Ollama and the OpenAI and Anthropic
/// sections are correctly empty. The Skip tests below are the ones that would
/// catch someone "simplifying" it back to that.
/// </summary>
public class OptionsValidationTests
{
    private static IOptions<ModelOptions> Using(ModelProvider provider) =>
        Options.Create(new ModelOptions { Provider = provider });

    // ---- The conditionality, from both sides -------------------------------

    [Theory]
    [InlineData(ModelProvider.Ollama)]
    [InlineData(ModelProvider.Anthropic)]
    public void An_unselected_OpenAI_section_is_not_validated_at_all(ModelProvider selected)
    {
        // Empty on every count - no key, no model, a nonsense timeout - and
        // still Skip, because nothing will read it.
        var result = new OpenAIOptionsValidator(Using(selected))
            .Validate(null, new OpenAIOptions { ApiKey = "", Model = "", TimeoutSeconds = 0 });

        Assert.True(result.Skipped);
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(ModelProvider.Ollama)]
    [InlineData(ModelProvider.OpenAI)]
    public void An_unselected_Anthropic_section_is_not_validated_at_all(ModelProvider selected)
    {
        var result = new AnthropicOptionsValidator(Using(selected))
            .Validate(null, new AnthropicOptions { ApiKey = "", MaxOutputTokens = 0 });

        Assert.True(result.Skipped);
    }

    [Fact]
    public void An_unselected_Ollama_section_is_not_validated_at_all()
    {
        var result = new OllamaOptionsValidator(Using(ModelProvider.Anthropic))
            .Validate(null, new OllamaOptions { Endpoint = "not-a-url", Model = "" });

        Assert.True(result.Skipped);
    }

    [Fact]
    public void The_selected_provider_is_validated()
    {
        var result = new OpenAIOptionsValidator(Using(ModelProvider.OpenAI))
            .Validate(null, new OpenAIOptions { ApiKey = "" });

        Assert.True(result.Failed);
    }

    // ---- The messages ------------------------------------------------------

    [Theory]
    [InlineData(ModelProvider.OpenAI, "OpenAI__ApiKey")]
    [InlineData(ModelProvider.Anthropic, "Anthropic__ApiKey")]
    public void A_missing_key_names_both_ways_to_supply_it(ModelProvider provider, string environmentVariable)
    {
        // Same wording as ChatClientFactory's own guard: whichever one a person
        // hits first, the instructions should not differ.
        var failures = provider == ModelProvider.OpenAI
            ? new OpenAIOptionsValidator(Using(provider)).Validate(null, new OpenAIOptions()).Failures
            : new AnthropicOptionsValidator(Using(provider)).Validate(null, new AnthropicOptions()).Failures;

        var message = Assert.Single(failures!);
        Assert.Contains("user-secrets", message);
        Assert.Contains(environmentVariable, message);
        Assert.Contains("Never put it in appsettings.json", message);
    }

    [Fact]
    public void A_malformed_endpoint_names_the_setting_and_the_value()
    {
        var result = new DabOptionsValidator().Validate(null, new DabOptions { McpEndpoint = "localhost:5000/mcp" });

        var message = Assert.Single(result.Failures!);
        Assert.Contains("Dab:McpEndpoint", message);
        Assert.Contains("localhost:5000/mcp", message);
    }

    [Fact]
    public void A_non_http_endpoint_is_rejected()
    {
        // Parses as a Uri, so only a scheme check catches it - and every consumer
        // here is an HttpClient.
        var result = new DabOptionsValidator().Validate(null, new DabOptions { McpEndpoint = "ftp://host/mcp" });

        Assert.Contains("ftp", Assert.Single(result.Failures!));
    }

    [Fact]
    public void A_provider_that_is_no_member_of_the_enum_is_rejected()
    {
        // Model__Provider=99 binds cleanly and would otherwise fall through
        // ChatClientFactory's switch at the first question.
        var result = new ModelOptionsValidator().Validate(null, new ModelOptions { Provider = (ModelProvider)99 });

        var message = Assert.Single(result.Failures!);
        Assert.Contains("99", message);
        Assert.Contains("Ollama", message);
    }

    [Fact]
    public void Every_problem_in_a_section_is_reported_at_once()
    {
        // Three separate mistakes should not mean three startup attempts.
        var result = new OllamaOptionsValidator(Using(ModelProvider.Ollama))
            .Validate(null, new OllamaOptions { Endpoint = "nope", Model = "", TimeoutSeconds = -1 });

        Assert.Equal(3, result.Failures!.Count());
    }

    // ---- The settings that are optional on purpose -------------------------

    [Fact]
    public void An_empty_OTLP_endpoint_is_valid_because_it_means_ship_nowhere()
    {
        var result = new TelemetryOptionsValidator().Validate(null, new TelemetryOptions { OtlpEndpoint = "" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void An_empty_OpenAI_endpoint_is_valid_because_it_means_api_openai_com()
    {
        var result = new OpenAIOptionsValidator(Using(ModelProvider.OpenAI))
            .Validate(null, new OpenAIOptions { ApiKey = "sk-test", Endpoint = "" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0, 1, 20, "RateLimit:QuestionsPerMinute")]
    [InlineData(5, 0, 20, "RateLimit:ConcurrentQuestions")]
    [InlineData(5, 1, 0, "RateLimit:PageLoadsPerMinute")]
    public void A_zero_limit_is_rejected_rather_than_read_as_unlimited(
        int questionsPerMinute, int concurrentQuestions, int pageLoadsPerMinute, string setting)
    {
        // Zero would not switch the limit off - it would mean nobody can ask
        // anything, and the app would look broken rather than protected.
        var result = new RateLimitOptionsValidator().Validate(null, new RateLimitOptions
        {
            QuestionsPerMinute = questionsPerMinute,
            ConcurrentQuestions = concurrentQuestions,
            PageLoadsPerMinute = pageLoadsPerMinute,
        });

        Assert.Contains(setting, Assert.Single(result.Failures!));
    }

    [Fact]
    public void The_shipped_appsettings_carries_the_tight_question_limits()
    {
        // The defaults protect the local GPU. The class defaults and the file
        // must say the same thing, or which one wins depends on whether the
        // file is present.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Repo.Root, "src", "StarTrekSqlAssistant.Web", "appsettings.json"))
            .Build();

        var shipped = Bind<RateLimitOptions>(configuration, "RateLimit");
        var defaults = new RateLimitOptions();

        Assert.Equal(defaults.QuestionsPerMinute, shipped.QuestionsPerMinute);
        Assert.Equal(defaults.ConcurrentQuestions, shipped.ConcurrentQuestions);
        Assert.Equal(defaults.PageLoadsPerMinute, shipped.PageLoadsPerMinute);
    }

    // ---- The configuration this repo actually ships ------------------------

    [Fact]
    public void The_shipped_appsettings_passes_every_validator()
    {
        // The defaults are a supported way to run this app - Ollama, no
        // credentials - so shipping an appsettings.json that fails validation
        // would mean a clone that cannot start.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Repo.Root, "src", "StarTrekSqlAssistant.Web", "appsettings.json"))
            .Build();

        var model = Options.Create(Bind<ModelOptions>(configuration, "Model"));
        Assert.Equal(ModelProvider.Ollama, model.Value.Provider);

        AssertValid(new DabOptionsValidator().Validate(null, Bind<DabOptions>(configuration, "Dab")));
        AssertValid(new ModelOptionsValidator().Validate(null, model.Value));
        AssertValid(new TelemetryOptionsValidator().Validate(null, Bind<TelemetryOptions>(configuration, "Telemetry")));
        AssertValid(new RateLimitOptionsValidator().Validate(null, Bind<RateLimitOptions>(configuration, "RateLimit")));
        AssertValid(new OllamaOptionsValidator(model).Validate(null, Bind<OllamaOptions>(configuration, "Ollama")));

        // Empty in the file, and skipped rather than failed because Ollama is
        // selected. This is the assertion that the whole design rests on.
        AssertValid(new OpenAIOptionsValidator(model).Validate(null, Bind<OpenAIOptions>(configuration, "OpenAI")));
        AssertValid(new AnthropicOptionsValidator(model).Validate(null, Bind<AnthropicOptions>(configuration, "Anthropic")));
    }

    private static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    private static void AssertValid(ValidateOptionsResult result) =>
        Assert.True(!result.Failed, result.FailureMessage);
}
