using GlassBogo.PluginSdk;
using System.Windows.Threading;

namespace GlassBogo.App;

internal sealed class PluginCard : Border
{
    private readonly StackPanel body = new(); private PluginSession? session; private CancellationTokenSource? cancellation;
    private readonly DispatcherTimer events = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly AppContext ctx; private readonly PluginManifest manifest; private readonly string folder;
    internal PluginCard(AppContext context, PluginManifest plugin, string path)
    {
        ctx = context;
        manifest = plugin;
        folder = path;
        CornerRadius = new(16);
        Padding = new(16);
        Margin = new(4);
        BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "Card");
        SetResourceReference(BorderBrushProperty, "Line");
        Child = body;
        body.Children.Add(Design.Text(plugin.Name, 18));
        Loaded += async (_, _) => { cancellation = new(); session = new(folder, manifest); ctx.Db.Changed += Changed; await Refresh("card"); };
        events.Tick += async (_, _) => { events.Stop(); await Refresh("event", "workspace.changed"); };
        Unloaded += async (_, _) => { ctx.Db.Changed -= Changed; events.Stop(); cancellation?.Cancel(); if (session is not null) await session.DisposeAsync(); cancellation?.Dispose(); session = null; };
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (manifest.Subscriptions?.Contains("workspace.changed") == true)
            Dispatcher.BeginInvoke(() => { events.Stop(); events.Start(); });
    }
    private async Task Refresh(string method, string? action = null)
    {
        if (session is null || cancellation is null)
            return;
        var token = cancellation.Token;
        try
        {
            var view = await session.Request(method, action, cancellation: token);
            if (token.IsCancellationRequested)
                return;
            body.Children.Clear();
            body.Children.Add(Design.Text(view.Title, 18));
            body.Children.Add(Design.Text(view.Text));
            foreach (var a in view.Actions.Take(20))
                body.Children.Add(Design.AsyncButton(a.Label, () => Refresh("card", a.Id)));
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) { body.Children.Clear(); body.Children.Add(Design.Text(manifest.Name + "：" + ex.Message, 14, "Muted")); } }
    }
}
