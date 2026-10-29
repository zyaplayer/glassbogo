param(
    [Parameter(Mandatory)][string]$TestReportDirectory,
    [string]$EnduranceReportDirectory = '',
    [string]$FollowupChangeNote = '',
    [string]$PublishDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$reportDirectory = (Resolve-Path -LiteralPath $TestReportDirectory).Path
$report = Get-Content -LiteralPath (Join-Path $reportDirectory 'report.json') -Raw | ConvertFrom-Json
$events = Get-Content -LiteralPath (Join-Path $reportDirectory 'events.log') -Raw
if ($events -notmatch 'PASS completed' -or $report.baselineWindows -ne $report.finalWindows) { throw '当前构建的隔离测试未通过。' }
$enduranceDirectory = if ($EnduranceReportDirectory) { (Resolve-Path -LiteralPath $EnduranceReportDirectory).Path } else { $reportDirectory }
$endurance = Get-Content -LiteralPath (Join-Path $enduranceDirectory 'report.json') -Raw | ConvertFrom-Json
$enduranceEvents = Get-Content -LiteralPath (Join-Path $enduranceDirectory 'events.log') -Raw
if ($enduranceEvents -notmatch 'PASS completed' -or $endurance.stressSeconds -lt 1800 -or $endurance.baselineWindows -ne $endurance.finalWindows) { throw '候选包需要已完成的 30 分钟隔离长测，且窗口数不能增长。' }
$dllHash = (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.dll') -Algorithm SHA256).Hash
$match = [regex]::Match($events, 'assemblySHA256=([A-F0-9]{64})')
if (-not $match.Success -or $match.Groups[1].Value -ne $dllHash) { throw '发布 DLL 与当前构建的测试不一致，拒绝打包。' }
$enduranceMatch = [regex]::Match($enduranceEvents, 'assemblySHA256=([A-F0-9]{64})')
if (-not $enduranceMatch.Success) { throw '长测记录缺少构建指纹。' }
$sameBuild = $enduranceMatch.Groups[1].Value -eq $dllHash
if (-not $sameBuild -and ([string]::IsNullOrWhiteSpace($FollowupChangeNote) -or $report.stressSeconds -lt 180)) {
    throw '长测后修订必须说明差异并提供当前构建至少 180 秒的独立回归；不能把旧长测归于新构建。'
}
foreach ($required in @('GlassBogo.exe', 'THIRD-PARTY-NOTICES.txt', 'licenses', 'README.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "发布目录缺少 $required" }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = 'GlassBogo-LiquidGlass-candidate-win-x64-' + $dllHash.Substring(0, 12)
$archive = Join-Path $output ($name + '.zip')
$manifest = Join-Path $output ($name + '.json')
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $manifest)) { throw '候选包已存在，请使用新的输出目录。' }
$files = @(Get-ChildItem -LiteralPath $publish -Force | Select-Object -ExpandProperty FullName)
Compress-Archive -LiteralPath $files -DestinationPath $archive -CompressionLevel Optimal
[ordered]@{
    product = 'GlassBogo'
    stage = 'Liquid Glass candidate; manual visual validation pending'
    architecture = 'win-x64'
    createdUtc = [DateTime]::UtcNow.ToString('O')
    dllSha256 = $dllHash
    archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    testedEntries = $report.entries
    testedTasks = $report.tasks
    currentBuildStressSeconds = $report.stressSeconds
    currentBuildIterations = $report.iterations
    currentBuildCalendarCycles = $report.calendarCycles
    idlePrivateMiB = $report.idlePrivateMB
    idleCpuPercentOfMachine = $report.idleCpuPercent
    cachedLayoutP95Milliseconds = $report.p95Milliseconds
    baselineWindows = $report.baselineWindows
    finalWindows = $report.finalWindows
    endurance = [ordered]@{
        dllSha256 = $enduranceMatch.Groups[1].Value
        sameAsPackagedBuild = $sameBuild
        stressSeconds = $endurance.stressSeconds
        iterations = $endurance.iterations
        calendarCycles = $endurance.calendarCycles
        finalPrivateMiB = $endurance.finalPrivateMB
        followupChangeNote = $FollowupChangeNote
    }
    limits = 'No physical DPI/multi-monitor or real DWM visual approval. No install, data migration, desktop icon change or application launch performed by packaging.'
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifest -Encoding utf8
Write-Output "候选包：$archive"
Write-Output "构建与验收指纹：$manifest"
