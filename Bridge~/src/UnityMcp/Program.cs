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

// Register Unity client as singleton
var unityUri = Environment.GetEnvironmentVariable("UNITY_MCP_URI") ?? "ws://localhost:9999/";
builder.Services.AddSingleton(new UnityClient(unityUri));

// Register MCP server with stdio transport and tools from this assembly
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
