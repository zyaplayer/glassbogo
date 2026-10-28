using System.IO.Pipes;
using System.Text.Json;

namespace GlassBogo.PluginSdk;

public record PluginManifest(string Id, string Name, string Version, int ApiVersion, string Executable, string[] Capabilities, string[]? Subscriptions = null);
public record PluginAction(string Id, string Label);
public record PluginView(string Title, string Text, PluginAction[] Actions);
public record PluginRequest(int ApiVersion, string Id, string Method, string? Action = null, string? EntryName = null);
public record PluginResponse(string Id, PluginView? View = null, string? Error = null);
public static class Protocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static void Validate(PluginManifest manifest)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[a-z][a-z0-9.-]{2,79}$") || manifest.ApiVersion != Version || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("插件标识或 API 版本不兼容。");
        if (Path.IsPathRooted(manifest.Executable) || manifest.Executable.Contains("..") || !manifest.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件入口必须为包内 EXE。");
        if (manifest.Capabilities is null || manifest.Capabilities.Any(c => c is not ("card" or "page" or "command" or "entry-menu")))
            throw new InvalidDataException("插件请求了不支持的能力。");
        if (manifest.Subscriptions?.Any(s => s != "workspace.changed") == true)
            throw new InvalidDataException("不支持的事件订阅。");
    }
    public static async Task RunAsync(string[] args, Func<PluginRequest, PluginView> handle, CancellationToken token = default)
    {
        if (args.Length != 2 || args[0] != "--pipe")
            throw new ArgumentException("由 GlassBogo 启动此插件。");
        await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
                break;
            if (line.Length > 65536)
                throw new InvalidDataException("请求过大。");
            var request = JsonSerializer.Deserialize<PluginRequest>(line, Json)!;
            PluginResponse reply;
            try
            {
                if (request.ApiVersion != Version)
                    throw new InvalidDataException("API 版本不兼容。");
                reply = new(request.Id, handle(request));
            }
            catch (Exception ex) { reply = new(request.Id, Error: ex.Message); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json));
        }
    }
}
