using UnityEditor;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.bridge.refreshAssets - Refresh the asset database.
/// </summary>
public sealed class BridgeRefreshAssetsCommand : IMcpCommandHandler {
    public string Command => "unity.bridge.refreshAssets";

    public McpResponse Execute(McpRequest request) {
        AssetDatabase.Refresh();
        return McpResponse.Ok(request.Id, new { refreshed = true });
    }
}