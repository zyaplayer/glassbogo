param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$publish = Join-Path $repo "artifacts/v2-preflight-$stamp/publish"
$output = Join-Path $repo "artifacts/v2-preflight-$stamp/reports"
$harness = Join-Path $repo 'artifacts/v2-preflight-harness'
Push-Location $repo
try {
    & $DotNetPath publish tools/GlassBogo.Preflight -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -warnaserror -o $publish
    if ($LASTEXITCODE -ne 0) { throw '前置验证程序构建失败。' }
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.txt -Destination $publish
    Copy-Item -LiteralPath README.md -Destination $publish
    Copy-Item -LiteralPath licenses -Destination $publish -Recurse
    '@start "" "%~dp0GlassBogo.Preflight.exe"' | Set-Content -LiteralPath (Join-Path $publish 'Open-V2-Preflight.cmd') -Encoding ascii
    & $DotNetPath build tests/GlassBogo.DesktopHarness -c Release -o $harness -warnaserror
    if ($LASTEXITCODE -ne 0) { throw '隔离测试宿主构建失败。' }
    # Headless check on the current desktop: reads a GPU frame but creates no UI,
    # saves no captured pixels and does not initialize product services.
    $captureProcess = Start-Process -FilePath (Join-Path $publish 'GlassBogo.Preflight.exe') -ArgumentList @('--v2-capture-check', ('"' + (Join-Path $output 'headless') + '"')) -WindowStyle Hidden -Wait -PassThru
    $captureExit = $captureProcess.ExitCode
    $captureReport = Get-Content -LiteralPath (Join-Path $output 'headless/capture-check.json') -Raw | ConvertFrom-Json
    $appHash = (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.dll') -Algorithm SHA256).Hash
    if ($captureReport.status -ne 'PASS' -or $captureReport.assemblySha256 -ne $appHash) { $captureExit = 2 }
    & $DotNetPath (Join-Path $harness 'GlassBogo.DesktopHarness.dll') (Join-Path $publish 'GlassBogo.Preflight.exe') (Join-Path $output 'isolated') 10 --v2-preflight-qa
    $qaExit = $LASTEXITCODE
    $qaReport = Get-Content -LiteralPath (Join-Path $output 'isolated/v2-preflight.json') -Raw | ConvertFrom-Json
    if ($qaReport.assemblySha256 -ne $appHash -or @($qaReport.checks | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0 -or -not @($qaReport.checks | Where-Object { $_.name -eq 'AutomatedChecks' -and $_.status -eq 'PASS' }).Count) { $qaExit = 2 }
    [ordered]@{
        stage = 'GlassBogo 2.0 preflight, not a product candidate'
        productionGate = 'CLOSED'
        captureExitCode = $captureExit
        isolatedExitCode = $qaExit
        manualDesktopAcceptance = 'PENDING'
        appSha256 = $appHash
        coreSha256 = (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.Core.dll') -Algorithm SHA256).Hash
        entrySha256 = (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.Preflight.exe') -Algorithm SHA256).Hash
        limits = 'No installation, user database migration or active-desktop UI automation. No captured pixels persisted.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
    Write-Output "实验程序：$publish"
    Write-Output "诊断报告：$output"
    Write-Output '真实桌面 Win+D、采样排除像素与视觉仍需显式打开实验程序验收；不会自动启动活动桌面窗口。'
    if ($captureExit -ne 0 -or $qaExit -ne 0) { throw "前置验证未全部通过（采样=$captureExit，隔离=$qaExit），禁止产品接入。" }
} finally { Pop-Location }
