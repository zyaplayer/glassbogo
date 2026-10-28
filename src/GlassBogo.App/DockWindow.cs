using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBogo.App;

internal sealed class DockWindow : GlassWindow
{
    private readonly AppContext ctx;
    private State renderedState;
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal };
    private readonly GlassWindow popup;
    private readonly DispatcherTimer openTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer environmentTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private sealed record PopupPage(UIElement Content, Button[] Entries);
    private readonly Dictionary<string, PopupPage> popupPages = new();
    private Button[] visibleEntries = [];
    private int hideGeneration;
    private Category? pending, active; private Button? anchor; private int page; private int holds;
    private Point dragStart; private string? dragCategory;
    private nint hook; private nint locationHook; private readonly Native.WinEventProc foregroundCallback;
    private HwndSource? source; private bool collapsed; private readonly Button discoveries;
    internal string ShortcutStatus { get; private set; } = "";
    internal DockWindow(AppContext context)
    {
        ctx = context;
        renderedState = context.Db.Current;
        Title = "GlassBogo";
        Height = 112;
        MinHeight = 12;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        popup = new GlassWindow { Title = "GlassBogo 分类", Width = 440, Height = 360, Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize };
        popup.MouseEnter += (_, _) => closeTimer.Stop();
        popup.MouseLeave += (_, _) => ScheduleClose();
        popup.PreviewKeyDown += PopupKeyDown;
        popup.PreviewMouseWheel += (_, e) => { if (e.Delta != 0) { ChangePage(e.Delta < 0 ? 1 : -1); e.Handled = true; } };
        popup.LostKeyboardFocus += (_, _) => ScheduleClose();
        discoveries = Design.Button("＋", () => ctx.OpenWorkspace(true));
        discoveries.ToolTip = "添加入口 / 新发现";
        var body = new DockPanel();
        var actions = Design.Row(Design.Button("⌕", () => ctx.OpenWorkspace()), Design.Button("▦", () => ctx.OpenCalendar()), discoveries);
        DockPanel.SetDock(actions, Dock.Right);
        body.Children.Add(actions);
        body.Children.Add(new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        SetBody(body);
        openTimer.Tick += (_, _) => { openTimer.Stop(); if (pending is not null && anchor is not null) ShowCategory(pending, anchor); };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); if (holds > 0 || popup.IsMouseOver || IsMouseOver || popup.IsKeyboardFocusWithin) return; HidePopup(true); if (ctx.Db.Current.Preferences.AutoHide) SetCollapsed(true); };
        MouseEnter += (_, _) => { closeTimer.Stop(); if (collapsed) SetCollapsed(false); };
        MouseLeave += (_, _) => ScheduleClose();
        environmentTimer.Tick += (_, _) => { environmentTimer.Stop(); AssessForeground(); };
        foregroundCallback = (_, ev, hwnd, obj, _, _, _) => { if (ev == 3 || hwnd == Native.GetForegroundWindow() && obj == 0) Dispatcher.BeginInvoke(() => { environmentTimer.Stop(); environmentTimer.Start(); }); };
        Loaded += (_, _) => { popup.Owner = this; PositionDock(); hook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2); locationHook = Native.SetWinEventHook(0x800B, 0x800B, 0, foregroundCallback, 0, 0, 2); source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage); RegisterShortcut(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        ctx.Db.Changed += DataChanged;
        ctx.Desktop.Changed += DesktopChanged;
        discoveryTimer.Tick += async (_, _) => { discoveryTimer.Stop(); await RefreshDiscoveries(); };
        Closing += (_, _) => { if (!ctx.Closing) { ctx.Closing = true; Dispatcher.BeginInvoke(() => Application.Current.Shutdown()); } };
        Closed += (_, _) => { openTimer.Stop(); closeTimer.Stop(); environmentTimer.Stop(); discoveryTimer.Stop(); Native.UnhookWinEvent(hook); Native.UnhookWinEvent(locationHook); if (source is not null) { Native.UnregisterHotKey(source.Handle, 1); source.RemoveHook(WindowMessage); } SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; ctx.Db.Changed -= DataChanged; ctx.Desktop.Changed -= DesktopChanged; popup.Close(); };
        Render();
        discoveryTimer.Start();
    }
    private void DataChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        var next = ctx.Db.Current;
        var before = renderedState;
        renderedState = next;
        if (!before.Categories.SequenceEqual(next.Categories) || !before.Entries.SequenceEqual(next.Entries))
        {
            HidePopup();
            popupPages.Clear();
            Render();
            Design.Reveal(strip);
        }
        if (before.Preferences.Monitor != next.Preferences.Monitor)
            PositionDock();
        if (before.Preferences.AutoHide != next.Preferences.AutoHide && !next.Preferences.AutoHide)
            SetCollapsed(false);
        if (before.Preferences.QuickShortcut != next.Preferences.QuickShortcut)
            RegisterShortcut();
        if (!before.Entries.SequenceEqual(next.Entries) || !before.DismissedDiscoveries.SequenceEqual(next.DismissedDiscoveries))
        {
            discoveryTimer.Stop();
            discoveryTimer.Start();
        }
    });
    private void DesktopChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { discoveryTimer.Stop(); discoveryTimer.Start(); });
    private async Task RefreshDiscoveries()
    {
        try
        {
            var found = await ctx.Desktop.DiscoverAsync(CancellationToken.None);
            var keys = ctx.Db.Current.Entries.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
            var count = found.Count(e => !keys.Contains(Catalog.Identity(e)) && !ctx.Db.Current.DismissedDiscoveries.Contains(Catalog.Identity(e), StringComparer.OrdinalIgnoreCase));
            discoveries.Content = count > 0 ? $"＋ {count}" : "＋";
        }
        catch { discoveries.ToolTip = "桌面暂不可读，点击可重新发现"; }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { HidePopup(); PositionDock(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() => { HidePopup(); PositionDock(); });
    private nint WindowMessage(nint h, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x2E0 || msg == 0x1A)
            Dispatcher.BeginInvoke(PositionDock);
        if (msg == 0x312)
        {
            ctx.QuickTask();
            handled = true;
        }
        return 0;
    }
    private void RegisterShortcut()
    {
        if (source is null)
            return;
        Native.UnregisterHotKey(source.Handle, 1);
        var key = ctx.Db.Current.Preferences.QuickShortcut;
        ShortcutStatus = key.Length == 0 ? "已关闭" : Native.RegisterHotKey(source.Handle, 1, 0x4000 | 0x1 | 0x2, (uint)key[0]) ? $"Ctrl+Alt+{key} 已启用" : "快捷键被占用，可使用日历中的速记按钮。";
    }
    private void Render()
    {
        strip.Children.Clear();
        foreach (var cat in ctx.Db.Current.Categories)
        {
            var panel = new StackPanel();
            var mini = new UniformGrid { Columns = 2, Rows = 2, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
            var entries = ctx.Db.Current.Entries.Where(e => e.CategoryId == cat.Id).Take(4).ToArray();
            if (entries.Length == 0)
                mini.Children.Add(Design.Text("▦", 22, "Accent"));
            else
                foreach (var entry in entries)
                {
                    var icon = new Image { Width = 16, Height = 16, Margin = new(1) };
                    mini.Children.Add(icon);
                    _ = SetIcon(icon, entry);
                }
            panel.Children.Add(mini);
            var label = Design.Text(cat.Name, 12);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(label);
            var button = new Button { Content = panel, Width = 86, Height = 68, Padding = new(6), ToolTip = cat.Name, AllowDrop = true };
            System.Windows.Automation.AutomationProperties.SetName(button, cat.Name);
            button.MouseEnter += (_, _) => { pending = cat; anchor = button; openTimer.Stop(); openTimer.Start(); closeTimer.Stop(); };
            button.MouseLeave += (_, _) => { openTimer.Stop(); ScheduleClose(); };
            button.Click += (_, _) => { ShowCategory(cat, button); popup.Activate(); popup.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); };
            button.PreviewMouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(this); dragCategory = cat.Id; };
            button.PreviewMouseMove += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed || dragCategory != cat.Id || (e.GetPosition(this) - dragStart).Length < 8) return; holds++; try { DragDrop.DoDragDrop(button, new DataObject("GlassBogo.Category", cat.Id), DragDropEffects.Move); } finally { holds--; dragCategory = null; ScheduleClose(); } };
            button.DragOver += (_, e) => { openTimer.Stop(); closeTimer.Stop(); e.Effects = DragDropEffects.Link; e.Handled = true; };
            button.Drop += async (_, e) => { e.Handled = true; try { if (e.Data.GetData("GlassBogo.Category") is string id) ctx.Catalog.ReorderCategory(id, cat.Id); else await ImportDrop(e.Data, cat.Id); } catch (Exception ex) { Design.Error(ex); } };
            var menu = new ContextMenu();
            if (cat.Id != "inbox")
            {
                AddMenu(menu, "重命名", () => { holds++; try { Design.Prompt(this, "分类名称", cat.Name, name => ctx.Catalog.SaveCategory(cat.Id, name)); } finally { holds--; ScheduleClose(); } });
                AddMenu(menu, "移除分类（入口回到待整理）", () => ctx.Catalog.RemoveCategory(cat.Id));
            }
            AddMenu(menu, "添加入口", () => ctx.OpenWorkspace(true));
            menu.Opened += (_, _) => holds++;
            menu.Closed += (_, _) => { holds--; ScheduleClose(); };
            button.ContextMenu = menu;
            strip.Children.Add(button);
        }
        strip.Children.Add(Design.Button("＋ 分类", () => { holds++; try { Design.Prompt(this, "新建分类", save: name => ctx.Catalog.SaveCategory(null, name)); } finally { holds--; } }));
        PositionDock();
    }
    private async Task SetIcon(Image image, Entry entry)
    {
        try
        {
            var icon = await ctx.Icons.Get(entry);
            if (!image.Dispatcher.HasShutdownStarted)
                await image.Dispatcher.InvokeAsync(() => image.Source = icon);
        }
        catch { }
    }
    private void ShowCategory(Category category, Button button)
    {
        openTimer.Stop();
        closeTimer.Stop();
        hideGeneration++;
        ((FrameworkElement)popup.Content).BeginAnimation(OpacityProperty, null);
        if (active?.Id == category.Id && popup.IsVisible)
            return;
        active = category;
        anchor = button;
        page = 0;
        DrawPopup();
        var screen = Display.FromHandle(new WindowInteropHelper(this).Handle);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pos = button.PointToScreen(new Point(button.ActualWidth / 2, 0));
        popup.Left = Math.Clamp(pos.X / scale - popup.Width / 2, screen.WorkingArea.Left / scale, Math.Max(screen.WorkingArea.Left / scale, screen.WorkingArea.Right / scale - popup.Width));
        popup.Top = Math.Max(screen.WorkingArea.Top / scale, pos.Y / scale - popup.Height - 4);
        if (!popup.IsVisible)
        {
            popup.Show();
            Design.Reveal((FrameworkElement)popup.Content);
        }
        var pixelWidth = (int)(popup.Width * scale);
        var pixelHeight = (int)(popup.Height * scale);
        var pixelLeft = (int)Math.Clamp(pos.X - pixelWidth / 2d, screen.WorkingArea.Left, Math.Max(screen.WorkingArea.Left, screen.WorkingArea.Right - pixelWidth));
        var pixelTop = (int)Math.Max(screen.WorkingArea.Top, pos.Y - pixelHeight - 4 * scale);
        Native.SetWindowPos(new WindowInteropHelper(popup).Handle, 0, pixelLeft, pixelTop, pixelWidth, pixelHeight, 0x14);
    }
    private void DrawPopup()
    {
        if (active is null)
            return;
        var cacheKey = active.Id + ":" + page;
        if (popupPages.TryGetValue(cacheKey, out var cached))
        {
            popup.SetBody(cached.Content);
            visibleEntries = cached.Entries;
            return;
        }
        var entries = ctx.Db.Current.Entries.Where(e => e.CategoryId == active.Id).ToArray();
        var layout = new DockPanel();
        var title = Design.Text(active.Name, 19);
        title.Margin = new(6, 2, 4, 12);
        DockPanel.SetDock(title, Dock.Top);
        layout.Children.Add(title);
        var pages = Math.Max(1, (int)Math.Ceiling(entries.Length / 12d));
        page = Math.Clamp(page, 0, pages - 1);
        var footer = Design.Row(Design.Button("‹", () => { page = Math.Max(0, page - 1); DrawPopup(); }), Design.Text($"{page + 1} / {pages} · {entries.Length} 个入口", 12, "Muted"), Design.Button("›", () => { page = Math.Min(pages - 1, page + 1); DrawPopup(); }), Design.Button("管理", () => { HidePopup(); ctx.OpenWorkspace(); }));
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var grid = new UniformGrid { Columns = 4, Rows = 3 };
        var buttons = new List<Button>();
        foreach (var entry in entries.Skip(page * 12).Take(12))
        {
            var content = new StackPanel();
            var image = new Image { Width = 32, Height = 32 };
            _ = SetIcon(image, entry);
            content.Children.Add(image);
            var label = Design.Text(entry.Name, 12);
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(label);
            var b = new Button { Content = content, ToolTip = entry.Target, Padding = new(4), AllowDrop = true };
            System.Windows.Automation.AutomationProperties.SetName(b, entry.Name);
            var dragged = false;
            Point start = default;
            b.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(b); dragged = false; };
            b.PreviewMouseMove += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed || dragged || (e.GetPosition(b) - start).Length < 8) return; dragged = true; holds++; try { DragDrop.DoDragDrop(b, new DataObject("GlassBogo.Entry", entry.Id), DragDropEffects.Move); } finally { holds--; ScheduleClose(); } };
            b.Click += async (_, _) => { if (dragged) return; HidePopup(); await ctx.Open(entry); };
            b.Drop += (_, e) => { if (e.Data.GetData("GlassBogo.Entry") is string id) { e.Handled = true; Design.Guard(() => ctx.Catalog.Reorder(id, entry.Id)); } };
            var menu = new ContextMenu();
            AddMenu(menu, "移除入口（保留原文件）", () => ctx.Catalog.Remove([entry.Id]));
            menu.Opened += (_, _) => holds++;
            menu.Closed += (_, _) => { holds--; ScheduleClose(); };
            b.ContextMenu = menu;
            grid.Children.Add(b);
            buttons.Add(b);
        }
        if (entries.Length == 0)
            layout.Children.Add(Design.Text("把应用或桌面文件拖到分类方块，\n即可添加入口。", 14, "Muted"));
        else
            layout.Children.Add(grid);
        if (popupPages.Count >= 24)
            popupPages.Remove(popupPages.Keys.First());
        visibleEntries = buttons.ToArray();
        popupPages[cacheKey] = new(layout, visibleEntries);
        popup.SetBody(layout);
        popup.AllowDrop = true;
        popup.Drop -= PopupDrop;
        popup.Drop += PopupDrop;
    }
    private async void PopupDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || active is null)
            return;
        e.Handled = true;
        try
        {
            await ImportDrop(e.Data, active.Id);
        }
        catch (Exception ex) { Design.Error(ex); }
    }
    internal async Task ImportDrop(IDataObject data, string category)
    {
        if (data.GetData("GlassBogo.Entry") is string id)
        {
            ctx.Catalog.Move([id], category);
            return;
        }
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var entries = await Platform.Sta(() => paths.Select(p => Platform.Resolve(p) with { CategoryId = category }).ToArray());
            var result = ctx.Catalog.AddRange(entries);
            if (result.Existing > 0)
                ctx.LocateEntries(entries);
        }
    }
    private static void AddMenu(ContextMenu menu, string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => Design.Guard(action);
        menu.Items.Add(item);
    }
    private void ScheduleClose()
    {
        closeTimer.Stop();
        closeTimer.Start();
    }
    private void ChangePage(int direction)
    {
        if (active is null) return;
        var pages = Math.Max(1, (int)Math.Ceiling(ctx.Db.Current.Entries.Count(e => e.CategoryId == active.Id) / 12d));
        var next = Math.Clamp(page + direction, 0, pages - 1);
        if (next == page) return;
        page = next;
        DrawPopup();
    }
    private void PopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HidePopup(); e.Handled = true; return; }
        if (e.Key == Key.PageDown || e.Key == Key.PageUp)
        {
            ChangePage(e.Key == Key.PageDown ? 1 : -1);
            visibleEntries.FirstOrDefault()?.Focus();
            e.Handled = true;
            return;
        }
        var at = Array.FindIndex(visibleEntries, b => b.IsKeyboardFocusWithin);
        if (e.Key == Key.Enter && at >= 0)
        {
            visibleEntries[at].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -4, Key.Down => 4, _ => 0 };
        if (offset == 0 || visibleEntries.Length == 0) return;
        var next = at < 0 ? 0 : at + offset;
        if (next >= visibleEntries.Length) { ChangePage(1); next = 0; }
        else if (next < 0) { ChangePage(-1); next = visibleEntries.Length - 1; }
        visibleEntries[Math.Clamp(next, 0, visibleEntries.Length - 1)].Focus();
        e.Handled = true;
    }
    private void HidePopup(bool animate = false)
    {
        openTimer.Stop();
        active = null;
        var generation = ++hideGeneration;
        if (animate && popup.IsVisible && SystemParameters.ClientAreaAnimation)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => { if (hideGeneration == generation) popup.Hide(); };
            ((FrameworkElement)popup.Content).BeginAnimation(OpacityProperty, fade);
        }
        else popup.Hide();
    }
    private void SetCollapsed(bool value)
    {
        collapsed = value;
        Height = value ? 20 : 112;
        strip.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        PositionDock();
    }
    private Display Screen() => Display.AllScreens.FirstOrDefault(s => s.DeviceName == ctx.Db.Current.Preferences.Monitor) ?? Display.PrimaryScreen!;
    private void PositionDock()
    {
        var area = Screen().WorkingArea;
        var scale = Screen().Scale;
        Width = Math.Min(area.Width / scale - 32, Math.Max(440, ctx.Db.Current.Categories.Count * 94 + 250));
        Left = area.Left / scale + (area.Width / scale - Width) / 2;
        Top = area.Bottom / scale - Height - 8;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, (int)(area.Left + (area.Width - Width * scale) / 2), (int)(area.Bottom - (Height + 8) * scale), (int)(Width * scale), (int)(Height * scale), 0x14);
    }
    private void AssessForeground()
    {
        if (ctx.Closing)
            return;
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == new WindowInteropHelper(this).Handle || hwnd == new WindowInteropHelper(popup).Handle)
            return;
        var cls = new System.Text.StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        var desktop = cls.ToString() is "Progman" or "WorkerW";
        var bounds = Screen().Bounds;
        var full = !desktop && Native.GetWindowRect(hwnd, out var r) && r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
        if (full)
        {
            HidePopup();
            Hide();
        }
        else if (!IsVisible)
        {
            Show();
            PositionDock();
        }
    }
    internal void QaVerifyIcons()
    {
        var icons = strip.Children.OfType<Button>().Select(b => b.Content).OfType<StackPanel>().SelectMany(s => s.Children.OfType<UniformGrid>()).SelectMany(g => g.Children.OfType<Image>()).ToArray();
        if (icons.Length == 0 || icons.Any(i => i.Source is null))
            throw new InvalidOperationException("分类预览图标未加载。");
    }
    internal void QaVerifyNavigation()
    {
        var category = ctx.Db.Current.Categories.First(c => ctx.Db.Current.Entries.Count(e => e.CategoryId == c.Id) > 12);
        var index = ctx.Db.Current.Categories.IndexOf(category);
        ShowCategory(category, (Button)strip.Children[index]);
        var first = visibleEntries[0];
        ChangePage(1);
        if (page != 1 || ReferenceEquals(first, visibleEntries[0]))
            throw new InvalidOperationException("分类滚动未切换页面。");
        ChangePage(-1);
        if (page != 0 || !ReferenceEquals(first, visibleEntries[0]))
            throw new InvalidOperationException("分类缓存页面未复用。");
        popup.Activate();
        first.Focus();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        PopupKeyDown(popup, key);
        if (!key.Handled || !visibleEntries[1].IsKeyboardFocusWithin)
            throw new InvalidOperationException("分类方向键未移动焦点。");
        HidePopup();
    }
    internal double QaCycle(int index)
    {
        var cats = ctx.Db.Current.Categories;
        var at = index % cats.Count;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ShowCategory(cats[at], (Button)strip.Children[at]);
        popup.UpdateLayout();
        return watch.Elapsed.TotalMilliseconds;
    }
    internal void QaHide() => HidePopup();
}
