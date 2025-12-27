using System;
using System.Collections.Concurrent;
using System.Text.Json;
using UnityEditor;
using UnityEngine;
using Vecerdi.UnityMcp.Commands;
using Vecerdi.UnityMcp.Protocol;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Vecerdi.UnityMcp;

/// <summary>
/// WebSocket server that handles MCP requests in the Unity Editor.
/// </summary>
[InitializeOnLoad]
public sealed class McpEditorServer {
    private static McpEditorServer? s_Instance;

    public static McpEditorServer Instance => s_Instance ??= new McpEditorServer();
    public static bool IsRunning => s_Instance?.m_IsRunning ?? false;
    public static int Port => s_Instance?.m_Port ?? 0;
    public static int ConnectionCount => s_Instance?.m_ConnectionCount ?? 0;

    private readonly McpCommandRegistry m_Commands = new();
    private readonly LogBuffer m_LogBuffer = new(1000);
    private readonly ConcurrentQueue<(McpRequest Request, Action<McpResponse> SendResponse)> m_PendingRequests = new();

    private WebSocketServer? m_Server;
    private bool m_IsRunning;
    private int m_Port;
    private int m_ConnectionCount;

    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    static McpEditorServer() {
        // Auto-start on editor load
        EditorApplication.delayCall += () => {
            if (!IsRunning) {
                Instance.Start();
            }
        };

        // Process requests on main thread
        EditorApplication.update += ProcessPendingRequests;

        // Cleanup on domain unload
        AssemblyReloadEvents.beforeAssemblyReload += () => {
            s_Instance?.Stop();
        };
    }

    private McpEditorServer() {
        RegisterCommands();
    }

    private void RegisterCommands() {
        // Debug commands
        m_Commands.Register(new GetLogsCommand(m_LogBuffer));
        m_Commands.Register(new ClearLogsCommand(m_LogBuffer));

        // Editor commands
        m_Commands.Register(new RecompileCommand());
        m_Commands.Register(new GetCompilationStatusCommand());
        m_Commands.Register(new IsPlayingCommand());
        m_Commands.Register(new EnterPlayModeCommand());
        m_Commands.Register(new ExitPlayModeCommand());
        m_Commands.Register(new PausePlayModeCommand());
        m_Commands.Register(new ResumePlayModeCommand());
        m_Commands.Register(new GetOpenScenesCommand());
        m_Commands.Register(new SaveAllCommand());
        m_Commands.Register(new RefreshAssetsCommand());

        // Meta command to list available commands
        m_Commands.Register(new ListCommandsHandler(m_Commands));
    }

    public void Start(int port = 9999) {
        if (m_IsRunning) {
            Debug.Log($"[UnityMcp] Server already running on port {m_Port}");
            return;
        }

        m_Port = port;

        try {
            m_Server = new WebSocketServer($"ws://localhost:{port}");
            m_Server.AddWebSocketService<McpBehavior>("/", behavior => behavior.Initialize(this));
            m_Server.Start();
            m_IsRunning = true;

            Debug.Log($"[UnityMcp] Server started on ws://localhost:{port}/");
        } catch (Exception ex) {
            Debug.LogError($"[UnityMcp] Failed to start server: {ex.Message}");
            m_IsRunning = false;
        }
    }

    public void Stop() {
        if (!m_IsRunning) return;

        Debug.Log("[UnityMcp] Stopping server...");

        m_Server?.Stop();
        m_Server = null;

        m_IsRunning = false;
        m_ConnectionCount = 0;

        Debug.Log("[UnityMcp] Server stopped.");
    }

    internal void OnClientConnected() {
        m_ConnectionCount++;
        Debug.Log($"[UnityMcp] Client connected. Total connections: {m_ConnectionCount}");
    }

    internal void OnClientDisconnected() {
        m_ConnectionCount = Math.Max(0, m_ConnectionCount - 1);
        Debug.Log($"[UnityMcp] Client disconnected. Total connections: {m_ConnectionCount}");
    }

    internal void QueueRequest(McpRequest request, Action<McpResponse> sendResponse) {
        m_PendingRequests.Enqueue((request, sendResponse));
    }

    private static void ProcessPendingRequests() {
        if (s_Instance == null) return;

        // Process up to 10 requests per frame to avoid blocking
        for (var i = 0; i < 10 && s_Instance.m_PendingRequests.TryDequeue(out var item); i++) {
            var (request, sendResponse) = item;
            var response = s_Instance.m_Commands.Execute(request);
            sendResponse(response);
        }
    }

    /// <summary>
    /// WebSocket behavior for handling MCP connections.
    /// </summary>
    private sealed class McpBehavior : WebSocketBehavior {
        private McpEditorServer? m_Server;

        public void Initialize(McpEditorServer server) {
            m_Server = server;
        }

        protected override void OnOpen() {
            m_Server?.OnClientConnected();
        }

        protected override void OnClose(CloseEventArgs e) {
            m_Server?.OnClientDisconnected();
        }

        protected override void OnMessage(MessageEventArgs e) {
            if (!e.IsText || m_Server == null) return;

            try {
                var request = JsonSerializer.Deserialize<McpRequest>(e.Data, s_JsonOptions);
                if (request == null) return;

                // Queue for main thread processing
                m_Server.QueueRequest(request, response => {
                    var json = JsonSerializer.Serialize(response, s_JsonOptions);
                    Send(json);
                });
            } catch (JsonException ex) {
                var errorResponse = McpResponse.Fail("", McpErrorCodes.InvalidParams, $"Invalid JSON: {ex.Message}");
                var json = JsonSerializer.Serialize(errorResponse, s_JsonOptions);
                Send(json);
            }
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogWarning($"[UnityMcp] WebSocket error: {e.Message}");
        }
    }

    /// <summary>
    /// Meta command that lists all available commands.
    /// </summary>
    private sealed class ListCommandsHandler(McpCommandRegistry registry) : IMcpCommandHandler {
        public string Command => "unity.listCommands";

        public McpResponse Execute(McpRequest request) {
            return McpResponse.Ok(request.Id, new {
                commands = registry.GetCommands(),
            });
        }
    }
}
