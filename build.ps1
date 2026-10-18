$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root ('tmp/publish-' + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $root 'artifacts'
dotnet publish (Join-Path $root 'src/TrueAuthor.Desktop') -c Release -r win-x64 --self-contained true -p:Version=0.7.0 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'TrueAuthor.exe') { throw 'Expected exactly one bundled executable.' }
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$target = Join-Path $artifacts 'TrueAuthor-0.7.0-win-x64.exe'
Copy-Item -LiteralPath $files[0].FullName -Destination $target
Get-FileHash -Algorithm SHA256 -LiteralPath $target
