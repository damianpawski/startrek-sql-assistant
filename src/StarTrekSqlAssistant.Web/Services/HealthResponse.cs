using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Writes <c>/health</c> as JSON, including each check's <c>data</c>.
///
/// The framework's default response writer emits the overall status and
/// nothing else - a bare "Healthy" - so everything a check puts in its data
/// dictionary is computed and then thrown away. That made
/// <see cref="DabMcpHealthCheck"/>'s payload unreachable: it reports which
/// endpoint it is pointed at, how many tools the connection carries, and the
/// connection *generation*, which is the one number that says whether the MCP
/// connection has been rebuilt since the app started. Watching that roll over
/// from 1 to 2 after `docker compose restart dab` is the cheapest proof that
/// <see cref="ReconnectingMcpTool"/> did its job, and before this the only way
/// to see it was to grep the app log.
/// </summary>
public static class HealthResponse
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Hands the report to the response as JSON. The status code is the framework's, not ours.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(Serialize(report));
    }

    /// <summary>
    /// The body, separated from the response so a test can assert it without
    /// standing up an HTTP pipeline.
    /// </summary>
    public static string Serialize(HealthReport report)
    {
        var payload = new Dictionary<string, object?>
        {
            ["status"] = report.Status.ToString(),
            ["checks"] = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => (object)new Dictionary<string, object?>
                {
                    ["status"] = entry.Value.Status.ToString(),
                    ["description"] = entry.Value.Description,
                    // The data every check collected, which is the point of this
                    // writer existing. Never entry.Value.Exception: the
                    // description already carries its message, and a stack trace
                    // on an endpoint anything can poll is a gift to nobody.
                    ["data"] = entry.Value.Data.Count == 0 ? null : entry.Value.Data,
                }),
        };

        return JsonSerializer.Serialize(payload, Options);
    }
}
