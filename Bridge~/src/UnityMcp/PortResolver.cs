namespace UnityMcp;

/// <summary>
/// Outcome of resolving which editor port a tool call should target.
/// </summary>
public readonly record struct PortResolution {
    private PortResolution(int? port, string? error) {
        Port = port;
        Error = error;
    }

    /// <summary>The resolved port, or <c>null</c> when resolution failed.</summary>
    public int? Port { get; }

    /// <summary>A human-readable error, or <c>null</c> when resolution succeeded.</summary>
    public string? Error { get; }

    public bool IsResolved => Error is null;

    public static PortResolution Resolved(int port) => new(port, null);
    public static PortResolution Failed(string error) => new(null, error);
}

/// <summary>
/// Pure resolution of the editor a tool call should target, in priority order:
/// explicit port -> default selection -> the only running editor -> error.
/// </summary>
public static class PortResolver {
    public static PortResolution Resolve(int? explicitPort, int? defaultPort, IReadOnlyList<EditorInstance> availableEditors) {
        if (explicitPort is { } port) return PortResolution.Resolved(port);
        if (defaultPort is { } selected) return PortResolution.Resolved(selected);

        return availableEditors.Count switch {
            1 => PortResolution.Resolved(availableEditors[0].Port),
            0 => PortResolution.Failed("No Unity Editor instances found. Make sure Unity is running with the MCP plugin installed."),
            _ => PortResolution.Failed(
                $"Multiple Unity Editors found ({availableEditors.Count}). Pass the 'port' parameter to target one, " +
                "or call 'select_editor' to set a default. Use 'list_editors' to see available ports."),
        };
    }
}
