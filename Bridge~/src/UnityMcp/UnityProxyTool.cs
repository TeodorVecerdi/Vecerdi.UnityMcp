using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityMcp;

/// <summary>
/// An MCP tool discovered from a Unity editor at runtime (todo #243): its name, description,
/// and input schema come from the editor's <c>unity.meta.listTools</c> command, and invoking it
/// forwards the arguments verbatim to the corresponding Unity command. The bridge injects its
/// standard <c>port</c> parameter into the schema and strips it back out before forwarding, so
/// dynamic tools route between editors exactly like native ones.
/// </summary>
public sealed class UnityProxyTool : McpServerTool {
    private const string PortParamDescription =
        "Optional Unity Editor port to target. When omitted, the call attaches to the editor chosen via " +
        "'select_editor', or to the only running editor when exactly one exists. Use 'list_editors' to see ports.";

    private static readonly JsonSerializerOptions s_OutputJson = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly UnityConnectionPool m_Pool;
    private readonly string m_Command;

    /// <summary>Identity of the definition this tool was built from; used by the manager to
    /// skip re-registering unchanged tools on refresh.</summary>
    public string Fingerprint { get; }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public UnityProxyTool(UnityConnectionPool pool, string name, string description, string command, JsonElement inputSchema) {
        m_Pool = pool;
        m_Command = command;
        Fingerprint = $"{name}\n{description}\n{command}\n{inputSchema.GetRawText()}";

        ProtocolTool = new Tool {
            Name = name,
            Description = description,
            InputSchema = InjectPortParameter(inputSchema),
        };
    }

    private static JsonElement InjectPortParameter(JsonElement schema) {
        var root = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        if (root["properties"] is not JsonObject properties) {
            root["properties"] = properties = new JsonObject();
        }

        properties["port"] = new JsonObject {
            ["type"] = "integer",
            ["description"] = PortParamDescription,
        };

        return JsonSerializer.SerializeToElement(root);
    }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) {
        int? port = null;
        var parameters = new Dictionary<string, JsonElement>();

        if (request.Params?.Arguments is { } arguments) {
            foreach (var (key, value) in arguments) {
                if (string.Equals(key, "port", StringComparison.Ordinal)) {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsedPort)) {
                        port = parsedPort;
                    }

                    continue;
                }

                parameters[key] = value;
            }
        }

        var resolution = PortResolver.Resolve(port, m_Pool.DefaultPort, EditorDiscovery.GetAvailableEditors());
        if (resolution.Error is { } resolveError) {
            return Result(resolveError, isError: true);
        }

        var (unity, connectionError) = await EditorAvailability.AcquireAsync(m_Pool, resolution.Port!.Value, cancellationToken);
        if (connectionError is not null) {
            return Result(connectionError, isError: true);
        }

        var response = await unity!.SendAsync(m_Command, parameters.Count == 0 ? null : parameters, cancellationToken);
        if (!response.Success) {
            var errorText = response.Error is not null
                ? $"Unity error [{response.Error.Code}]: {response.Error.Message}"
                : "Unity command failed with unknown error";
            return Result(errorText, isError: true);
        }

        return Result(response.Result is { } result ? JsonSerializer.Serialize(result, s_OutputJson) : "OK.", isError: false);
    }

    private static CallToolResult Result(string text, bool isError) => new() {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
