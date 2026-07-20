using System;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnityEditor;
using Vecerdi.Extensions.Logging;
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
    private readonly LogBuffer m_LogBuffer = new();
    private readonly ConcurrentQueue<(McpRequest Request, Action<McpResponse> SendResponse)> m_PendingRequests = new();
    private readonly ILogger<McpEditorServer> m_Logger = UnityLoggerFactory.CreateLogger<McpEditorServer>();

    private WebSocketServer? m_Server;
    private bool m_IsRunning;
    private int m_Port;
    private int m_ConnectionCount;

    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    static McpEditorServer() {
        // Auto-start on editor load - use both delayCall and update to ensure
        // server starts even if editor doesn't have focus
        EditorApplication.delayCall += TryStartServer;

        // Also try to start on update (will be a no-op if already running)
        // This ensures the server starts even when Unity doesn't have focus
        EditorApplication.update += OnEditorUpdate;

        // Cleanup on domain unload
        AssemblyReloadEvents.beforeAssemblyReload += () => {
            s_Instance?.m_LogBuffer.Dispose();
            s_Instance?.Stop();
        };
    }

    private static bool s_StartupAttempted;

    private static void TryStartServer() {
        if (!IsRunning) {
            Instance.Start();
        }
    }

    private static void OnEditorUpdate() {
        // Process pending requests on main thread
        ProcessPendingRequests();

        // Try to start server if not yet started (handles case where editor wasn't focused)
        if (!s_StartupAttempted) {
            s_StartupAttempted = true;
            if (!IsRunning) {
                Instance.Start();
            }
        }
    }

    private McpEditorServer() {
        RegisterCommands();
    }

    private void RegisterCommands() {
        // Debug commands
        m_Commands.Register(new GetLogsCommand(m_LogBuffer));
        m_Commands.Register(new ClearLogsCommand(m_LogBuffer, m_Logger));

        // Editor commands
        m_Commands.Register(new RecompileCommand());
        m_Commands.Register(new GetCompilationStatusCommand()); // Used internally by MCP recompile tool
        m_Commands.Register(new IsPlayingCommand());
        m_Commands.Register(new SetPlayModeCommand());
        m_Commands.Register(new RefreshAssetsCommand());
        m_Commands.Register(new ExecuteMenuItemCommand());
        m_Commands.Register(new InvokeManagedMethodCommand());
        m_Commands.Register(new GetInvocationResultCommand());

        // Tool discovery for the stdio bridge (#243) — must see every handler, register last.
        m_Commands.Register(new ListToolsCommand(m_Commands));
        m_Commands.Register(new RunTestsCommand());
        m_Commands.Register(new GetTestRunStatusCommand());
        m_Commands.Register(new CancelTestRunCommand());
    }

    public void Start(int port = -1) {
        if (m_IsRunning) {
            m_Logger.LogDebug("Server already running on port {Port}", m_Port);
            return;
        }

        // If no port specified, use dynamic allocation
        if (port == -1) {
            port = EditorInstanceRegistry.RegisterInstance(m_Logger);
            if (port == -1) {
                m_Logger.LogError("Failed to allocate port for MCP server");
                return;
            }
        }

        m_Port = port;

        try {
            m_Server = new WebSocketServer($"ws://localhost:{port}");
            m_Server.AddWebSocketService<McpBehavior>("/", behavior => behavior.Initialize(this, m_Logger));
            m_Server.Start();
            m_IsRunning = true;

            m_Logger.LogDebug("Server started on ws://localhost:{Port}/", port);
        } catch (Exception ex) {
            m_Logger.LogError(ex, "Failed to start server");
            m_IsRunning = false;
        }
    }

    public void Stop() {
        if (!m_IsRunning) return;

        m_Logger.LogDebug("Stopping server...");

        // Unregister from discovery file
        EditorInstanceRegistry.UnregisterInstance(m_Port, m_Logger);

        m_Server?.Stop();
        m_Server = null;

        m_IsRunning = false;
        m_ConnectionCount = 0;

        m_Logger.LogDebug("Server stopped");
    }

    internal void OnClientConnected() {
        m_ConnectionCount++;
        m_Logger.LogDebug("Client connected. Total connections: {ConnectionCount}", m_ConnectionCount);
    }

    internal void OnClientDisconnected() {
        m_ConnectionCount = Math.Max(0, m_ConnectionCount - 1);
        m_Logger.LogDebug("Client disconnected. Total connections: {ConnectionCount}", m_ConnectionCount);
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
        private ILogger? m_Logger;

        public void Initialize(McpEditorServer server, ILogger logger) {
            m_Server = server;
            m_Logger = logger;
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
                    try {
                        Send(json);
                    } catch (InvalidOperationException ex) {
                        m_Logger?.LogDebug(ex, "Skipping response send because socket is no longer open.");
                    }
                });
            } catch (JsonException ex) {
                m_Logger?.LogWarning(ex, "Failed to parse MCP request");
                var errorResponse = McpResponse.Fail("", McpErrorCodes.InvalidParams, $"Invalid JSON: {ex.Message}");
                var json = JsonSerializer.Serialize(errorResponse, s_JsonOptions);
                try {
                    Send(json);
                } catch (InvalidOperationException sendEx) {
                    m_Logger?.LogDebug(sendEx, "Skipping parse error response send because socket is no longer open.");
                }
            }
        }

        protected override void OnError(ErrorEventArgs e) {
            m_Logger?.LogWarning("WebSocket error: {Message}", e.Message);
        }
    }
}
