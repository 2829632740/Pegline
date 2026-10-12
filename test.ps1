$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw "WPF"
New-Item -ItemType Directory -Force build | Out-Null
$refs = @(
    "/r:$wpf\PresentationCore.dll", "/r:$wpf\PresentationFramework.dll", "/r:$wpf\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll", "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
    "/r:Microsoft.VisualBasic.dll", "/r:System.dll", "/r:System.Core.dll"
)
& "$fw\csc.exe" /nologo /target:exe /main:Pegline.PreviewTests /out:build\PreviewTests.exe /win32manifest:src\app.manifest $refs (Get-ChildItem src\*.cs).FullName (Get-ChildItem tests\*.cs).FullName
if ($LASTEXITCODE) { throw "Test build failed" }
& build\PreviewTests.exe
if ($LASTEXITCODE) { throw "Preview tests failed" }
