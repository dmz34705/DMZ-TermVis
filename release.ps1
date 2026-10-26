# Builds self-contained DMZ TermVis release archives for Windows and macOS into dist\.
# Requires the .NET 8 SDK and Python 3 (used to write the macOS tarballs with the executable bit set).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = '1.1'
$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

$targets = @(
    @{ Rid = 'win-x64';   Name = "DMZ-TermVis-$version-windows-x64";     Exe = 'termvis.exe' },
    @{ Rid = 'osx-arm64'; Name = "DMZ-TermVis-$version-macos-arm64";     Exe = 'termvis' },
    @{ Rid = 'osx-x64';   Name = "DMZ-TermVis-$version-macos-intel-x64"; Exe = 'termvis' }
)

foreach ($t in $targets) {
    $out = Join-Path $dist $t.Name
    dotnet publish TermVis.csproj -c Release -r $t.Rid -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Rid)" }
    Copy-Item README.md, LICENSE $out
    if ($t.Rid -like 'osx-*') { Copy-Item setup-mac.sh $out }

    if ($t.Rid -like 'win-*') {
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath "$out.zip" -Force
    } else {
        # Windows zip tools drop Unix permissions, so write the tarball with mode 755 on the binary.
        python -c @"
import sys, tarfile, os
src, name, exe = sys.argv[1], sys.argv[2], sys.argv[3]
with tarfile.open(src + '.tar.gz', 'w:gz') as tar:
    for f in sorted(os.listdir(src)):
        info = tar.gettarinfo(os.path.join(src, f), arcname=name + '/' + f)
        info.mode = 0o755 if f in (exe, 'setup-mac.sh') else 0o644
        info.uid = info.gid = 0; info.uname = info.gname = ''
        with open(os.path.join(src, f), 'rb') as fh:
            tar.addfile(info, fh)
"@ $out $t.Name $t.Exe
        if ($LASTEXITCODE -ne 0) { throw "tarball failed for $($t.Rid)" }
    }
}

Get-ChildItem $dist -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
