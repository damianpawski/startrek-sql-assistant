namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Startup work for the agent, split by how its two halves should fail.
///
/// <c>StartAsync</c> resolves the agent and lets any failure kill startup:
/// ChatClientFactory validates the provider config in its constructor (an
/// Anthropic/OpenAI provider with no API key, an unparseable endpoint), and a
/// clear failure in the startup log beats a Blazor component blowing up when
/// someone types their first question.
///
/// <c>ExecuteAsync</c> then opens the MCP connection in the background, and
/// must *not* fail startup: `depends_on: dab` in docker-compose.yml waits for
/// the container to exist, not for DAB to be listening, so a cold
/// `docker compose up` routinely gets here before DAB is ready. Failing hard
/// would turn that into a crash loop. It retries with backoff and then gives
/// up quietly - the connection is lazy anyway, so the first question still
/// establishes it, and /health reports the state in the meantime.
/// </summary>
public sealed class McpWarmupService(
    IServiceProvider services,
    DabMcpToolProvider toolProvider,
    ILogger<McpWarmupService> logger) : BackgroundService
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<IStarTrekAgent>();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            try
            {
                await toolProvider.ConnectAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                if (attempt == Backoff.Length)
                {
                    logger.LogWarning(ex,
                        "Could not warm up the MCP connection after {Attempts} attempts; the first question will retry",
                        attempt + 1);
                    return;
                }

                logger.LogInformation(
                    "MCP server not ready yet ({Message}); retrying in {Delay}", ex.Message, Backoff[attempt]);
                await Task.Delay(Backoff[attempt], stoppingToken);
            }
        }
    }
}
