using GlassBogo.App;
using System.IO;

namespace GlassBogo.Preflight;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--v2-capture-check")
            V2Preflight.CaptureCheck(Path.GetFullPath(args[1]));
        else if (args.Length > 0 && args[0] is "--v2-preflight" or "--v2-preflight-qa")
            V2Preflight.Run(args);
        else if (args.Length == 0)
            V2Preflight.Run(["--v2-preflight"]);
        else
            Environment.ExitCode = 64;
    }
}
