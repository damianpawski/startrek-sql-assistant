using StarTrekSqlAssistant.Web.Components;
using StarTrekSqlAssistant.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Web App, Interactive Server render mode: the agent (LLM API access,
// MCP client) stays on the server. Nothing sensitive is ever shipped to the
// browser - only UI updates stream down over the SignalR circuit.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<DabOptions>(builder.Configuration.GetSection("Dab"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));

// Singleton: one shared MCP connection and tool list for the whole app,
// rather than reconnecting to DAB for every chat.
builder.Services.AddSingleton<StarTrekAgentService>();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
