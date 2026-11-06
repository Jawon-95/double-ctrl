# 用 Windows 自带的 .NET Framework 编译器构建，不需要安装任何 SDK。输出 bin/DoubleCtrl.exe
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$csc = Join-Path $framework 'csc.exe'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$references = @(
    "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
    "$wpf\UIAutomationClient.dll", "$wpf\UIAutomationTypes.dll",
    "$framework\System.Xaml.dll", "$framework\System.Net.Http.dll", "$framework\System.Web.Extensions.dll",
    "$framework\System.Windows.Forms.dll", "$framework\System.Drawing.dll"
) | ForEach-Object { "/reference:$_" }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /utf8output /codepage:65001 `
    "/out:$bin\DoubleCtrl.exe" `
    "/win32icon:$root\assets\icon.ico" `
    "/win32manifest:$root\assets\app.manifest" `
    "/resource:$root\src\Popup.xaml,Popup.xaml" `
    "/resource:$root\assets\icon.ico,icon.ico" `
    @references @sources

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item "$bin\DoubleCtrl.exe" | Select-Object Name, Length
