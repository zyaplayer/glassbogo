param([string]$PublishDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish'))
$ErrorActionPreference = 'Stop'
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installation = Join-Path $programs 'GlassBogo'
$staging = Join-Path $programs ('GlassBogo.staging-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $programs 'GlassBogo.previous'
function Assert-ManagedPath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $programs -or $resolved -notin @($installation, $staging, $previous)) { throw "非受管路径：$resolved" }
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "安装路径不能是链接：$resolved" }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'GlassBogo.exe'))) { throw '请先运行 build.ps1' }
if (Get-Process -Name GlassBogo -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $installation 'GlassBogo.exe') }) { throw '请从托盘退出 GlassBogo 后更新。' }
New-Item -ItemType Directory -Path $programs -Force | Out-Null
Assert-ManagedPath $installation
Assert-ManagedPath $staging
Assert-ManagedPath $previous
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $staging -Recurse -Force
if (Test-Path -LiteralPath $previous) { Assert-ManagedPath $previous; Remove-Item -LiteralPath $previous -Recurse -Force }
if (Test-Path -LiteralPath $installation) { Assert-ManagedPath $installation; Move-Item -LiteralPath $installation -Destination $previous }
try { Assert-ManagedPath $staging; Move-Item -LiteralPath $staging -Destination $installation }
catch { if (Test-Path -LiteralPath $previous) { Assert-ManagedPath $previous; Move-Item -LiteralPath $previous -Destination $installation }; throw }
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $desktop 'GlassBogo.lnk'))
$shortcut.TargetPath = Join-Path $installation 'GlassBogo.exe'
$shortcut.WorkingDirectory = $installation
$shortcut.IconLocation = $shortcut.TargetPath + ',0'
$shortcut.Description = 'GlassBogo 桌面入口工作台'
$shortcut.Save()
if (Test-Path -LiteralPath $previous) { Assert-ManagedPath $previous; Remove-Item -LiteralPath $previous -Recurse -Force }
Write-Output "已安装：$installation。数据保留在 LOCALAPPDATA\GlassBogo。"
