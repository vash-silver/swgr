# Rasterize glyphs from a TTF with GDI+ into a full-height cell per glyph.
# usage: powershell -File glyph-raster.ps1 <font.ttf> <emPx> <cellH> <baselineY> <codes,comma,separated> <out.json> [hint]
# hint: gridfit (hinted, default) or aa (unhinted antialias).
# Output JSON: [{ code, adv, w, h, ox, a }] where a is base64 alpha (w*h bytes) and ox is the pen origin x in the cell.
param([string]$Ttf, [double]$Em, [int]$CellH, [int]$Baseline, [string]$Codes, [string]$Out, [string]$Hint = 'gridfit')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

public static class GlyphRaster {
  // Must outlive the Font: if the collection is garbage-collected mid-run, GDI+ calls fail with "Parameter is not valid"
  static PrivateFontCollection pfc;

  public static string Run(string ttf, float em, int cellH, int baseline, string codes, bool gridfit) {
    pfc = new PrivateFontCollection();
    pfc.AddFontFile(ttf);
    var fam = pfc.Families[0];
    var style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
    var font = new Font(fam, em, style, GraphicsUnit.Pixel);
    float ascent = em * fam.GetCellAscent(style) / fam.GetEmHeight(style);
    var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
    int margin = (int)Math.Ceiling(em * 0.5f) + 2;
    var sb = new StringBuilder("[");
    bool first = true;
    foreach (var cs in codes.Split(',')) {
      int code = int.Parse(cs);
      try { first = Glyph(sb, first, code, font, fmt, ascent, cellH, baseline, margin, gridfit); }
      catch (Exception e) { throw new Exception("glyph " + code + ": " + e.Message, e); }
    }
    return sb.Append(']').ToString();
  }

  static bool Glyph(StringBuilder sb, bool first, int code, Font font, StringFormat fmt, float ascent, int cellH, int baseline, int margin, bool gridfit) {
      string s = char.ConvertFromUtf32(code);
      float adv;
      using (var mb = new Bitmap(1, 1))
      using (var mg = Graphics.FromImage(mb)) {
        mg.TextRenderingHint = gridfit ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;
        adv = mg.MeasureString(s, font, 10000, fmt).Width;
      }
      int w = (int)Math.Ceiling(adv) + 2 * margin;
      using (var bmp = new Bitmap(w, cellH, PixelFormat.Format32bppArgb))
      using (var g = Graphics.FromImage(bmp)) {
        g.Clear(Color.FromArgb(0, 255, 255, 255));
        g.TextRenderingHint = gridfit ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawString(s, font, Brushes.White, margin, baseline - ascent, fmt);
        var data = bmp.LockBits(new Rectangle(0, 0, w, cellH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var raw = new byte[w * cellH * 4];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(data);
        var a = new byte[w * cellH];
        for (int i = 0; i < a.Length; i++) a[i] = raw[i * 4 + 3];
        if (!first) sb.Append(',');
        first = false;
        sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
          "{{\"code\":{0},\"adv\":{1},\"w\":{2},\"h\":{3},\"ox\":{4},\"a\":\"{5}\"}}", code, adv, w, cellH, margin, Convert.ToBase64String(a));
      }
      return false;
  }
}
'@
$json = [GlyphRaster]::Run((Resolve-Path $Ttf).Path, $Em, $CellH, $Baseline, $Codes, $Hint -ne 'aa')
[System.IO.File]::WriteAllText($Out, $json)
