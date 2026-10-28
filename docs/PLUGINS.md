# 本地插件 SDK v1

插件是独立的本地 EXE，不向 GlassBogo 主进程加载任意 DLL。只有受信任的来源适合安装；进程隔离用于避免普通崩溃拖垮 UI，并非恶意代码沙箱。

## 清单

包根目录需有 `plugin.json`，入口必须为包内相对 EXE 路径，禁止 `..` 和链接目录。

```json
{
  "id": "glassbogo.focus",
  "name": "专注小记",
  "version": "1.0.0",
  "apiVersion": 1,
  "executable": "FocusPlugin.exe",
  "capabilities": ["card", "page", "command", "entry-menu"],
  "subscriptions": ["workspace.changed"]
}
```

安装后默认停用。启用前展示能力；没有在线安装或自动更新。工作台只展示已启用插件。卸载不修改宿主日历、入口或用户原文件。

## 通信

宿主启动：`Plugin.exe --pipe <随机管道名>`。两端使用 CurrentUserOnly 命名管道。UTF-8 一行一个 JSON，API 版本为 1，请求 ID 必须原样回传。单条响应最多 64 KiB，请求超时 5 秒；取消、退出或错误会终止会话并回收进程。

请求字段：`apiVersion`、`id`、`method`、可选 `action`、可选 `entryName`。method 为声明的扩展点名称；事件通知为 `event`，action 为 `workspace.changed`。通知不包含完整任务和入口数据。只有入口菜单主动调用会传入当前选中入口的显示名称。

响应字段：`id`、`view` 或 `error`。view 包含 `title`、`text`、`actions`（每项 `id` 和 `label`，宿主最多显示 20 项）。宿主负责统一渲染，不接受 HTML、脚本、任意 XAML 或嵌入窗口。

卡片可声明 workspace.changed 订阅，仅在卡片可见期间连接，通知经过 250 毫秒合并；卸载卡片自动退订。插件不能通过通知调用宿主文件处理功能，因为宿主不存在这类接口。

## 示例

```csharp
await Protocol.RunAsync(args, request =>
    new PluginView("我的卡片", "离线插件已连接", [new("run", "执行命令")]));
```

`scripts/build.ps1` 同时生成可安装的 `artifacts/sample-plugin`。选择该目录安装，启用后回到工作台即可看到卡片。示例专注次数仅保存在当前会话内。
