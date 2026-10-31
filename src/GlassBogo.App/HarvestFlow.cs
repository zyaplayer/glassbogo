namespace GlassBogo.App;

/// <summary>
/// 桌面收纳界面流程：预览 → 确认 → 移动 → 隐藏图标 → 生成入口。
/// 真正的文件操作全部在 <see cref="DesktopHarvest"/> 里，这里只负责让用户先看清再动手。
/// </summary>
internal static class HarvestFlow
{
    internal static void Start(Window owner, AppContext ctx)
    {
        try
        {
            var root = DesktopHarvest.NewArchiveRoot(DateTime.Now);
            var plan = ctx.Harvest.Plan(ctx.Desktop.ItemPaths(), root, DateTime.Now);
            if (plan.Items.Count == 0)
            {
                MessageBox.Show("桌面上没有可收纳的项目。", "GlassBogo");
                return;
            }
            var rows = plan.Items.Select(i => new Row(i)).ToArray();
            var dialog = new GlassWindow { Owner = owner, Width = 700, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new DockPanel();
            var header = new StackPanel { Margin = new(0, 0, 0, 12) };
            header.Children.Add(Design.Text($"把桌面上的 {rows.Length} 项搬进归档夹。只移动，不删除，同名自动加序号。", 14));
            var path = Design.Text("搬到：" + root, 12, "Muted");
            path.Margin = new(0, 6, 0, 0);
            header.Children.Add(path);
            var count = Design.Text("", 12, "Muted");
            count.Margin = new(0, 6, 0, 0);
            header.Children.Add(count);
            DockPanel.SetDock(header, Dock.Top);
            body.Children.Add(header);

            var list = new StackPanel();
            foreach (var (kind, label) in new[] { (HarvestKind.Shortcut, "快捷方式"), (HarvestKind.Folder, "文件夹"), (HarvestKind.Document, "文档与图片") })
            {
                var group = rows.Where(r => r.Item.Kind == kind).ToArray();
                if (group.Length == 0)
                    continue;
                var title = Design.Text($"{label} · {group.Length}", 13, "Muted");
                title.Margin = new(0, 10, 0, 4);
                list.Children.Add(title);
                foreach (var row in group)
                {
                    var box = new CheckBox { Content = row.Item.Name, IsChecked = true, ToolTip = row.Item.Source, Margin = new(6, 3, 0, 3) };
                    box.Click += (_, _) => { row.Selected = box.IsChecked == true; };
                    row.Box = box;
                    list.Children.Add(box);
                }
            }
            body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
            var message = Design.Text("", 12, "Warning");
            footer.Children.Add(message);
            Button confirm = null!;
            void Update()
            {
                var selected = rows.Count(r => r.Selected);
                count.Text = selected == rows.Length ? $"将收纳全部 {selected} 项" : $"已选 {selected} / {rows.Length} 项";
                confirm.Content = $"确认收纳（{selected} 项）";
                confirm.IsEnabled = selected > 0;
            }
            void SetAll(bool value)
            {
                foreach (var row in rows)
                {
                    row.Selected = value;
                    row.Box.IsChecked = value;
                }
                Update();
            }
            confirm = Design.Button("确认收纳", () =>
            {
                var chosen = rows.Where(r => r.Selected).Select(r => r.Item.Source).ToArray();
                HarvestBatch batch;
                try
                {
                    batch = ctx.Harvest.Execute(ctx.Harvest.Plan(chosen, root, DateTime.Now));
                }
                catch (HarvestException ex)
                {
                    message.Text = ex.Message;
                    return;
                }
                dialog.Close();
                Finish(owner, ctx, batch);
            });
            footer.Children.Add(Design.Row(
                Design.Button("全选", () => SetAll(true)),
                Design.Button("全不选", () => SetAll(false)),
                Design.Button("取消", () => dialog.Close()),
                confirm));
            DockPanel.SetDock(footer, Dock.Bottom);
            body.Children.Add(footer);

            dialog.SetPage("桌面收纳 · 先看一眼，再搬", body);
            Update();
            dialog.ShowDialog();
        }
        catch (Exception ex) { Design.Error(ex); }
    }

    private static void Finish(Window owner, AppContext ctx, HarvestBatch batch)
    {
        var moved = batch.Items.Where(i => i.Moved).ToArray();
        var entries = moved.Select(i => new Entry(Guid.NewGuid().ToString("N"), DisplayName(i), i.Destination, "path", "", Path.GetDirectoryName(i.Destination) ?? "", "inbox")).ToArray();
        var added = entries.Length == 0 ? (Added: 0, Existing: 0) : ctx.Catalog.AddRange(entries);
        string icons;
        try
        {
            ctx.Desktop.HideIcons();
            icons = "桌面图标已隐藏（退出 GlassBogo 会自动恢复）。";
        }
        catch (Exception ex) { icons = "桌面图标未隐藏：" + ex.Message; }
        ctx.Db.Update(s => s.Preferences = s.Preferences with { Onboarded = true });
        MessageBox.Show(owner, $"已收纳 {moved.Length} 项到\n{batch.ArchiveRoot}\n\n新增 {added.Added} 个应用到「全部应用」，已有 {added.Existing} 个。\n{icons}\n\n撤销：工作台 → 设置 → 桌面收纳 → 撤销上次收纳", "GlassBogo");
    }

    private static string DisplayName(HarvestEntry entry) => entry.Kind == HarvestKind.Folder ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name);

    private sealed class Row(HarvestItem item)
    {
        public HarvestItem Item { get; } = item;
        public bool Selected { get; set; } = true;
        public CheckBox Box { get; set; } = null!;
    }
}
