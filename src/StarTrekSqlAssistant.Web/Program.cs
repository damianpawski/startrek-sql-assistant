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

builder.Services.Configure<DabOptions>(builder.Configuration.GetSection("Dab"));

// Model:Provider picks the chat backend; the three sections below hold the
// settings for whichever one is selected. Only the selected provider's section
// is read, so the others can stay empty. API keys belong in user-secrets or
// environment variables, never in appsettings.json.
builder.Services.Configure<ModelOptions>(builder.Configuration.GetSection("Model"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection("Telemetry"));

// Everything the agent depends on is registered as an interface, so the agent
// can be built over a scripted chat client and a fake tool server in tests.
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();

// Singleton: one shared MCP connection and tool list for the whole app,
// rather than reconnecting to DAB for every chat.
builder.Services.AddSingleton<DabMcpToolProvider>();
builder.Services.AddSingleton<IMcpToolProvider>(sp => sp.GetRequiredService<DabMcpToolProvider>());
builder.Services.AddSingleton<IMcpConnectionState>(sp => sp.GetRequiredService<DabMcpToolProvider>());
builder.Services.AddSingleton<IStarTrekAgent, StarTrekAgentService>();

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
    otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(telemetry.OtlpEndpoint));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

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
