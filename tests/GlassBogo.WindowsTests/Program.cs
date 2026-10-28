using System.IO;
using GlassBogo.App;
using GlassBogo.Core;
using GlassBogo.PluginSdk;
using System.Text.Json;
using System.Diagnostics;

int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
void Check(bool ok) { if (!ok) throw new Exception("assertion failed"); }
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var sample = Path.Combine(root, "artifacts", "sample-plugin");
var fault = Path.Combine(root, "artifacts", "fault-plugin");
var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(sample, "plugin.json")), Protocol.Json)!;
await Test("真实管道请求与命令", async () => { await using var session = new PluginSession(sample, manifest); var first = await session.Request("card"); Check(first.Text.Contains("0 次")); var next = await session.Request("card", "finish"); Check(next.Text.Contains("1 次")); });
await Test("插件声明事件通知", async () => { await using var session = new PluginSession(sample, manifest); var view = await session.Request("event", "workspace.changed"); Check(view.Title == "专注小记"); });
await Test("插件异常返回", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); try { await session.Request("card"); throw new Exception("did not fail"); } catch (InvalidOperationException e) { Check(e.Message.Contains("测试错误")); } });
await Test("插件崩溃不影响宿主", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); try { await session.Request("card", "crash"); throw new Exception("did not fail"); } catch (IOException) { } });
await Test("插件超时结束进程", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); var timer = Stopwatch.StartNew(); try { await session.Request("card", "timeout"); throw new Exception("did not fail"); } catch (OperationCanceledException) { Check(timer.Elapsed.TotalSeconds < 7); } });
await Test("调用取消", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); using var cancellation = new CancellationTokenSource(100); try { await session.Request("card", "timeout", cancellation: cancellation.Token); throw new Exception("did not cancel"); } catch (OperationCanceledException) { } });
await Test("插件安装停用卸载", () => { using var db = new Database(Path.Combine(Path.GetTempPath(), "GlassBogo-plugin-tests-" + Guid.NewGuid().ToString("N"))); var plugins = new Plugins(db); plugins.Install(sample); Check(plugins.List().Single().Manifest!.Id == manifest.Id); Check(!db.Current.Plugins[manifest.Id]); plugins.Uninstall(manifest); Check(!plugins.List().Any()); return Task.CompletedTask; });
await Test("快捷方式解析不改写文件", async () => { var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-link-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "sample.lnk"); await Platform.Sta(() => { dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!; dynamic link = shell.CreateShortcut(path); try { link.TargetPath = Environment.ProcessPath; link.Arguments = "--profile A"; link.WorkingDirectory = dir; link.Save(); } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } return true; }); var before = File.ReadAllBytes(path); var entry = await Platform.Sta(() => Platform.Resolve(path)); Check(entry.Arguments == "--profile A" && entry.WorkingDirectory == dir && File.ReadAllBytes(path).SequenceEqual(before)); });
await Test("错误目标启动反馈", async () => { var result = await new Launcher().LaunchAsync(new("x", "missing", @"C:\does-not-exist-glassbogo\app.exe")); Check(!result.Success && !string.IsNullOrWhiteSpace(result.Error)); });
await Test("插件全部声明能力分别调用", async () => { await using var session = new PluginSession(sample, manifest); foreach (var capability in manifest.Capabilities) { var view = await session.Request(capability, entryName: capability == "entry-menu" ? "测试入口" : null); Check(!string.IsNullOrWhiteSpace(view.Title)); } });
Console.WriteLine($"RESULT passed={passed} failed={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;
