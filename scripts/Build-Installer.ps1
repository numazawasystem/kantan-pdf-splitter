param([string]$SignToolCommand = '', [string]$SignPublishedFilesScript = '')
$ErrorActionPreference = 'Stop'
if ([bool]$SignToolCommand -ne [bool]$SignPublishedFilesScript) { throw 'For signed packaging, both runtime signing script and NSIS signing command are required.' }
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
  if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must contain three numeric components.' }
  $payload = Join-Path $root "artifacts/publish/$version"
  $output = Join-Path $root 'artifacts/dist'
  if (Test-Path $payload) {
    $resolved = (Resolve-Path $payload).Path
    if ($resolved -ne [IO.Path]::GetFullPath((Join-Path $root "artifacts/publish/$version"))) { throw 'Unsafe cleanup target.' }
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
  $desktopVersion = ((Get-Content "$payload/KantanPdfSplitter.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
  Copy-Item "$env:USERPROFILE/.nuget/packages/microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE" "$payload/licenses/dotnet/WPF-LICENSE.txt" -Force
  if ($SignPublishedFilesScript) {
    $signScript = (Resolve-Path -LiteralPath $SignPublishedFilesScript).Path
    & $signScript -PublishDirectory $payload
    if ($LASTEXITCODE -ne 0) { throw 'Runtime signing script failed.' }
    foreach ($file in (Get-ChildItem $payload -File -Recurse | Where-Object Extension -in '.exe','.dll')) {
      if ((Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') { throw "Runtime signing was not verified: $($file.Name)" }
    }
  }
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
  $hashes = Get-Item "$output/KantanPdfSplitter-Setup-$version.exe", $zip | Get-FileHash -Algorithm SHA256
  $hashLines = $hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" }
  $hashLines | Set-Content "$output/SHA256SUMS-$version.txt" -Encoding ascii
  $hashLines | Set-Content "$output/SHA256SUMS.txt" -Encoding ascii
  $hashes | Format-Table Hash,Path -AutoSize
} finally { Pop-Location }
