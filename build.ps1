# Builds the widget with the C# compiler that ships with Windows and installs it to %LOCALAPPDATA%\AiUsageWidget.
#   .\build.ps1              build and install
#   .\build.ps1 -NoInstall   build to bin\ only
#   .\build.ps1 -Snapshots   also render docs\*.png (uses the cached data in %LOCALAPPDATA%\AiUsageWidget)
param([switch]$NoInstall, [switch]$Snapshots)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$dest = Join-Path $env:LOCALAPPDATA 'AiUsageWidget'
$out = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $out, $dest, (Join-Path $dest 'icons') | Out-Null

$refs = 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll' |
    ForEach-Object { "/r:$fw\$_" }
# The app version comes from VERSION; release tags must match it (BWUI DESIGN.md §6.10).
$version = (Get-Content "$src\VERSION").Trim()
Set-Content "$out\AppInfo.g.cs" "namespace Bwui { public static class AppInfo { public const string Version = `"$version`"; } }" -Encoding utf8
$files = @("$src\Widget.cs", "$src\Data.cs", "$out\AppInfo.g.cs") + @(Get-ChildItem "$src\BWUI\*.cs" | ForEach-Object { $_.FullName })

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/win32manifest:$src\BWUI\bwui.manifest" "/out:$out\AiUsageWidget.exe" @refs @files
if ($LASTEXITCODE) { throw 'Widget build failed' }
& $csc /nologo /codepage:65001 /optimize+ /target:exe "/out:$out\AiUsageStatus.exe" "/r:$fw\System.Web.Extensions.dll" "$src\StatusLine.cs"
if ($LASTEXITCODE) { throw 'Status line build failed' }

if ($Snapshots) {
    $jobs = @(@('bars', 'light'), @('bars-dark', 'dark'), @('rings', 'light', 'ring'), @('rings-dark', 'dark', 'ring'))
    foreach ($j in $jobs) {
        $a = @('--snapshot', "$src\docs\$($j[0]).png") + $j[1..($j.Count - 1)]
        Start-Process "$out\AiUsageWidget.exe" -ArgumentList $a -Wait
    }
}
if ($NoInstall) { return }

# Stop a running widget so its exe can be replaced.
Get-Process AiUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item "$out\AiUsageWidget.exe", "$out\AiUsageStatus.exe" $dest -Force

# App icons, copied from the installed Store apps (their folders change on every update).
# Each app must be running during the build; otherwise the widget draws an accent square instead.
function Copy-AppIcon($procName, $asset, $target) {
    $p = Get-Process $procName -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*WindowsApps*' } | Select-Object -First 1
    if (-not $p) { return }
    $png = Join-Path (Split-Path (Split-Path $p.Path)) "assets\$asset"
    if (Test-Path $png) { Copy-Item $png (Join-Path $dest "icons\$target") -Force }
}
Copy-AppIcon 'claude' 'Square44x44Logo.targetsize-96_altform-unplated.png' 'claude.png'
Copy-AppIcon 'ChatGPT' 'Square44x44Logo.targetsize-96_altform-lightunplated.png' 'codex.png'
Copy-AppIcon 'ChatGPT' 'Square44x44Logo.targetsize-96_altform-unplated.png' 'codex-dark.png'
"Installed to $dest"
