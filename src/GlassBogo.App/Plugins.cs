using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using GlassBogo.PluginSdk;

namespace GlassBogo.App;

internal sealed class Plugins(Database db)
{
    internal string Root => Path.Combine(db.DirectoryPath, "plugins");
    internal IEnumerable<(PluginManifest? Manifest, string Folder, string? Error)> List()
    {
        if (!Directory.Exists(Root))
            yield break;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            PluginManifest? manifest = null;
            string? error = null;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(dir, "plugin.json")), Protocol.Json)!;
                Protocol.Validate(manifest);
            }
            catch (Exception ex) { error = ex.Message; }
            yield return (manifest, dir, error);
        }
    }
    internal PluginManifest Inspect(string folder)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(folder, "plugin.json")), Protocol.Json) ?? throw new InvalidDataException("插件清单为空。");
        Protocol.Validate(manifest);
        if (!File.Exists(Path.Combine(folder, manifest.Executable)))
            throw new FileNotFoundException("找不到插件程序。");
        return manifest;
    }
    internal void Install(string source)
    {
        var manifest = Inspect(source);
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, manifest.Id);
        if (Directory.Exists(target))
            throw new InvalidOperationException("该插件已安装，请先卸载后更新。");
        var staging = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Copy(source, staging);
            Directory.Move(staging, target);
            db.Update(s => s.Plugins[manifest.Id] = false);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static void Copy(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件包不允许链接目录。");
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("插件包不允许链接文件。");
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        }
        foreach (var d in Directory.GetDirectories(source))
            Copy(d, Path.Combine(target, Path.GetFileName(d)));
    }
    internal void Uninstall(PluginManifest manifest)
    {
        Protocol.Validate(manifest);
        var folder = Path.GetFullPath(Path.Combine(Root, manifest.Id));
        if (Path.GetDirectoryName(folder) != Path.GetFullPath(Root))
            throw new InvalidOperationException("插件路径无效。");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        db.Update(s => s.Plugins.Remove(manifest.Id));
    }
}
internal sealed class PluginSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private int disposed; private static int activeCount;
    internal static int ActiveCount => Volatile.Read(ref activeCount);
    private readonly Process process;
    private readonly SemaphoreSlim requests = new(1);
    private StreamReader? reader; private StreamWriter? writer;
    internal PluginSession(string folder, PluginManifest manifest)
    {
        Protocol.Validate(manifest);
        var name = "GlassBogo-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(Path.Combine(folder, manifest.Executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(name);
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("插件无法启动。");
            Interlocked.Increment(ref activeCount);
        }
        catch { pipe.Dispose(); throw; }
    }
    internal async Task<PluginView> Request(string method, string? action = null, string? entryName = null, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        await requests.WaitAsync(token);
        try
        {
            if (!pipe.IsConnected)
            {
                await pipe.WaitForConnectionAsync(token);
                reader = new(pipe, leaveOpen: true);
                writer = new(pipe, leaveOpen: true)
                {
                    AutoFlush = true
                };
            }
            var request = new PluginRequest(1, Guid.NewGuid().ToString("N"), method, action, entryName);
            await writer!.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.Json).AsMemory(), token);
            // Bounded read: do not trust a third-party process to send a short JSON line.
            var chars = new char[1];
            var line = new System.Text.StringBuilder();
            while (true)
            {
                if (await reader!.ReadAsync(chars, token) == 0)
                    throw new IOException("插件已退出。");
                if (chars[0] == '\n')
                    break;
                if (line.Length >= 65536)
                    throw new InvalidDataException("插件响应过大。");
                line.Append(chars[0]);
            }
            var response = JsonSerializer.Deserialize<PluginResponse>(line.ToString(), Protocol.Json) ?? throw new InvalidDataException("插件响应为空。");
            if (response.Id != request.Id)
                throw new InvalidDataException("插件响应标识不匹配。");
            if (response.Error is not null)
                throw new InvalidOperationException(response.Error);
            return response.View ?? throw new InvalidDataException("插件没有返回视图。");
        }
        catch { await DisposeAsync(); throw; }
        finally { requests.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return ValueTask.CompletedTask;
        Interlocked.Decrement(ref activeCount);
        pipe.Dispose();
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
