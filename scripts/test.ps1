param([string]$DotNetPath = 'dotnet', [switch]$DesktopSmoke, [switch]$Endurance)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & $DotNetPath run --project tests/GlassBogo.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw '核心测试失败' }
    & "$PSScriptRoot/build.ps1" -DotNetPath $DotNetPath
    & $DotNetPath publish tests/FaultPlugin -c Release -r win-x64 --self-contained true -o artifacts/fault-plugin
    if ($LASTEXITCODE -ne 0) { throw '故障插件构建失败' }
    & $DotNetPath run --project tests/GlassBogo.WindowsTests -c Release -- $repo
    if ($LASTEXITCODE -ne 0) { throw 'Windows 服务测试失败' }
    if ($DesktopSmoke -or $Endurance) {
        $seconds = if ($Endurance) { 1800 } else { 10 }
        $output = Join-Path $repo ('artifacts/qa-' + (Get-Date -Format yyyyMMdd-HHmmss))
        & $DotNetPath build tests/GlassBogo.DesktopHarness -c Release -o artifacts/harness
        if ($LASTEXITCODE -ne 0) { throw '隔离桌面测试宿主构建失败' }
        $env:GLASSBOGO_QA_PLUGIN = (Resolve-Path 'artifacts/sample-plugin').Path
        & $DotNetPath artifacts/harness/GlassBogo.DesktopHarness.dll artifacts/publish/GlassBogo.exe $output $seconds
        $qaExit=$LASTEXITCODE
        Remove-Item Env:GLASSBOGO_QA_PLUGIN
        if ($qaExit -ne 0) { throw '隔离桌面测试失败' }
        Write-Output "验收输出：$output"
    }
} finally { Pop-Location }
