param(
    [string]$DestinationDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work/nsis/3.13')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $root 'installer/nsis-toolchain.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$compiler = Join-Path $destination 'makensis.exe'

function Assert-CompilerVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $version = (& $Path /VERSION 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { return $false }
    return $version -eq "v$($config.version)"
}

if (Assert-CompilerVersion -Path $compiler) {
    [pscustomobject]@{
        Version = $config.version
        Root = $destination
        Compiler = $compiler
        SourceArchiveSha256 = $config.archiveSha256
        Reused = $true
    }
    return
}

$workingParent = Join-Path $root 'work/nsis/downloads'
[IO.Directory]::CreateDirectory($workingParent) | Out-Null
$working = Join-Path $workingParent ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($working) | Out-Null

try {
    $archive = Join-Path $working $config.archiveFileName
    $officialDownloadSucceeded = $false
    try {
        Invoke-WebRequest -Uri $config.officialDownloadUrl -OutFile $archive
        $officialDownloadSucceeded = (Test-Path -LiteralPath $archive -PathType Leaf) -and
            ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $config.archiveSha256)
    }
    catch {
        $officialDownloadSucceeded = $false
    }

    if (-not $officialDownloadSucceeded) {
        if (Test-Path -LiteralPath $archive) { [IO.File]::Delete($archive) }
        $nupkg = Join-Path $working $config.fallbackTransport.packageFileName
        Invoke-WebRequest -Uri $config.fallbackTransport.url -OutFile $nupkg
        $nupkgHash = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
        if ($nupkgHash -ne $config.fallbackTransport.packageSha256) {
            throw "NSIS fallback transport hash mismatch. Expected $($config.fallbackTransport.packageSha256), got $nupkgHash."
        }

        $expandedPackage = Join-Path $working 'fallback-package'
        [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $expandedPackage)
        $embedded = Join-Path $expandedPackage ($config.fallbackTransport.embeddedArchivePath -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $embedded -PathType Leaf)) {
            throw "NSIS archive was not found in the pinned fallback package."
        }
        [IO.File]::Copy($embedded, $archive, $true)
    }

    $archiveInfo = Get-Item -LiteralPath $archive
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($archiveInfo.Length -ne [long]$config.archiveSize -or $archiveHash -ne $config.archiveSha256) {
        throw "NSIS archive integrity failure. Expected size/hash $($config.archiveSize)/$($config.archiveSha256), got $($archiveInfo.Length)/$archiveHash."
    }

    $expanded = Join-Path $working 'archive'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
    $sourceRoot = Get-ChildItem -LiteralPath $expanded -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'makensis.exe') -PathType Leaf } |
        Select-Object -First 1
    if ($null -eq $sourceRoot) { throw 'The verified NSIS archive does not contain makensis.exe.' }

    if (Test-Path -LiteralPath $destination) {
        $resolvedDestination = [IO.Path]::GetFullPath($destination)
        $allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'work/nsis')) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedDestination.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace an NSIS directory outside work/nsis: $resolvedDestination"
        }
        [IO.Directory]::Delete($resolvedDestination, $true)
    }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item -Path (Join-Path $sourceRoot.FullName '*') -Destination $destination -Recurse -Force

    if (-not (Assert-CompilerVersion -Path $compiler)) {
        throw "The extracted NSIS compiler did not report v$($config.version)."
    }

    [pscustomobject]@{
        Version = $config.version
        Root = $destination
        Compiler = $compiler
        SourceArchiveSha256 = $archiveHash
        Reused = $false
    }
}
finally {
    if (Test-Path -LiteralPath $working) { [IO.Directory]::Delete($working, $true) }
}
