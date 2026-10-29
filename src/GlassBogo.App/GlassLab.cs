using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace GlassBogo.App;

internal static class GlassLab
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal static string AssemblyHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GlassLab).Assembly.Location)));
    internal static void Run(string[] args)
    {
        var qa = args[0] == "--glass-lab-qa";
        if (qa) DesktopVisibilityTest.RequireIsolatedDesktop();
        var explicitOutput = args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        var output = Path.GetFullPath(explicitOutput ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo-Lab",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), e.Exception.ToString());
            e.Handled = true;
            if (!qa) MessageBox.Show("材质实验发生错误，诊断已保存到：\n" + output + "\n\n" + e.Exception.Message, "GlassBogo 材质实验");
            app.Shutdown(1);
        };
        if (qa)
        {
            app.Startup += async (_, _) => await GlassLabTest.Run(output, app);
            app.Run();
        }
        else
        {
            var lab = new GlassLabController(output, startCompatibility: args.Contains("--compat"));
            app.Run(lab);
        }
    }
}

// Independent WPF host. Structural compatibility switches create new *lab* HWNDs;
// production popup reuse is unaffected, and no lab selection is promoted to production.
internal sealed class GlassLabWindow : Window
{
    internal GlassHostOptions Options { get; private set; }
    internal GlassBackdropResult Result { get; private set; } = null!;
    internal GlassSurface Surface { get; }
    internal bool Decorated { get; }
    internal nint Handle => new WindowInteropHelper(this).Handle;
    private readonly TextBlock state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Action<string, object>? record;
    internal bool IsClosed { get; private set; }

    internal GlassLabWindow(string name, GlassHostOptions options, bool decorated, Action<string, object>? record = null)
    {
        Options = options; Decorated = decorated; this.record = record;
        Title = name; Width = 432; Height = 320; MinWidth = 260; MinHeight = 240;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = options.Layered; Background = Brushes.Transparent;
        Topmost = options.Topmost; ShowActivated = options.ActivateOnShow; ShowInTaskbar = true;
        FontFamily = new("Segoe UI Variable, Microsoft YaHei UI");
        UseLayoutRounding = true;
        if (options.Chrome && !options.Layered)
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = options.ExtendFrame ? new(-1) : new(0),
                ResizeBorderThickness = new(0), CornerRadius = new(22), UseAeroCaptionButtons = false });
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        layout.Children.Add(heading);
        var hint = new TextBlock { Text = "背景观察区\n\n拖动标题，把窗口放到壁纸不同颜色和细节上。\n再点击桌面，观察非激活状态。",
            FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hint, 1); layout.Children.Add(hint);
        var footer = new StackPanel();
        if (options.Layered && options.Backend == GlassBackend.ExperimentalAccent)
        {
            var toggle = new Button { Content = "仅透明 / 恢复模糊", Padding = new(8, 5, 8, 5), Margin = new(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            toggle.Click += (_, _) =>
            {
                Options = Options with { Backend = Options.Backend == GlassBackend.TransparentControl ? GlassBackend.ExperimentalAccent : GlassBackend.TransparentControl };
                RefreshMaterial();
            };
            footer.Children.Add(toggle);
        }
        footer.Children.Add(state); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = Surface = new GlassSurface(layout);
        SourceInitialized += (_, _) => RefreshMaterial();
        ContentRendered += (_, _) => Record("content-rendered");
        Activated += (_, _) => Record("activated");
        Deactivated += (_, _) => Record("deactivated");
        Closed += (_, _) => IsClosed = true;
    }

    internal void RefreshMaterial(GlassSystemState? policy = null)
    {
        if (Handle == 0 || IsClosed) return;
        var system = policy ?? GlassSystemState.Read();
        Result = GlassBackdrop.Apply(Handle, Options, system);
        if (HwndSource.FromHwnd(Handle)?.CompositionTarget is { } target)
            target.BackgroundColor = Result.Applied == GlassBackend.Opaque || !Options.TransparentTarget
                ? GlassBackdrop.Fallback(Options.Dark) : Colors.Transparent;
        Surface.Configure(Result.Applied == GlassBackend.Opaque, Decorated, Options, system);
        state.Text = Result.Applied == GlassBackend.TransparentControl ? "直接透明对照：原生模糊已关闭\n同一窗口、同一位置；比较条纹是否变清晰。"
            : Result.Applied == GlassBackend.Opaque ? "不透明回退：" + Result.FallbackReason
            : (Result.Applied == GlassBackend.DesktopAcrylic ? "官方 DWM 请求已接受" : "实验 Accent 请求已接受（非官方 Desktop Acrylic）")
                + "\n实际模糊和背景色彩：等待人工确认";
        Record("material-applied", system);
    }
    internal void SetTint(double alpha, double highlight)
    {
        Options = Options with { TintAlpha = alpha, HighlightAlpha = highlight };
        // Updating the foreground layers must not restart native material or recreate the HWND.
        Surface.Configure(Result.Applied == GlassBackend.Opaque, Decorated, Options, GlassSystemState.Read());
    }
    internal object Describe(GlassSystemState? system = null) => GlassBackdrop.DescribeWindow(Handle, this, Options, Result, system ?? GlassSystemState.Read());
    private void Record(string trigger, GlassSystemState? system = null)
    {
        if (Result is not null && !IsClosed) record?.Invoke(trigger, new { title = Title, host = Describe(system) });
    }
}

