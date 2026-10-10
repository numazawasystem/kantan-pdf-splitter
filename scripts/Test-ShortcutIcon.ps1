param([Parameter(Mandatory=$true)][string]$ShortcutPath,
    [Parameter(Mandatory=$true)][string]$ReportPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
if(-not ('KantanShellIconProbe' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class KantanShellIconProbe {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct Info {
        public IntPtr icon;
        public int index;
        public uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string typeName;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attributes,
        out Info info, uint size, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
}
$path=[IO.Path]::GetFullPath($ShortcutPath)
if(-not(Test-Path -LiteralPath $path)){throw 'Expected installed shortcut is missing.'}
$root=Split-Path -Parent $PSScriptRoot
$results=@(foreach($small in @($true,$false)) {
    $info=[KantanShellIconProbe+Info]::new()
    $flags=[uint32]0x100;if($small){$flags=$flags -bor 1}
    # Query the Shell's actual shortcut icon; no shortcut Save, launch or cache reset.
    $status=[KantanShellIconProbe]::SHGetFileInfo($path,0,[ref]$info,[uint32][Runtime.InteropServices.Marshal]::SizeOf($info),$flags)
    if($status -eq [IntPtr]::Zero -or $info.icon -eq [IntPtr]::Zero){throw 'Shell could not resolve the shortcut icon.'}
    $borrowed=[Drawing.Icon]::FromHandle($info.icon)
    $icon=$borrowed.Clone()
    try {
        $bitmap=$icon.ToBitmap()
        try {
            $expected=[Drawing.Bitmap]::new((Join-Path $root "branding/icons/app-$($bitmap.Width).png"))
            try {
                $sum=0L;$samples=0
                for($y=0;$y -lt $bitmap.Height;$y++){for($x=0;$x -lt $bitmap.Width;$x++){
                    # Windows adds the normal shortcut arrow in this quadrant.
                    if($x -lt $bitmap.Width/2 -and $y -ge $bitmap.Height/2){continue}
                    $reference=$expected.GetPixel($x,$y);if($reference.A -lt 240){continue}
                    $actual=$bitmap.GetPixel($x,$y)
                    $sum += [Math]::Abs([int]$actual.R-$reference.R)+[Math]::Abs([int]$actual.G-$reference.G)+[Math]::Abs([int]$actual.B-$reference.B)
                    $samples++
                }}
                $mean=$sum/($samples*3.0)
                if($samples -eq 0 -or $mean -gt 30){throw "Shell shortcut icon is not the PDF splitter icon (mean RGB error=$mean)."}
                [ordered]@{size=$bitmap.Width;meanRgbError=$mean;samples=$samples;shortcutOverlayQuadrantExcluded=$true;result='PASS'}
            }finally{$expected.Dispose()}
        }finally{$bitmap.Dispose()}
    }finally{$icon.Dispose();[void][KantanShellIconProbe]::DestroyIcon($info.icon)}
})
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath)))|Out-Null
[ordered]@{path=$path;result='PASS';readOnlyQuery='SHGetFileInfo SHGFI_ICON';icons=$results}|
    ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Host "SHORTCUT ICON PASS $(Split-Path $path -Leaf)"
$global:LASTEXITCODE=0

