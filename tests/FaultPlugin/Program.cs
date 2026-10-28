using GlassBogo.PluginSdk;
await Protocol.RunAsync(args, request => { if (request.Action == "crash") Environment.Exit(5); if (request.Action == "timeout") Thread.Sleep(10000); throw new InvalidOperationException("测试错误"); });
