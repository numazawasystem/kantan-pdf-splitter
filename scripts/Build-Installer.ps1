param([string]$SignToolCommand = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
  if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must contain three numeric components.' }
  $payload = Join-Path $root 'artifacts/publish'
  $output = Join-Path $root 'artifacts/dist'
  if (Test-Path $payload) {
    $resolved = (Resolve-Path $payload).Path
    if ($resolved -ne [IO.Path]::GetFullPath((Join-Path $root 'artifacts/publish'))) { throw 'Unsafe cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
  New-Item -ItemType Directory -Path $output -Force | Out-Null
  & dotnet publish src/KantanPdfSplitter/KantanPdfSplitter.csproj -c Release -r win-x64 --self-contained true -o $payload -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true
  if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
  Copy-Item LICENSE $payload
  # Include the exact notices from this publish runtime, not a guessed license.
  $runtimePackage = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netcore.app.runtime.win-x64'
  $runtimeVersion = ((Get-Content "$payload/KantanPdfSplitter.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
  Copy-Item "$runtimePackage/$runtimeVersion/LICENSE.TXT" "$payload/licenses/dotnet/LICENSE.txt" -Force
  Copy-Item "$runtimePackage/$runtimeVersion/THIRD-PARTY-NOTICES.TXT" "$payload/licenses/dotnet/ThirdPartyNotices.txt" -Force
  $owned = @(Get-ChildItem $payload -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName) } | Sort-Object)
  if ($owned | Where-Object { $_ -match '(?i)\.pdf$|\.\.|:' }) { throw 'Unsafe payload name.' }
  [IO.File]::WriteAllLines((Join-Path $payload 'owned-files.txt'), $owned, [Text.Encoding]::ASCII)
  $size = [math]::Ceiling((Get-ChildItem $payload -File -Recurse | Measure-Object Length -Sum).Sum / 1024)
  $nsis = & ./scripts/Get-Nsis.ps1
  $arguments = @("/DPRODUCT_VERSION=$version", "/DROOT=$root", "/DPAYLOAD_DIR=$payload", "/DOUTPUT_DIR=$output", "/DSIZE_KB=$size")
  if ($SignToolCommand) { $arguments += "/DSIGNTOOL_COMMAND=$SignToolCommand" }
  & $nsis.Compiler /INPUTCHARSET UTF8 @arguments installer/KantanPdfSplitter.nsi
  if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed.' }
  $zip = Join-Path $output "KantanPdfSplitter-$version-win-x64.zip"
  if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
  Compress-Archive -Path "$payload/*" -DestinationPath $zip
  $hashes = Get-ChildItem $output -File | Where-Object Extension -in '.exe','.zip' | Get-FileHash -Algorithm SHA256
  $hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } | Set-Content "$output/SHA256SUMS.txt" -Encoding ascii
  $hashes | Format-Table Hash,Path -AutoSize
} finally { Pop-Location }
