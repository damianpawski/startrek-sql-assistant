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

// Singleton: one shared MCP connection and tool list for the whole app,
// rather than reconnecting to DAB for every chat.
builder.Services.AddSingleton<StarTrekAgentService>();

var app = builder.Build();

// Resolve the agent once at startup rather than on the first chat message.
// ChatClientFactory validates the provider config in its constructor (an
// Anthropic/OpenAI provider with no API key, an unparseable endpoint), and a
// clear failure in the startup log beats a Blazor component blowing up when
// someone types their first question.
_ = app.Services.GetRequiredService<StarTrekAgentService>();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
