namespace GlassBogo.App;

// A single background writer and one replaceable pending snapshot. Repeated manual
// checks cannot grow a queue or perform filesystem work on the WPF dispatcher.
internal sealed class PreflightReportWriter(string path)
{
    private readonly object gate = new();
    private string? pending;
    private bool writing;
    private Task worker = Task.CompletedTask;
    internal void Save(string json)
    {
        lock (gate)
        {
            pending = json;
            if (writing) return;
            writing = true;
            worker = Task.Run(Drain);
        }
    }
    private void Drain()
    {
        try
        {
            while (true)
            {
                string json;
                lock (gate)
                {
                    if (pending is null) { writing = false; return; }
                    json = pending; pending = null;
                }
                Database.AtomicWrite(path, json);
            }
        }
        catch { lock (gate) writing = false; throw; }
    }
    internal Task FlushAsync() { lock (gate) return worker; }
}
