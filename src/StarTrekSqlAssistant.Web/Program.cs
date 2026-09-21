using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StarTrekSqlAssistant.Web.Components;
using StarTrekSqlAssistant.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Web App, Interactive Server render mode: the agent (LLM API access,
// MCP client) stays on the server. Nothing sensitive is ever shipped to the
// browser - only UI updates stream down over the SignalR circuit.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // A question can occupy the server for minutes while the model thinks and
    // calls tools. The await itself never blocks the hub, so SignalR keeps
    // sending keep-alive pings throughout - but a slow or backgrounded browser
    // can still miss enough of them to be declared dead mid-answer, which drops
    // the circuit and loses the conversation. KeepAliveInterval stays at the
    // default 15s so the browser's own 30s server timeout is always satisfied;
    // ClientTimeoutInterval is what gets the headroom.
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

// Every section is bound with a validator and ValidateOnStart, so a typo in a
// URL or a missing API key stops the app during startup - with a message
// naming the setting - instead of surfacing as a failure in whatever first
// touched it. Registered before the hosted services below, because hosted
// services run in registration order and the options validator is itself one:
// this way "Ollama:Endpoint is not an absolute URL" beats McpWarmupService
// resolving the agent and throwing a UriFormatException from a constructor.
//
// Model:Provider picks the chat backend; the three provider sections hold the
// settings for whichever one is selected. Only the selected provider's section
// is validated - see OllamaOptionsValidator for why that conditionality is
// unavoidable here. API keys belong in user-secrets or environment variables,
// never in appsettings.json.
builder.Services
    .AddValidatedOptions<DabOptions, DabOptionsValidator>(builder.Configuration, "Dab")
    .AddValidatedOptions<ModelOptions, ModelOptionsValidator>(builder.Configuration, "Model")
    .AddValidatedOptions<OllamaOptions, OllamaOptionsValidator>(builder.Configuration, "Ollama")
    .AddValidatedOptions<OpenAIOptions, OpenAIOptionsValidator>(builder.Configuration, "OpenAI")
    .AddValidatedOptions<AnthropicOptions, AnthropicOptionsValidator>(builder.Configuration, "Anthropic")
    .AddValidatedOptions<TelemetryOptions, TelemetryOptionsValidator>(builder.Configuration, "Telemetry")
    .AddValidatedOptions<RateLimitOptions, RateLimitOptionsValidator>(builder.Configuration, "RateLimit");

// Everything the agent depends on is registered as an interface, so the agent
// can be built over a scripted chat client and a fake tool server in tests.
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();

// Singleton: one shared MCP connection and tool list for the whole app,
// rather than reconnecting to DAB for every chat.
builder.Services.AddSingleton<DabMcpToolProvider>();
builder.Services.AddSingleton<IMcpToolProvider>(sp => sp.GetRequiredService<DabMcpToolProvider>());
builder.Services.AddSingleton<IMcpConnectionState>(sp => sp.GetRequiredService<DabMcpToolProvider>());
// Singleton because the limits are app-wide: one budget of questions a minute
// and one in-flight slot, shared by every circuit. A per-circuit instance would
// hand each new tab a fresh budget, which is no limit at all.
builder.Services.AddSingleton<QuestionLimiter>();
builder.Services.AddSingleton<IStarTrekAgent, StarTrekAgentService>();

// Page loads, not questions. Questions travel over the SignalR connection a
// page opens once, so they never reach HTTP middleware - QuestionLimiter,
// inside AskAsync, is what limits those. This caps the other thing a flood of
// requests costs: every full page load opens a circuit that holds server memory
// for as long as the tab lives.
//
// Only real page loads are counted, and the exclusions are load-bearing.
// /_blazor is the circuit's own traffic: over WebSockets it is one request, but
// a browser that falls back to long polling sends every circuit message as an
// HTTP request to it, and limiting those would throttle a live conversation.
// /health is polled. Everything else - app.css, blazor.web.js, other static
// assets - is recognised by not asking for HTML.
//
// Partitioned by remote address. Under docker compose every browser arrives from
// Docker's gateway address, so there this is effectively one shared limit -
// harmless for a page-load cap, and the reason the question limits are
// deliberately not per visitor either.
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<IOptions<RateLimitOptions>>((options, limits) =>
    {
        var pageLoadsPerMinute = limits.Value.PageLoadsPerMinute;

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType = "text/plain";
            return new ValueTask(context.HttpContext.Response.WriteAsync(
                "Too many page loads from this address. Wait a minute and reload.", cancellationToken));
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            IsPageLoad(context.Request)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = pageLoadsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter("not-a-page-load"));
    });

