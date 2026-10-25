using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Get recent console log entries.
/// </summary>
public sealed class GetLogsCommand(LogBuffer logBuffer) : IMcpCommandHandler {
    public string Command => "unity.debug.getLogs";

    public McpResponse Execute(McpRequest request) {
        var count = request.GetParam("count", 100);
        var minLevel = request.GetParam<string>("minLevel");
        var filter = request.GetParam<string>("filter");

        var logs = logBuffer.GetLogs(count, minLevel, filter);
        return McpResponse.Ok(request.Id, new {
            logs,
            total = logs.Count,
            droppedSinceStart = logBuffer.DroppedSinceStart,
            capacity = logBuffer.MaxSize,
        });
    }
}
