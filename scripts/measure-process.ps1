param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^GlassBogo-QA-[0-9a-f]{32}$')][string]$DesktopName,
    [ValidateRange(2, 60)][int]$IntervalSeconds = 5
)
$ErrorActionPreference = 'Stop'
$targetProcess = Get-Process -Id $ProcessId
if ($targetProcess.ProcessName -ne 'GlassBogo') { throw '仅采样 GlassBogo 进程。' }
$metadata = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId"
if ($metadata.CommandLine -notmatch '\s--qa\s') { throw '仅对通过隔离桌面宿主启动的 --qa 进程采样。' }
if ((Split-Path $metadata.ExecutablePath -Leaf) -ne 'GlassBogo.exe') { throw '被测程序不匹配。' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$samplesPath = Join-Path $output 'process-samples.jsonl'
$summaryPath = Join-Path $output 'process-summary.json'
if ((Test-Path -LiteralPath $samplesPath) -or (Test-Path -LiteralPath $summaryPath)) { throw '请使用新的采样目录，避免覆盖已有证据。' }

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class GlassBogoProcessMetrics
{
    private delegate bool EnumWindow(IntPtr window, IntPtr state);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindow callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint kind);
    public static int NativeWindows(Process process, string name)
    {
        // DESKTOP_READOBJECTS only. Never SetThreadDesktop or SwitchDesktop.
        var desktop = OpenDesktop(name, 0, false, 1);
        if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int count = 0;
            EnumWindow callback = (window, state) => { uint owner; GetWindowThreadProcessId(window, out owner); if (owner == process.Id) count++; return true; };
            if (!EnumDesktopWindows(desktop, callback, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return count;
        }
        finally { CloseDesktop(desktop); }
    }
}
'@

$samples = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$lastWall = $watch.Elapsed.TotalMilliseconds
$lastCpu = $targetProcess.TotalProcessorTime.TotalMilliseconds
try {
    while (-not $targetProcess.HasExited) {
        try {
            $targetProcess.Refresh()
            $wall = $watch.Elapsed.TotalMilliseconds
            $cpu = $targetProcess.TotalProcessorTime.TotalMilliseconds
            $cpuPercent = if ($samples.Count -eq 0) { $null } else { ($cpu - $lastCpu) / [Math]::Max(1, $wall - $lastWall) / [Environment]::ProcessorCount * 100 }
            $sample = [ordered]@{
                utc = [DateTime]::UtcNow.ToString('O')
                seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
                privateMiB = [Math]::Round($targetProcess.PrivateMemorySize64 / 1MB, 3)
                workingMiB = [Math]::Round($targetProcess.WorkingSet64 / 1MB, 3)
                cpuPercentOfMachine = $cpuPercent
                handles = $targetProcess.HandleCount
                threads = $targetProcess.Threads.Count
                userObjects = [GlassBogoProcessMetrics]::GetGuiResources($targetProcess.Handle, 1)
                gdiObjects = [GlassBogoProcessMetrics]::GetGuiResources($targetProcess.Handle, 0)
                nativeWindows = [GlassBogoProcessMetrics]::NativeWindows($targetProcess, $DesktopName)
            }
            $samples.Add([pscustomobject]$sample)
            $sample | ConvertTo-Json -Compress | Add-Content -LiteralPath $samplesPath -Encoding utf8
            $lastWall = $wall
            $lastCpu = $cpu
        } catch {
            if ($targetProcess.HasExited) { break }
            throw
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
} finally {
    $targetProcess.Dispose()
    if ($samples.Count -gt 0) {
        [ordered]@{
            processId = $ProcessId
            desktopName = $DesktopName
            sampleIntervalSeconds = $IntervalSeconds
            sampleCount = $samples.Count
            durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            first = $samples[0]
            last = $samples[$samples.Count - 1]
            maximumPrivateMiB = ($samples | Measure-Object -Property privateMiB -Maximum).Maximum
            maximumHandles = ($samples | Measure-Object -Property handles -Maximum).Maximum
            maximumNativeWindows = ($samples | Measure-Object -Property nativeWindows -Maximum).Maximum
            note = 'Read-only samples of the isolated QA process; sampled maxima are not instantaneous peaks. Excludes system DWM and other processes.'
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
        Write-Output "进程采样完成：$summaryPath"
    }
}
