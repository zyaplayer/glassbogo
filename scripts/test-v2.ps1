param([string]$DotNetPath = 'dotnet', [switch]$Endurance, [ValidateRange(1,1800)][int]$Seconds = 10)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'artifacts/v2-ui-publish'
$harness = Join-Path $repo 'artifacts/v2-ui-harness'
$output = Join-Path $repo ('artifacts/v2-ui-qa-' + (Get-Date -Format yyyyMMdd-HHmmss))
Push-Location $repo
try {
    & $DotNetPath publish src/GlassBogo.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -warnaserror -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'V2 构建失败。' }
    Copy-Item -LiteralPath README.md,THIRD-PARTY-NOTICES.txt -Destination $publish -Force
    Copy-Item -LiteralPath licenses -Destination $publish -Recurse -Force
    & $DotNetPath build tests/GlassBogo.DesktopHarness -c Release -o $harness -warnaserror
    if ($LASTEXITCODE -ne 0) { throw '隔离宿主构建失败。' }
    $sources = Get-ChildItem src,tests,tools -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj','.json','.xaml' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    $sources | ForEach-Object { '{0} {1}' -f ([IO.Path]::GetRelativePath($repo,$_.FullName)),(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Set-Content artifacts/v2-frozen-source-hashes.txt -Encoding utf8
    if ($Endurance) { $Seconds = 1800 }
    & $DotNetPath (Join-Path $harness 'GlassBogo.DesktopHarness.dll') (Join-Path $publish 'GlassBogo.exe') $output $Seconds --v2-ui-qa
    $code = $LASTEXITCODE
    Write-Output "V2 验证输出：$output"
    if ($code -ne 0) { Get-Content -LiteralPath (Join-Path $output 'events.log') -Tail 12; throw "V2 隔离验证失败：$code" }
    $report = Get-Content -LiteralPath (Join-Path $output 'report.json') -Raw | ConvertFrom-Json
    if ($report.assemblySha256 -ne (Get-FileHash -LiteralPath (Join-Path $publish 'GlassBogo.dll') -Algorithm SHA256).Hash) { throw '测试指纹与当前发布 DLL 不一致。' }
} finally { Pop-Location }
