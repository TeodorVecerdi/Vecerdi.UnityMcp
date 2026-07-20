using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnityMcp;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (MCP uses stdout for protocol messages)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => {
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Set minimum log level based on environment
var logLevel = Environment.GetEnvironmentVariable("UNITY_MCP_LOG_LEVEL") switch {
    "debug" => LogLevel.Debug,
    "trace" => LogLevel.Trace,
    "warning" => LogLevel.Warning,
    "error" => LogLevel.Error,
    _ => LogLevel.Information,
};
builder.Logging.SetMinimumLevel(logLevel);

// Register the connection pool as a singleton. It lazily opens one connection per editor
// port, so multiple concurrent consumers of this bridge can each target a different editor.
builder.Services.AddSingleton(new UnityConnectionPool());

// Dynamic tool discovery (#243): editors advertise agent-facing tools via unity.meta.listTools;
// the manager reconciles them into the server's tool collection whenever a connection (re)opens.
builder.Services.AddSingleton<DynamicToolManager>();

// Register MCP server with stdio transport and tools from this assembly
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
host.Services.GetRequiredService<DynamicToolManager>().Attach();
await host.RunAsync();
