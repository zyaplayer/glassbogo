using System.Text;
using System.Windows.Threading;

namespace GlassBogo.App;

internal interface IDesktopIconBackend
{
    bool Visible { get; }
    void Show(bool visible);
}

internal sealed class WindowsDesktopIconBackend : IDesktopIconBackend
{
    private static nint List => Native.DesktopList() is var hwnd && hwnd != 0 ? hwnd : throw new InvalidOperationException("桌面尚未就绪，恢复记录已保留，请稍后重试。");
    public bool Visible => Native.IsWindowVisible(List);
    public void Show(bool visible) => Native.ShowWindow(List, visible ? 4 : 0);
}

internal sealed class MemoryDesktopIconBackend : IDesktopIconBackend
{
    public bool Visible { get; private set; } = true;
    public void Show(bool visible) => Visible = visible;
}

// Owns only the desktop icon view's visibility. It never touches any desktop file.
internal sealed class DesktopIconController : IAsyncDisposable
{
    private readonly string recovery;
    private readonly IDesktopIconBackend backend;
    private readonly object gate = new();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Native.WinEventProc callback;
    private nint hook;
    private volatile bool disposed, hidden;
    private int queued;
    internal string? LastError { get; private set; }
    internal bool Hidden => hidden;
    internal DesktopIconController(string directory, IDesktopIconBackend? backend = null, bool listen = true)
    {
        recovery = Path.Combine(directory, "desktop-visibility.json");
        this.backend = backend ?? new WindowsDesktopIconBackend();
        callback = (_, _, window, obj, child, _, _) =>
        {
            if (disposed || !hidden || obj != 0 || child != 0 || window == 0) return;
            var cls = new StringBuilder(64); Native.GetClassName(window, cls, cls.Capacity);
            if (cls.ToString() != "SysListView32" || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
            try { dispatcher.BeginInvoke(async () =>
            {
                Interlocked.Exchange(ref queued, 0);
                try { await Task.Run(Reconcile); } catch (Exception ex) { LastError = ex.Message; }
            }); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
        };
        if (listen) hook = Native.SetWinEventHook(0x8000, 0x8002, 0, callback, 0, 0, 2);
    }
    internal void Initialize(bool desired)
    {
        lock (gate)
        {
            Restore();
            SetHidden(desired);
        }
    }
    internal void SetHidden(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (value)
            {
                var visible = backend.Visible;
                if (!File.Exists(recovery)) Database.AtomicWrite(recovery, visible ? "true" : "false");
                backend.Show(false);
            }
            else Restore();
            hidden = value; LastError = null;
        }
    }
    internal void Reconcile()
    {
        lock (gate)
        {
            if (disposed || !hidden) return;
            // Explorer recreated its view. Preserve the original recovery point.
            if (backend.Visible) backend.Show(false);
        }
    }
    private void Restore()
    {
        if (!File.Exists(recovery)) return;
        if (!bool.TryParse(File.ReadAllText(recovery), out var visible)) throw new InvalidDataException("桌面恢复记录无效，原记录已保留。");
        backend.Show(visible);
        File.Delete(recovery);
    }
    public async ValueTask DisposeAsync()
    {
        if (hook != 0) { Native.UnhookWinEvent(hook); hook = 0; }
        await Task.Run(() =>
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                try { Restore(); hidden = false; }
                catch (Exception ex) { LastError = ex.Message; throw; }
            }
        });
    }
}
