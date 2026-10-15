using DustPlanner.App;
using DustPlanner.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// dustplanner-mcp [--home <folder>]
// A stdio MCP server: stdout carries the protocol, so every log line goes to stderr.
var home = Array.IndexOf(args, "--home") is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(AppServices.ForHome(home));
builder.Services.AddSingleton<ReadOnlyGateway>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "DustPlanner", Version = ServerInfo.Version };
        options.ServerInstructions = ServerInfo.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<DustPlannerTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
