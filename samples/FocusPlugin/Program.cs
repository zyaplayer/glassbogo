using GlassBogo.PluginSdk;
int count = 0;
await Protocol.RunAsync(args, request =>
{
    if (request.Action == "finish")
        count++;
    return new PluginView("专注小记", $"本次已完成 {count} 次专注。\n" + (request.EntryName is null ? "这是本地插件示例，退出页面后释放进程。" : $"选中的入口：{request.EntryName}"), [new("finish", "完成一次专注")]);
});
