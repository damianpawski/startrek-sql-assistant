using Microsoft.Extensions.AI;

namespace StarTrekSqlAssistant.Web.Services;

/// <summary>
/// Supplies the tools the model may call. The only implementation in the app
/// talks to Data API builder's MCP server; tests substitute an in-process fake
/// so tool-argument shaping can be asserted without Docker.
/// </summary>
public interface IMcpToolProvider
{
    /// <summary>
    /// The tool list, connecting on first use. Throws if the tool server
    /// cannot be reached - the agent turns that into a friendly chat reply.
    /// </summary>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default);
}
