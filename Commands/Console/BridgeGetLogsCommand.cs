using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Recent console log entries as structured records; the bridge's sync_and_compile reads them
/// to pick out the diagnostics of its own compile.
/// </summary>
public sealed class BridgeGetLogsCommand(LogBuffer logBuffer) : IMcpCommandHandler {
    public string Command => "unity.bridge.getLogs";

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
