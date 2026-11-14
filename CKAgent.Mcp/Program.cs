using Garden.CkAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

string? creationKit = null;
string? gameDirectory = null;
string? stateDirectory = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ck":
            if (++i >= args.Length) { Console.Error.WriteLine("--ck requires a path to CreationKit.exe"); return 2; }
            creationKit = args[i];
            break;
        case "--game":
            if (++i >= args.Length) { Console.Error.WriteLine("--game requires a game directory"); return 2; }
            gameDirectory = args[i];
            break;
        case "--state":
            if (++i >= args.Length) { Console.Error.WriteLine("--state requires a directory"); return 2; }
            stateDirectory = args[i];
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            Console.Error.WriteLine("usage: ckagent [--ck <CreationKit.exe>] [--game <Fallout 4 dir>] [--state <work dir>]");
            return 2;
    }
}

if (!EditorPaths.TryCreate(creationKit, gameDirectory, stateDirectory, out EditorPaths? paths, out string startupError))
{
    Console.Error.WriteLine(startupError);
    return 2;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(paths!);
builder.Services.AddSingleton<CommandStore>();
builder.Services.AddSingleton<PluginClient>();
builder.Services.AddSingleton<SaveCoordinator>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CkTools>()
    .WithTools<CameraTools>()
    .WithTools<ResolveTools>()
    .WithTools<CellTools>()
    .WithTools<UiTools>();

await builder.Build().RunAsync();
return 0;