// Validates the provider config at startup and opens the MCP connection in the
// background - see McpWarmupService for why only the first of those is allowed
// to fail startup.
builder.Services.AddHostedService<McpWarmupService>();

// Whether the MCP connection is up, without having to ask the chat UI.
builder.Services.AddHealthChecks().AddCheck<DabMcpHealthCheck>("dab-mcp");

// Traces, metrics and logs. Four services and a non-deterministic component in
// the middle of them: without this, "why was that answer wrong/slow" is a log
// file, and "how often does that happen" has no answer at all.
//
// AgentTelemetry.Name covers both the app's own instruments and the gen_ai
// spans/metrics from Microsoft.Extensions.AI, because ChatClientFactory.Wrap
// points UseOpenTelemetry at the same source name.
var telemetry = builder.Configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(telemetry.ServiceName))
    .WithTracing(tracing => tracing
        .AddSource(AgentTelemetry.Name)
        .AddAspNetCoreInstrumentation(options =>
            // /health is polled; left in, it buries the requests worth looking at.
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
        // The hop to DAB and the hop to a hosted model provider are both
        // outbound HTTP, so this is what puts them on the same timeline as the
        // question that caused them.
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(AgentTelemetry.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

// Only *register* the exporter when there is somewhere to send to. The
// fast-iteration mode in the README runs the app on the host with nothing
// listening on 18889, and an exporter configured against a dead endpoint spends
// a background thread retrying and logs its failures - noise that looks like an
// app problem. Empty endpoint: instrumentation still runs, nothing ships.
if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
{
    // Checked here as well as in TelemetryOptionsValidator, and with the same
    // message, because this runs at configuration time - before the host starts
    // and therefore before any validator does. Left to the Uri constructor, a
    // typo here would surface as a bare "Invalid URI: The format of the URI
    // could not be determined" that names neither the setting nor the value.
    var problem = OptionChecks.Endpoint(telemetry.OtlpEndpoint, "Telemetry:OtlpEndpoint");
    if (problem is not null)
    {
        throw new InvalidOperationException(problem);
    }

    otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(telemetry.OtlpEndpoint));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Early, so a rejected page load costs as little as possible. Only the global
// page-load limiter above is configured; no endpoint carries a policy.
app.UseRateLimiter();

app.UseAntiforgery();

// MapStaticAssets, not UseStaticFiles: since .NET 9 the framework assets -
// notably _framework/blazor.web.js, the script that upgrades the prerendered
// page to a live SignalR circuit - exist only as entries in
// *.staticwebassets.endpoints.json, not as files under wwwroot. UseStaticFiles
// serves physical files only, so it 404s that script and the page silently
// stays static HTML.
app.MapStaticAssets();

// Liveness for the one dependency that can drop out from under a running app:
// the MCP connection to DAB. Reads cached state only, so it answers instantly
// even when DAB is down.
app.MapHealthChecks("/health");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// A browser navigation - the request that renders a page and opens a circuit.
// Recognised by asking for HTML, which is what separates it from the static
// assets that page then pulls in. The /_blazor and /health exclusions are
// explained where the limiter is configured above.
static bool IsPageLoad(HttpRequest request) =>
    HttpMethods.IsGet(request.Method)
    && !request.Path.StartsWithSegments("/_blazor")
    && !request.Path.StartsWithSegments("/health")
    && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
