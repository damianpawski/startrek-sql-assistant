using Microsoft.Extensions.Options;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Checks shared by the validators below, so one setting being wrong reads the
/// same way wherever it lives.
/// </summary>
public static class OptionChecks
{
    /// <summary>
    /// An endpoint has to be an absolute http(s) URL, because every consumer
    /// here eventually hands it to <see cref="Uri"/> - and that constructor
    /// throws <see cref="UriFormatException"/> deep inside a service
    /// constructor, which is a far worse place to learn about a typo.
    /// </summary>
    public static string? Endpoint(string value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return $"{setting} is '{value}', which is not an absolute URL. " +
                   "Expected something like http://localhost:5000.";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"{setting} is '{value}', which uses the '{uri.Scheme}' scheme. Expected http or https.";
        }

        return null;
    }

    /// <summary>A timeout or token budget of zero is always a mistake, and a negative one throws later.</summary>
    public static string? Positive(int value, string setting) =>
        value > 0 ? null : $"{setting} must be greater than zero, but is {value}.";

    /// <summary>Collapses the checks that found something into one failure, or succeeds.</summary>
    public static ValidateOptionsResult Problems(params string?[] problems)
    {
        var failures = problems.Where(problem => problem is not null).ToArray();

        return failures.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures!);
    }
}

/// <summary>
/// Where DAB's MCP server is. Always validated - there is no configuration in
/// which the app does not need it.
/// </summary>
public sealed class DabOptionsValidator : IValidateOptions<DabOptions>
{
    public ValidateOptionsResult Validate(string? name, DabOptions options) =>
        OptionChecks.Problems(OptionChecks.Endpoint(options.McpEndpoint, "Dab:McpEndpoint"));
}

/// <summary>
/// Which provider is selected. Always validated, and validated first in
/// practice, because the three validators below ask it whether they apply.
/// </summary>
public sealed class ModelOptionsValidator : IValidateOptions<ModelOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelOptions options)
    {
        // A name that is not a member at all ("Gemini") fails when the
        // configuration binder converts it, before this runs. What reaches here
        // is a numeric value that bound cleanly onto no member - Model__Provider=99
        // - which would otherwise fall through ChatClientFactory's switch at the
        // moment of the first question.
        if (!Enum.IsDefined(options.Provider))
        {
            return ValidateOptionsResult.Fail(
                $"Model:Provider is '{(int)options.Provider}', which is not a known provider. " +
                $"Valid values: {string.Join(", ", Enum.GetNames<ModelProvider>())}.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Telemetry. Always validated, but both settings are optional in the sense
/// that an empty <see cref="TelemetryOptions.OtlpEndpoint"/> is a valid choice
/// - it means "instrument, but ship nowhere".
/// </summary>
public sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options) =>
        OptionChecks.Problems(
            string.IsNullOrWhiteSpace(options.ServiceName)
                ? "Telemetry:ServiceName is empty. It is what this app calls itself in traces and metrics."
                : null,
            string.IsNullOrWhiteSpace(options.OtlpEndpoint)
                ? null
                : OptionChecks.Endpoint(options.OtlpEndpoint, "Telemetry:OtlpEndpoint"));
}

/// <summary>
/// Ollama, but only when it is the selected provider.
///
/// This conditionality is the whole difficulty of validating this app's
/// settings, and it is why the provider sections cannot simply be marked
/// [Required] and left to ValidateDataAnnotations. Running with
/// Model:Provider=Ollama is a first-class, credential-free way to use this app,
/// and in that configuration the OpenAI and Anthropic sections are *correctly*
/// empty - appsettings.json ships them that way. A validator that insisted on
/// an OpenAI key regardless of provider would refuse to start the default
/// configuration, so each provider validator returns Skip unless it is the one
/// actually selected.
/// </summary>
public sealed class OllamaOptionsValidator(IOptions<ModelOptions> model) : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (model.Value.Provider != ModelProvider.Ollama)
        {
            return ValidateOptionsResult.Skip;
        }

        return OptionChecks.Problems(
            OptionChecks.Endpoint(options.Endpoint, "Ollama:Endpoint"),
            OptionChecks.Positive(options.TimeoutSeconds, "Ollama:TimeoutSeconds"),
            string.IsNullOrWhiteSpace(options.Model)
                ? "Ollama:Model is empty. It must match a tag from `ollama list` exactly - " +
                  "llama3.1 and llama3.1:8b are different models."
                : null);
    }
}

/// <summary>
/// OpenAI, but only when it is the selected provider. See
/// <see cref="OllamaOptionsValidator"/> for why that condition exists.
/// </summary>
public sealed class OpenAIOptionsValidator(IOptions<ModelOptions> model) : IValidateOptions<OpenAIOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAIOptions options)
    {
        if (model.Value.Provider != ModelProvider.OpenAI)
        {
            return ValidateOptionsResult.Skip;
        }

        return OptionChecks.Problems(
            ApiKey.Missing(options.ApiKey, "OpenAI", "OpenAI:ApiKey", "OpenAI__ApiKey"),
            OptionChecks.Positive(options.TimeoutSeconds, "OpenAI:TimeoutSeconds"),
            string.IsNullOrWhiteSpace(options.Model) ? "OpenAI:Model is empty." : null,
            // Empty means api.openai.com; a value means Azure OpenAI or a gateway.
            string.IsNullOrWhiteSpace(options.Endpoint)
                ? null
                : OptionChecks.Endpoint(options.Endpoint, "OpenAI:Endpoint"));
    }
}

/// <summary>
/// Anthropic, but only when it is the selected provider. See
/// <see cref="OllamaOptionsValidator"/> for why that condition exists.
/// </summary>
public sealed class AnthropicOptionsValidator(IOptions<ModelOptions> model) : IValidateOptions<AnthropicOptions>
{
    public ValidateOptionsResult Validate(string? name, AnthropicOptions options)
    {
        if (model.Value.Provider != ModelProvider.Anthropic)
        {
            return ValidateOptionsResult.Skip;
        }

        return OptionChecks.Problems(
            ApiKey.Missing(options.ApiKey, "Anthropic", "Anthropic:ApiKey", "Anthropic__ApiKey"),
            OptionChecks.Positive(options.TimeoutSeconds, "Anthropic:TimeoutSeconds"),
            OptionChecks.Positive(options.MaxOutputTokens, "Anthropic:MaxOutputTokens"),
            string.IsNullOrWhiteSpace(options.Model)
                ? "Anthropic:Model is empty. It must be a model id exactly as the API names it."
                : null);
    }
}

/// <summary>
/// The missing-key message, in one place. <see cref="ChatClientFactory"/> still
/// raises its own copy: validation is what a running app hits first, but the
/// factory is constructible directly - the tests do it - and a guard that only
/// works when someone remembered to wire up validation is not a guard.
/// </summary>
internal static class ApiKey
{
    public static string? Missing(string key, string provider, string secretPath, string envVar) =>
        string.IsNullOrWhiteSpace(key)
            ? $"Model:Provider is set to {provider} but no API key was found. Set it with " +
              $"`dotnet user-secrets set \"{secretPath}\" \"...\"` for local runs, or the " +
              $"{envVar} environment variable in Docker. Never put it in appsettings.json."
            : null;
}

/// <summary>
/// Binds an options section and registers its validator, with ValidateOnStart
/// so the failure lands in the startup log rather than in whichever request
/// first touched the setting.
/// </summary>
public static class OptionsRegistration
{
    public static IServiceCollection AddValidatedOptions<TOptions, TValidator>(
        this IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(section))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        return services;
    }
}
