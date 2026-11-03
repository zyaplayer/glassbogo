using System.Runtime.InteropServices;

namespace GlassBogo.App;

// English PDH names work on localized Windows. Sample on a worker, never UI.
// Reports the busiest engine for this process, not an invented aggregate percent.
internal sealed class GpuUsage : IDisposable
{
    private nint query, counter;
    private string? failure;
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct Item { public nint Name; public Value Value; }
    internal GpuUsage()
    {
        var code = PdhOpenQuery(null, 0, out query);
        if (code == 0) code = PdhAddEnglishCounter(query, $"\\GPU Engine(pid_{Environment.ProcessId}_*)\\Utilization Percentage", 0, out counter);
        if (code == 0) code = PdhCollectQueryData(query);
        if (code != 0) failure = $"PDH 0x{code:X8}";
    }
    internal (double? Percent, string? Error) Sample()
    {
        if (failure is not null) return (null, failure);
        var code = PdhCollectQueryData(query);
        if (code != 0) return (null, $"PDH collect 0x{code:X8}");
        uint bytes = 0;
        code = PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out var count, 0);
        if (code != 0x800007D2 || bytes == 0 || bytes > 1024 * 1024) return (null, $"PDH size 0x{code:X8}");
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            code = PdhGetFormattedCounterArray(counter, 0x200, ref bytes, out count, buffer);
            if (code != 0 || count > 4096) return (null, $"PDH values 0x{code:X8}");
            var max = 0d; var valid = false; var size = Marshal.SizeOf<Item>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * size);
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Number)) continue;
                valid = true; max = Math.Max(max, item.Value.Number);
            }
            return valid ? (max, null) : (null, "No valid engine samples");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() { if (query != 0) { PdhCloseQuery(query); query = 0; } }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
