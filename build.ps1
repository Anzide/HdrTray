# Builds HdrTray.exe with the C# compiler that ships with Windows
# (.NET Framework 4.x), so no SDK or Visual Studio is needed.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Add -Docs to also re-render the README images in docs\.

param([switch]$Docs)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found; .NET Framework 4.x is required." }

$out = Join-Path $root 'build'
$obj = Join-Path $out 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

function Invoke-Csc([string[]]$cscArgs) {
    # /codepage:65001: sources are UTF-8 without BOM (Chinese UI strings).
    & $csc /nologo /utf8output /codepage:65001 /warn:4 /optimize+ @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
}

$src = Join-Path $root 'src'
$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'

Write-Host 'Generating icon...'
$iconGen = Join-Path $obj 'IconGen.exe'
$ico = Join-Path $obj 'app.ico'
Invoke-Csc (@('/target:exe', "/out:$iconGen") + $refs + @((Join-Path $root 'tools\IconGen.cs'), (Join-Path $src 'IconRenderer.cs')))
& $iconGen $ico
if ($LASTEXITCODE -ne 0) { throw 'IconGen failed' }

Write-Host 'Building HdrTray.exe...'
$exe = Join-Path $out 'HdrTray.exe'
$sources = Get-ChildItem -LiteralPath $src -Filter *.cs | ForEach-Object { $_.FullName }
Invoke-Csc (@('/target:winexe', "/out:$exe", "/win32manifest:$(Join-Path $src 'app.manifest')", "/win32icon:$ico") + $refs + $sources)

Write-Host 'Building HdrStatus.exe (diagnostics)...'
$status = Join-Path $out 'HdrStatus.exe'
Invoke-Csc (@('/target:exe', "/out:$status") + $refs + @((Join-Path $root 'tools\HdrStatus.cs'), (Join-Path $src 'DisplayConfig.cs'), (Join-Path $src 'Strings.cs')))

if ($Docs) {
    Write-Host 'Rendering README images...'
    $docImages = Join-Path $obj 'DocImages.exe'
    Invoke-Csc (@('/target:exe', "/out:$docImages") + $refs + @((Join-Path $root 'tools\DocImages.cs'), (Join-Path $src 'IconRenderer.cs')))
    & $docImages (Join-Path $root 'docs')
    if ($LASTEXITCODE -ne 0) { throw 'DocImages failed' }
}

Write-Host "Done: $exe"
