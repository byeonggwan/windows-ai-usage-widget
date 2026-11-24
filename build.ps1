# Builds the widget with the C# compiler that ships with Windows and installs it to %LOCALAPPDATA%\AiUsageWidget.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$dest = Join-Path $env:LOCALAPPDATA 'AiUsageWidget'
$out = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $out, $dest, (Join-Path $dest 'icons') | Out-Null

$refs = @("$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll", "$fw\System.Web.Extensions.dll") | ForEach-Object { "/r:$_" }

& $csc /nologo /codepage:65001 /optimize+ /target:winexe "/out:$out\AiUsageWidget.exe" @refs "$src\Widget.cs"
if ($LASTEXITCODE) { throw 'Widget build failed' }
& $csc /nologo /codepage:65001 /optimize+ /target:exe "/out:$out\AiUsageStatus.exe" "/r:$fw\System.Web.Extensions.dll" "$src\StatusLine.cs"
if ($LASTEXITCODE) { throw 'Status line build failed' }

# Stop a running widget so its exe can be replaced.
Get-Process AiUsageWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item "$out\AiUsageWidget.exe", "$out\AiUsageStatus.exe" $dest -Force

# App icons, copied from the installed Store apps (their folders change on every update).
# Each app must be running during the build; otherwise the widget draws a colored dot instead.
function Copy-AppIcon($procName, $asset, $target) {
    $p = Get-Process $procName -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*WindowsApps*' } | Select-Object -First 1
    if (-not $p) { return }
    $png = Join-Path (Split-Path (Split-Path $p.Path)) "assets\$asset"
    if (Test-Path $png) { Copy-Item $png (Join-Path $dest "icons\$target") -Force }
}
Copy-AppIcon 'claude' 'Square44x44Logo.targetsize-96_altform-unplated.png' 'claude.png'
Copy-AppIcon 'ChatGPT' 'Square44x44Logo.targetsize-96_altform-lightunplated.png' 'codex.png'
"Installed to $dest"