internal sealed class GlassLabController : Window
{
    private readonly string output;
    private readonly string hash = GlassLab.AssemblyHash;
    private Task writes = Task.CompletedTask;
    private int sequence;
    private readonly List<GlassLabWindow> samples = new();
    private GlassLabNativeWindow? native;
    private Window? referenceBoard;
    private bool compatibility;
    private readonly TextBlock comparisonLabel = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), FontSize = 12 };
    private DispatcherOperation? refresh;
    private bool closing;
    private readonly ComboBox backend = Select("官方 Desktop Acrylic", "实验 AccentPolicy（未公开接口）");
    private readonly ComboBox theme = Select("浅色", "深色");
    private readonly ComboBox rendering = Select("软件绘制（当前产品默认）", "硬件绘制（系统默认）");
    private readonly CheckBox chrome = Check("WindowChrome", true);
    private readonly CheckBox extend = Check("DWM 扩展整个框架", true);
    private readonly CheckBox layered = Check("分层窗口 AllowsTransparency（对照用）", false);
    private readonly CheckBox transparent = Check("透明合成目标", true);
    private readonly CheckBox topmost = Check("Topmost", true);
    private readonly CheckBox activate = Check("显示时激活", false);
    private readonly CheckBox owned = Check("设置 Owner", true);
    private readonly Slider tint = new() { Minimum = .06, Maximum = .12, Value = .08, TickFrequency = .01, IsSnapToTickEnabled = true, Width = 210 };
    private readonly Slider highlight = new() { Minimum = .10, Maximum = .25, Value = .18, TickFrequency = .01, IsSnapToTickEnabled = true, Width = 210 };
    private readonly TextBlock tintValue = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8), FontSize = 12 };
    private readonly ComboBox scenario = Select("浅色壁纸", "深色壁纸", "高饱和壁纸", "系统关闭透明效果");
    private readonly ComboBox verdict = Select("尚未判断", "符合观察项", "失败：仍为实色/无模糊");
    private readonly CheckBox colors = Check("底层颜色确实参与渲染", false);
    private readonly CheckBox blur = Check("细节被模糊，区别于直接透明", false);
    private readonly CheckBox inactive = Check("点击桌面后，非激活状态仍成立", false);
    private readonly TextBox notes = new() { MinHeight = 48, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly List<string> evidence = new();
    private readonly TextBlock evidenceLabel = new() { Text = "未选择证据文件", TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    internal GlassLabController(string output, bool ownsApplicationLifetime = true, bool startCompatibility = false)
    {
        this.output = output;
        Title = "GlassBogo V2 · 材质实验（人工验收前不接入产品）";
        Width = 570; Height = Math.Min(790, SystemParameters.WorkArea.Height - 40); MinWidth = 450; MinHeight = 400;
        Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
        FontFamily = new("Segoe UI Variable, Microsoft YaHei UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 18);
        Top = SystemParameters.WorkArea.Top + 18;
        var panel = new StackPanel { Margin = new(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Label("先看真实背景，再判断玻璃", 21));
        panel.Children.Add(comparisonLabel);
        panel.Children.Add(Row(ActionButton("兼容性并排对照", () => RebuildSamples(true)), ActionButton("颜色 / 条纹参照板", ShowReferenceBoard)));
        panel.Children.Add(Row(backend, theme));
        panel.Children.Add(rendering);
        panel.Children.Add(Row(chrome, extend));
        panel.Children.Add(layered);
        panel.Children.Add(Row(transparent, topmost, activate, owned));
        panel.Children.Add(Label("勾选分层窗口会禁用 WindowChrome；这是兼容性变量，不代表解决方案。", 12));
        panel.Children.Add(Row(Label("Tint"), tint, tintValue));
        panel.Children.Add(Row(Label("局部高光"), highlight));
        panel.Children.Add(Row(ActionButton("应用配置 / 重建对照窗口", Rebuild), ActionButton("保存当前诊断", SaveSnapshot)));
        panel.Children.Add(status);
        panel.Children.Add(new Separator { Margin = new(0, 10, 0, 10) });
        panel.Children.Add(Label("人工观察记录", 17));
        panel.Children.Add(Row(scenario, verdict));
        panel.Children.Add(colors); panel.Children.Add(blur); panel.Children.Add(inactive);
        panel.Children.Add(Label("说明：写明 A/B/Win32 哪些可见、哪些失败。关闭系统透明效果时记录回退表现。", 12));
        panel.Children.Add(notes);
        panel.Children.Add(Row(ActionButton("关联截图 / 录屏", PickEvidence), ActionButton("保存人工观察", SaveObservation)));
        panel.Children.Add(evidenceLabel);
        panel.Children.Add(Row(ActionButton("打开记录目录", () => Process.Start(new ProcessStartInfo(output) { UseShellExecute = true })),
            ActionButton("关闭实验", Close)));
        panel.Children.Add(Label("不会修改壁纸、透明设置、真实入口数据或原应用。不会自动录屏或持续采集桌面。", 12));
        tint.ValueChanged += (_, _) => TintChanged();
        highlight.ValueChanged += (_, _) => TintChanged();
        theme.SelectionChanged += (_, _) =>
        {
            tint.Minimum = theme.SelectedIndex == 1 ? .08 : .06;
            tint.Maximum = theme.SelectedIndex == 1 ? .16 : .12;
            tint.Value = theme.SelectedIndex == 1 ? .12 : .08;
        };
        layered.Checked += (_, _) => { chrome.IsChecked = false; chrome.IsEnabled = false; };
        layered.Unchecked += (_, _) => chrome.IsEnabled = true;
        Loaded += (_, _) =>
        {
            QueueWrite("session", new { schemaVersion = 2, stage = "P0MaterialPrototype", assemblySha256 = hash,
                visualGate = "PendingHumanEvidence", productionEnabled = false, system = GlassSystemState.Read() });
            TintChanged(); RebuildSamples(startCompatibility);
        };
        SystemParameters.StaticPropertyChanged += SystemChanged;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        Closed += async (_, _) =>
        {
            closing = true;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            refresh?.Abort(); CloseSamples(); referenceBoard?.Close();
            try { await writes; }
            finally { if (ownsApplicationLifetime) Application.Current.Shutdown(); }
        };
    }

    internal Task FlushDiagnostics() => writes;

    private void TintChanged()
    {
        tintValue.Text = tint.Value.ToString("F2");
        foreach (var sample in samples.Where(w => !w.IsClosed)) sample.SetTint(tint.Value, highlight.Value);
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRefresh();
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => ScheduleRefresh();
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => ScheduleRefresh();
    private void ScheduleRefresh()
    {
        if (closing) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closing || refresh?.Status == DispatcherOperationStatus.Pending) return;
            refresh = Dispatcher.BeginInvoke(() =>
            {
                foreach (var sample in samples.Where(w => !w.IsClosed)) sample.RefreshMaterial();
                native?.Refresh(); SaveSnapshot();
            }, DispatcherPriority.Background);
        });
    }
    private GlassHostOptions CurrentOptions() => new(
        backend.SelectedIndex == 0 ? GlassBackend.DesktopAcrylic : GlassBackend.ExperimentalAccent,
        chrome.IsChecked == true, extend.IsChecked == true, layered.IsChecked == true,
        transparent.IsChecked == true, topmost.IsChecked == true, activate.IsChecked == true,
        owned.IsChecked == true, theme.SelectedIndex == 1, tint.Value, highlight.Value);

    private void Rebuild() => RebuildSamples(false);

    private void RebuildSamples(bool compare)
    {
        CloseSamples();
        compatibility = compare;
        RenderOptions.ProcessRenderMode = rendering.SelectedIndex == 0 ? RenderMode.SoftwareOnly : RenderMode.Default;
        var options = CurrentOptions();
        var area = SystemParameters.WorkArea;
        var presets = compare ? GlassLabPresets.Compatibility(options) : new[]
        {
            new GlassLabPreset("A · 原生材质 / 无 WPF Tint", options, false),
            new GlassLabPreset("B · 薄 Tint + 局部高光", options, true),
            new GlassLabPreset("C · 不透明回退对照", options with { Backend = GlassBackend.Opaque }, false)
        };
        comparisonLabel.Text = compare
            ? "兼容对照：A 官方 DWM；B 非分层 Accent；C 分层 Accent；D 实色。\n三种材质使用相同薄 Tint。点击参照板可同时检查非激活表现。\n结构变量由预设固定；下方配置点“应用”后恢复普通 A/B/C 对照。"
            : "普通对照：A 原生材质；B 薄 Tint + 局部高光；C 实色。\n另有独立 Win32 基准。拖动标题进行同位置对比。";
        // Initial placement only. After opening, positioning and interaction belong to the human.
        for (var i = 0; i < presets.Length; i++)
        {
            var preset = presets[i];
            var sample = new GlassLabWindow(preset.Name, preset.Options, preset.Decorated, QueueWrite);
            if (preset.Options.Owned) sample.Owner = this;
            var columns = Math.Max(1, Math.Min(3, (int)((area.Width - 32) / 448)));
            sample.Left = area.Left + 16 + i % columns * 448;
            sample.Top = Math.Min(area.Top + 24 + i / columns * 340, area.Bottom - sample.Height - 12);
            samples.Add(sample); sample.Show();
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        native = new(options, new WindowInteropHelper(this).Handle,
            (int)((area.Left + 24) * dpi.DpiScaleX), (int)((area.Bottom - 300) * dpi.DpiScaleY));
        // Controller is not topmost; a user may move it without changing any preview's activation policy.
        SaveSnapshot();
    }

    private void ShowReferenceBoard()
    {
        if (referenceBoard is not null) { referenceBoard.Activate(); return; }
        referenceBoard = new Window
        {
            Title = "颜色 / 条纹参照板 · 诊断其他窗口背景合成，不是壁纸验收证据",
            Width = Math.Min(1400, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(740, SystemParameters.WorkArea.Height - 40),
            Left = SystemParameters.WorkArea.Left + 12, Top = SystemParameters.WorkArea.Top + 12,
            Owner = this, Background = Brushes.White, Content = new GlassLabReferencePattern()
        };
        referenceBoard.Closed += (_, _) => { referenceBoard = null; if (!closing) SaveSnapshot(); };
        referenceBoard.Show();
        SaveSnapshot();
    }
    private void CloseSamples()
    {
        foreach (var sample in samples.Where(w => !w.IsClosed)) sample.Close();
        samples.Clear(); native?.Dispose(); native = null;
    }
    private object Snapshot() => new
    {
        schemaVersion = 2, assemblySha256 = hash, utc = DateTime.UtcNow, visualGate = "PendingHumanEvidence",
        comparisonMode = compatibility ? "FixedCompatibilityPresets" : "ConfigurableMaterialLayers",
        referenceBoardVisible = referenceBoard?.IsVisible == true,
        backgroundEvidence = referenceBoard?.IsVisible == true ? "DiagnosticOtherWindowNotWallpaperProof" : "RequiresHumanSourceDescription",
        requestedControls = CurrentOptions(), windows = samples.Where(w => !w.IsClosed).Select(w => new { title = w.Title, host = w.Describe() }).ToArray(),
        nativeBaseline = native?.Handle != 0 ? native?.Describe() : null
    };
    private void SaveSnapshot()
    {
        QueueWrite("snapshot", Snapshot());
        var system = GlassSystemState.Read();
        status.Text = $"Windows {Environment.OSVersion.Version.ToString(3)}.{system.Revision} · 透明设置：{system.TransparencySetting}\n"
            + "当前只确认请求结果；实际玻璃效果尚未通过人工验收。\n记录目录：" + output;
    }
    private void PickEvidence()
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = "截图或视频|*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.gif|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        evidence.Clear(); evidence.AddRange(picker.FileNames);
        evidenceLabel.Text = string.Join("\n", evidence.Select(Path.GetFileName));
    }
    private void SaveObservation()
    {
        if (verdict.SelectedIndex == 0) { status.Text = "请先选择观察结果；未判断不会记为通过。"; return; }
        if (samples.Count != (compatibility ? 4 : 3) || samples.Any(w => w.IsClosed)) { status.Text = "请先重建完整的对照窗口。"; return; }
        var system = GlassSystemState.Read();
        if (scenario.SelectedIndex == 3 && system.TransparencySetting != false)
        { status.Text = "关闭透明效果的验收需要 Windows 设置实际为关闭；固定的实色对照不能代替这个场景。"; return; }
        var snapshot = Snapshot();
        var observation = new
        {
            source = "HumanEnteredNotAutomaticallyVerified", scenario = scenario.SelectedItem?.ToString(),
            verdict = verdict.SelectedItem?.ToString(), wallpaperColors = colors.IsChecked == true,
            blurredDetails = blur.IsChecked == true, inactiveVerified = inactive.IsChecked == true,
            notes = notes.Text, evidencePaths = evidence.ToArray(), snapshot,
            visualGate = "RequiresReviewOfAllEvidenceBeforeProductionIntegration"
        };
        QueueWrite("human-observation", observation);
        status.Text = "人工观察已排队保存；API 成功与该观察分别记录，不会自动启用产品材质。";
        verdict.SelectedIndex = 0; colors.IsChecked = blur.IsChecked = inactive.IsChecked = false;
        notes.Clear(); evidence.Clear(); evidenceLabel.Text = "未选择证据文件";
    }
    private void QueueWrite(string kind, object value)
    {
        var file = Path.Combine(output, $"{++sequence:D4}-{kind}.json");
        var json = JsonSerializer.Serialize(value, GlassLab.Json);
        var previous = writes;
        writes = Task.Run(async () =>
        {
            await previous;
            await File.WriteAllTextAsync(file, json);
        });
        _ = writes.ContinueWith(t => Dispatcher.BeginInvoke(() =>
            status.Text = "诊断保存失败：" + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
    }
    private static TextBlock Label(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 8, 7) };
    private static ComboBox Select(params string[] labels) => new() { ItemsSource = labels, SelectedIndex = 0, MinWidth = 112, Margin = new(0, 4, 8, 4), Padding = new(5) };
    private static CheckBox Check(string label, bool selected) => new() { Content = label, IsChecked = selected, Margin = new(0, 5, 12, 5) };
    private static WrapPanel Row(params UIElement[] items) { var row = new WrapPanel(); foreach (var item in items) row.Children.Add(item); return row; }
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(10, 7, 10, 7), Margin = new(0, 7, 8, 3) };
        button.Click += (_, _) => action(); return button;
    }
}
