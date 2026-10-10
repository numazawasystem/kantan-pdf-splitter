param([string]$Setup = '', [switch]$KeepInstalled, [switch]$SkipAppLaunch)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content "$root/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
if (-not $Setup) { $Setup = "$root/artifacts/dist/KantanPdfSplitter-Setup-$version.exe" }
$Setup = [IO.Path]::GetFullPath($Setup)
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KantanPdfSplitter'
if (Test-Path $key) { throw 'An existing installation is present; refusing to replace it during smoke testing.' }
$install = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/KantanPdfSplitter'))
if (Test-Path $install) { throw 'An existing install directory is present; refusing to replace it.' }
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolderOption]::DoNotVerify)
$menu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs, [Environment+SpecialFolderOption]::DoNotVerify)
foreach ($folder in @($desktop,$menu)) { if (Test-Path "$folder/かんたんPDF分割.lnk") { throw 'A pre-existing shortcut is present.' } }
$report = [Collections.Generic.List[string]]::new()
if (-not ('PdfSplitterShortcutReader' -as [type])) {
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class PdfSplitterShortcutReader {
  [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellLinkW {
    void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int length,IntPtr data,uint flags);
    void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
    void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
    void GetShowCmd(out int command); void SetShowCmd(int command);
    void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder text,int length,out int index);
  }
  public static string[] Read(string path) {
    object instance=new ShellLink();
    try { ((IPersistFile)instance).Load(path,0); var link=(IShellLinkW)instance;
      var target=new StringBuilder(32768);var icon=new StringBuilder(32768);int index;
      link.GetPath(target,target.Capacity,IntPtr.Zero,0);link.GetIconLocation(icon,icon.Capacity,out index);
      return new[]{target.ToString(),icon.ToString(),index.ToString()};
    } finally { Marshal.FinalReleaseComObject(instance); }
  }
}
'@
}
function Invoke-Setup([int]$Expected = 0) {
  $process = Start-Process -FilePath $Setup -ArgumentList '/S' -WindowStyle Hidden -PassThru
  if (-not $process.WaitForExit(120000)) { throw 'Installer timeout.' }
  if ($process.ExitCode -ne $Expected) { throw "Setup exit $($process.ExitCode), expected $Expected." }
}
function Assert-Install {
  $entry = Get-ItemProperty $key
  if ($entry.DisplayVersion -ne $version -or $entry.Publisher -ne '名古屋テクノロジー' -or $entry.InstallLocation -ne $install) { throw 'Installed Apps metadata mismatch.' }
  foreach ($folder in @($desktop,$menu)) {
    $link = [IO.Path]::GetFullPath((Join-Path $folder 'かんたんPDF分割.lnk'))
    if (-not (Test-Path -LiteralPath $link)) {
      $found = @(Get-ChildItem $folder -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '
      throw "Shortcut missing: $link; found=$found"
    }
    # Some Windows Shell/WScript combinations report an empty TargetPath for
    # valid shortcuts. Load through the native IShellLinkW interface instead.
    $shortcut = [PdfSplitterShortcutReader]::Read($link)
    if ($shortcut[0] -ne "$install\KantanPdfSplitter.exe") { throw "Shortcut target mismatch: $($shortcut[0])" }
    if ($shortcut[1] -ne '' -and $shortcut[1] -ne "$install\KantanPdfSplitter.exe") { throw "Shortcut icon location mismatch: $($shortcut[1])" }
    if ($shortcut[2] -ne '0') { throw 'Shortcut icon index mismatch.' }
    $label = if ($folder -eq $desktop) { 'desktop' } else { 'start-menu' }
    & "$PSScriptRoot/Test-ShortcutIcon.ps1" -ShortcutPath $link -ReportPath "$root/artifacts/tests/$label-icon.json"
  }
  if ((Get-Item "$install/KantanPdfSplitter.exe").VersionInfo.ProductVersion -notlike "$version*") { throw 'Executable version mismatch.' }
}
function Invoke-Uninstall {
  $exe = "$install/KantanPdfSplitter-Uninstall.exe"
  $process = Start-Process -FilePath $exe -ArgumentList '/S' -WindowStyle Hidden -PassThru
  if (-not $process.WaitForExit(120000)) { throw 'Uninstaller timeout.' }
  if ($process.ExitCode -ne 0) { throw "Uninstall exit $($process.ExitCode)." }
  $deadline = [DateTime]::UtcNow.AddSeconds(20)
  while ((Test-Path $key) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
  if (Test-Path $key) { throw 'Uninstall registration remained.' }
  foreach ($folder in @($desktop,$menu)) { if (Test-Path "$folder/かんたんPDF分割.lnk") { throw 'Shortcut remained.' } }
  if (Test-Path "$install/KantanPdfSplitter.exe") { throw 'App binary remained.' }
}
try {
  Invoke-Setup; Assert-Install; $report.Add('PASS fresh per-user install / registry / shortcuts / version')
  [IO.File]::WriteAllText("$install/元の資料.pdf", 'user source sentinel')
  [IO.File]::WriteAllText("$install/分割後の資料.pdf", 'user output sentinel')
  [IO.File]::WriteAllText("$install/obsolete-test.dll", 'previous owned binary')
  Add-Content "$install/owned-files.txt" 'obsolete-test.dll' -Encoding ascii
  Invoke-Setup; Assert-Install
  if (Test-Path "$install/obsolete-test.dll") { throw 'Old owned binary remained after repair.' }
  if ((Get-Content "$install/元の資料.pdf" -Raw) -ne 'user source sentinel') { throw 'User file changed during repair.' }
  $report.Add('PASS same-version repair / old owned binary cleanup / user PDF preservation')
  Set-ItemProperty $key DisplayVersion '0.9.0'
  Invoke-Setup; Assert-Install; $report.Add('PASS upgrade branch from simulated 0.9.0 metadata')
  Set-ItemProperty $key DisplayVersion '99.0.0'
  try { Invoke-Setup 12 } finally { Set-ItemProperty $key DisplayVersion $version }
  $report.Add('PASS downgrade refused (exit 12)')
  $running = [Threading.Mutex]::new($false,'Local\KantanPdfSplitter.AppRunning')
  try { Invoke-Setup 10 } finally { $running.Dispose() }
  $report.Add('PASS running-app maintenance guard (exit 10)')
  if ($SkipAppLaunch) {
    $report.Add('UNVERIFIED installed app startup: explicitly skipped for documented local application-control restriction')
  } else {
  $app = Start-Process "$install/KantanPdfSplitter.exe" -WindowStyle Hidden -PassThru
  try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 200; $app.Refresh() } while (-not $app.HasExited -and $app.MainWindowTitle -ne 'かんたんPDF分割' -and [DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowTitle -ne 'かんたんPDF分割') { throw 'App did not reach its main window.' }
    $report.Add('PASS self-contained installed app startup / splash closed')
  } finally { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() } }
  }
  if (-not $KeepInstalled) {
    Invoke-Uninstall
    foreach ($name in @('元の資料.pdf','分割後の資料.pdf')) { if (-not (Test-Path "$install/$name")) { throw 'User PDF removed by uninstall.' } }
    $report.Add('PASS uninstall / shortcuts and binaries removed / user PDFs preserved')
    # Reinstall over a directory containing only preserved user files.
    Invoke-Setup; Assert-Install; Invoke-Uninstall
    $report.Add('PASS reinstall after uninstall')
    # Only remove known sentinels authored by this test; no recursive cleanup.
    Remove-Item -LiteralPath "$install/元の資料.pdf","$install/分割後の資料.pdf"
    if (@(Get-ChildItem $install -Force).Count -ne 0) { throw 'Unexpected installer-owned leftovers.' }
    Remove-Item -LiteralPath $install
  }
} catch { $report.Add("FAIL $($_.Exception.Message)"); throw }
finally {
  New-Item -ItemType Directory "$root/artifacts/tests" -Force | Out-Null
  $report | Set-Content "$root/artifacts/tests/installer-smoke.txt" -Encoding utf8
  $report | ForEach-Object { Write-Host $_ }
}
