$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logo = Join-Path $root 'branding/nagoya-technology-logo.png'
if ((Get-FileHash $logo).Hash -ne 'B8BB696071B466374CC95C8427AD563C5D48E36C14A1164D7CBB2B981D487041') { throw 'Official logo hash mismatch.' }
Add-Type -AssemblyName System.Drawing
$drawingRefs = @("System.Drawing.Common", "System.Drawing.Primitives", "System.Runtime") + @(Get-ChildItem $PSHOME -Filter "System.Private.Windows.*.dll" | ForEach-Object FullName)
Add-Type -ReferencedAssemblies $drawingRefs -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class BrandAssets {
  static Color Navy = Color.FromArgb(23,50,77), Blue = Color.FromArgb(24,116,230);
  public static Bitmap Icon(int size) {
    var image = new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(image)) {
      g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias;
      g.ScaleTransform(size*4/256f,size*4/256f);
      using(var white=new SolidBrush(Color.White)) using(var blue=new SolidBrush(Blue))
      using(var red=new SolidBrush(Color.FromArgb(214,55,61))) using(var navy=new Pen(Navy,12))
      using(var line=new Pen(Blue,10)) {
        navy.LineJoin=LineJoin.Round; line.StartCap=line.EndCap=LineCap.Round;
        // Two sheets separating outward; strong silhouettes at 16 pixels.
        PointF[] back={new(139,22),new(192,22),new(221,53),new(221,178),new(139,178)};
        g.FillPolygon(blue,back); g.DrawPolygon(navy,back);
        PointF[] front={new(29,62),new(92,62),new(123,93),new(123,223),new(29,223)};
        g.FillPolygon(white,front); g.DrawPolygon(navy,front);
        g.DrawLines(navy,new PointF[]{new(92,64),new(92,95),new(121,95)});
        g.FillRectangle(red,43,113,65,19);
        if(size>=24) { g.DrawLine(line,48,155,102,155); g.DrawLine(line,48,178,89,178); }
        // Arrow announces a page leaving the document without tiny lettering.
        g.DrawLine(line,148,208,217,208);
        g.DrawLines(line,new PointF[]{new(198,189),new(217,208),new(198,227)});
      }
    }
    var small=new Bitmap(size,size,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(small)) {g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(image,0,0,size,size);}
    image.Dispose(); return small;
  }
  public static void Build(string root) {
    var folder=Path.Combine(root,"branding","icons"); Directory.CreateDirectory(folder);
    int[] sizes={16,20,24,32,40,48,64,128,256};
    var chunks=new byte[sizes.Length][];
    for(int i=0;i<sizes.Length;i++) using(var bitmap=Icon(sizes[i])) {
      bitmap.Save(Path.Combine(folder,"app-"+sizes[i]+".png"),ImageFormat.Png);
      int n=sizes[i],maskStride=((n+31)/32)*4;
      using(var ms=new MemoryStream()) using(var b=new BinaryWriter(ms)) {
        b.Write(40);b.Write(n);b.Write(n*2);b.Write((ushort)1);b.Write((ushort)32);b.Write(0);
        b.Write(n*n*4+maskStride*n);b.Write(0);b.Write(0);b.Write(0);b.Write(0);
        for(int y=n-1;y>=0;y--) for(int x=0;x<n;x++){var c=bitmap.GetPixel(x,y);b.Write(c.B);b.Write(c.G);b.Write(c.R);b.Write(c.A);}
        for(int y=n-1;y>=0;y--){var mask=new byte[maskStride];for(int x=0;x<n;x++)if(bitmap.GetPixel(x,y).A==0)mask[x/8]|=(byte)(128>>(x%8));b.Write(mask);}
        chunks[i]=ms.ToArray();
      }
    }
    using(var b=new BinaryWriter(File.Create(Path.Combine(folder,"app.ico")))) {
      b.Write((ushort)0);b.Write((ushort)1);b.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
      for(int i=0;i<sizes.Length;i++){b.Write((byte)(sizes[i]==256?0:sizes[i]));b.Write((byte)(sizes[i]==256?0:sizes[i]));b.Write((byte)0);b.Write((byte)0);b.Write((ushort)1);b.Write((ushort)32);b.Write(chunks[i].Length);b.Write(offset);offset+=chunks[i].Length;}
      foreach(var c in chunks)b.Write(c);
    }
    using(var logo=Image.FromFile(Path.Combine(root,"branding","nagoya-technology-logo.png"))) {
      if(logo.Width!=2172||logo.Height!=724)throw new Exception("Logo dimensions mismatch.");
      foreach(var entry in new[]{("installer-header.bmp",150,57,138,46),("installer-welcome.bmp",164,314,150,50)})
      using(var bmp=new Bitmap(entry.Item2,entry.Item3,PixelFormat.Format24bppRgb)) {
        using(var g=Graphics.FromImage(bmp)){g.Clear(Color.White);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(logo,(entry.Item2-entry.Item4)/2,(entry.Item3-entry.Item5)/2,entry.Item4,entry.Item5);}
        bmp.Save(Path.Combine(root,"branding",entry.Item1),ImageFormat.Bmp);
      }
    }
  }
}
'@
[BrandAssets]::Build($root)
Write-Host 'Verified unchanged official logo; built nine icon sizes and installer artwork.'


