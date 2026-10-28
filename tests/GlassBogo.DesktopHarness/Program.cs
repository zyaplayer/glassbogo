using System.Runtime.InteropServices;
using System.Text;
if (args.Length < 2) throw new ArgumentException("Usage: DesktopHarness app.exe output-directory [stress-seconds]");
var name = "GlassBogo-QA-" + Guid.NewGuid().ToString("N");
var desktop = Win.CreateDesktop(name, 0, 0, 0, 0x1FF, 0);
if (desktop == 0) throw new System.ComponentModel.Win32Exception();
try
{
    var startup = new Win.Startup { Size = Marshal.SizeOf<Win.Startup>(), Desktop = "WinSta0\\" + name };
    var command = new StringBuilder($"\"{Path.GetFullPath(args[0])}\" --qa \"{Path.GetFullPath(args[1])}\" {(args.Length > 2 ? args[2] : "10")}");
    if (!Win.CreateProcess(null, command, 0, 0, false, 0x08000000, 0, Path.GetDirectoryName(Path.GetFullPath(args[0])), ref startup, out var process))
        throw new System.ComponentModel.Win32Exception();
    Console.WriteLine($"Isolated desktop={name}; child PID={process.Id}");
    Win.CloseHandle(process.Thread);
    try
    {
        Win.WaitForSingleObject(process.Process, uint.MaxValue);
        Win.GetExitCodeProcess(process.Process, out var code);
        Environment.ExitCode = (int)code;
        Console.WriteLine("QA exit=" + code);
    }
    finally { Win.CloseHandle(process.Process); }
}
finally { Win.CloseDesktop(desktop); }
static class Win
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Startup
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public short Show, ReservedSize; public nint ReservedPointer, StdIn, StdOut, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo
    {
        public nint Process, Thread; public int Id, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcess(string? app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(nint handle, out uint code);
}
