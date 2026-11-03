// Shared look for the overlays: text size and font, saved in swgr-ui.cfg next to the exe. Opacity is per window
// (LayeredForm below, saved in swgr-opacity.cfg).
// Panels draw in their 100% design space under a scale transform, so everything (text, bars, padding) grows together.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Game data the panels read (string tables, the skills datatable): bin/data/<rel> (the shareable data pack, see
// tools/make-data-pack.js), else ../beautified/<rel> next to bin/ (a full local extraction)
static class GameData {
  static readonly string bin = Path.GetDirectoryName(Application.ExecutablePath);
  internal static string File(params string[] rel) {
    var pack = Path.Combine(bin, "data", Path.Combine(rel));
    return System.IO.File.Exists(pack) ? pack : Path.GetFullPath(Path.Combine(bin, "..", "beautified", Path.Combine(rel)));
  }
}

static class UiLook {
  // Font choices: menu name, regular family, emphasis family (names and headings; never a heavy bold)
  static readonly string[][] AllFonts = {
    new[] { "Encode Sans (Questlog)", "Encode Sans", "Encode Sans SemiBold" },
    new[] { "Segoe UI", "Segoe UI", "Segoe UI Semibold" },
    new[] { "Bahnschrift", "Bahnschrift", "Bahnschrift SemiBold" },
    new[] { "Verdana", "Verdana", "Verdana" },
    new[] { "Open Sans", "Open Sans", "Open Sans" },
    new[] { "Calibri", "Calibri", "Calibri" },
    new[] { "Cascadia Mono", "Cascadia Mono", "Cascadia Mono SemiBold" },
  };
  internal static readonly float[] Sizes = { 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f };
  // Opacity choices in percent: the panel background only (text and bars stay solid), and the whole panel
  internal static readonly int[] BackOpacities = { 100, 85, 70, 55, 40, 25, 0 };
  internal static readonly int[] PanelOpacities = { 100, 90, 80, 70, 60, 50 };

  static readonly string cfgFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-ui.cfg");
  internal static readonly string[][] Fonts;
  internal static float Scale = 1f;
  internal static int FontIndex;
  internal static bool Chosen; // a font was picked; until then the HP overlay keeps each style's own fonts
  internal static event Action<bool> Changed; // true when a text size was picked

  // Bundled fonts (bin/fonts/*.ttf, e.g. Encode Sans) loaded privately, so they needn't be installed
  static readonly PrivateFontCollection bundled = new PrivateFontCollection();

  static UiLook() {
    try { foreach (var f in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "fonts"), "*.ttf")) bundled.AddFontFile(f); } catch { }
    var installed = new HashSet<string>(new InstalledFontCollection().Families.Concat(bundled.Families).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
    Fonts = AllFonts.Where(f => installed.Contains(f[1]) && installed.Contains(f[2])).ToArray();
    try {
      foreach (var line in File.ReadAllLines(cfgFile)) {
        var kv = line.Split('=');
        if (kv.Length != 2) continue;
        if (kv[0] == "scale") Scale = Math.Max(0.75f, Math.Min(3f, float.Parse(kv[1], CultureInfo.InvariantCulture)));
        if (kv[0] == "font") { FontIndex = Math.Max(0, Array.FindIndex(Fonts, f => f[0] == kv[1])); Chosen = true; }
      }
    } catch { }
  }

  internal static string Regular { get { return Fonts.Length > 0 ? Fonts[FontIndex][1] : "Segoe UI"; } }
  internal static string Strong { get { return Fonts.Length > 0 ? Fonts[FontIndex][2] : "Segoe UI Semibold"; } }

  // new Font(family, size) that also finds the bundled fonts
  internal static Font MakeFont(string family, float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point) {
    var b = bundled.Families.FirstOrDefault(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
    return b != null ? new Font(b, size, style, unit) : new Font(family, size, style, unit);
  }

  // Panel fonts: the size is in points at 96 dpi but fixed in pixels, so only the panel's scale transform (which includes Dpi) enlarges it
  internal static Font PanelFont(string family, float size, FontStyle style = FontStyle.Regular) {
    return MakeFont(family, size * 96f / 72f, style, GraphicsUnit.Pixel);
  }

  // Display scaling (1.5 at 150%). Windows draws the tools at real resolution instead of stretching a 96-dpi bitmap,
  // so panels multiply their scale by this and the main window runs its pixel sizes through D / Pad / Sz.
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();
  internal static float Dpi = 1f;

  // Call first thing in Main, before any window exists. (Graphics.FromHwnd keeps reporting 96 here, so ask user32.)
  internal static void EnableDpi() {
    try { SetProcessDPIAware(); Dpi = GetDpiForSystem() / 96f; } catch { }
  }

  internal static int D(int v) { return (int)Math.Round(v * Dpi); }
  internal static Padding Pad(int l, int t, int r, int b) { return new Padding(D(l), D(t), D(r), D(b)); }
  internal static Size Sz(int w, int h) { return new Size(D(w), D(h)); }

  internal static void Set(float scale, int font, bool save = true, bool sizePicked = false) {
    Scale = scale;
    FontIndex = font;
    var lines = new List<string> { "scale=" + scale.ToString(CultureInfo.InvariantCulture) };
    if (Chosen) lines.Add("font=" + Fonts[font][0]);
    if (save) try { File.WriteAllLines(cfgFile, lines); } catch { }
    if (Changed != null) Changed(sizePicked);
  }

  internal static void PickSize(float scale) { Set(scale, FontIndex, true, true); }
  internal static void PickFont(int font) { Chosen = true; Set(Scale, font); }

  // "Text size" and "Font" submenus; checkmarks refresh each time the menu opens
  internal static ToolStripItem[] MenuItems() {
    var size = new ToolStripMenuItem("Text size");
    foreach (var s in Sizes) {
      float v = s;
      size.DropDownItems.Add(new ToolStripMenuItem((int)Math.Round(s * 100) + "%", null, (o, e) => PickSize(v)));
    }
    size.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem i in size.DropDownItems) i.Checked = i.Text == (int)Math.Round(Scale * 100) + "%"; };
    var font = new ToolStripMenuItem("Font");
    for (int i = 0; i < Fonts.Length; i++) {
      int idx = i;
      font.DropDownItems.Add(new ToolStripMenuItem(Fonts[i][0], null, (o, e) => PickFont(idx)) { Font = MakeFont(Fonts[i][1], 9f) });
    }
    font.DropDownOpening += (o, e) => { for (int i = 0; i < font.DropDownItems.Count; i++) ((ToolStripMenuItem)font.DropDownItems[i]).Checked = Chosen && i == FontIndex; };
    return new ToolStripItem[] { size, font };
  }
}

// Base for the overlay panels: a per-pixel alpha (layered) window. Each repaint draws the panel into a 32-bit bitmap,
// the background at BackOpacity and then the panel's own OnPaint on top, and hands it to Windows, which
// blends it over the game at PanelOpacity. So the background can be see-through while text, bars and icons
// stay solid. Windows never sends WM_PAINT to such a window: Invalidate() schedules the repaint instead.
// (A fully clear background is kept at alpha 1, so the panel can still be dragged and right-clicked.)
class LayeredForm : Form {
  const int WS_EX_LAYERED = 0x80000, ULW_ALPHA = 2;
  [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
  [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);

  bool pending;

  // ---- staying on top: a focused borderless DX12 game can end up above other topmost windows, so each overlay
  // re-asserts topmost on its timer. Not while one of our menus is open, or the overlays cover it.
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static int menusOpen;

  // Count a menu (context menu, tray menu, or a dropdown hosted elsewhere) as open while it shows
  internal static void TrackMenu(ToolStripDropDown m) {
    if (m == null || m.Tag as string == "tracked") return;
    m.Tag = "tracked";
    m.Opened += (s, e) => menusOpen++;
    m.Closed += (s, e) => menusOpen = Math.Max(0, menusOpen - 1);
  }

  protected override void OnContextMenuStripChanged(EventArgs e) { TrackMenu(ContextMenuStrip); base.OnContextMenuStripChanged(e); }

  protected void KeepOnTop() {
    if (menusOpen > 0) return;
    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
  }

  // Rounded panel corners in device pixels (0 = square): the corners outside become fully transparent
  protected virtual float CornerRadius { get { return 0; } }

  // ---- opacity, per window: "key=back,panel" lines in swgr-opacity.cfg (percent)
  static readonly string opacityFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-opacity.cfg");
  string opacityKey;
  internal int BackOpacity = 100, PanelOpacity = 100;
  protected double Fade = 1; // 0..1 on top of the overall opacity, for windows that fade in and out (not saved)

  static Dictionary<string, string> LoadOpacities() {
    var d = new Dictionary<string, string>();
    try { foreach (var l in File.ReadAllLines(opacityFile)) { int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
    return d;
  }

  // Call from the constructor with a name for this window ("target", "self", "dps", ...)
  protected void UseOpacity(string key) {
    opacityKey = key;
    string v;
    if (!LoadOpacities().TryGetValue(key, out v)) return;
    var p = v.Split(',');
    try { BackOpacity = Math.Max(0, Math.Min(100, int.Parse(p[0]))); PanelOpacity = Math.Max(20, Math.Min(100, int.Parse(p[1]))); } catch { }
  }

  internal void SetOpacity(int back, int panel) {
    BackOpacity = back; PanelOpacity = panel;
    if (opacityKey != null) {
      var d = LoadOpacities();
      d[opacityKey] = back + "," + panel;
      try { File.WriteAllLines(opacityFile, d.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
    }
    Invalidate();
  }

  // "Background opacity" and "Overall opacity" submenus for this window's right-click menu
  internal ToolStripItem[] OpacityMenu() {
    var back = new ToolStripMenuItem("Background opacity");
    foreach (var p in UiLook.BackOpacities) { int v = p; back.DropDownItems.Add(new ToolStripMenuItem(p + "%", null, (o, e) => SetOpacity(v, PanelOpacity))); }
    back.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem i in back.DropDownItems) i.Checked = i.Text == BackOpacity + "%"; };
    var panel = new ToolStripMenuItem("Overall opacity");
    foreach (var p in UiLook.PanelOpacities) { int v = p; panel.DropDownItems.Add(new ToolStripMenuItem(p + "%", null, (o, e) => SetOpacity(BackOpacity, v))); }
    panel.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem i in panel.DropDownItems) i.Checked = i.Text == PanelOpacity + "%"; };
    return new ToolStripItem[] { back, panel };
  }

  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED; return cp; }
  }

  protected override void OnInvalidated(InvalidateEventArgs e) { base.OnInvalidated(e); Schedule(); }
  protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Schedule(); }
  protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Present(); }
  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Present(); }

  // Several Invalidate calls in one tick become one repaint
  void Schedule() {
    if (pending || !IsHandleCreated) return;
    pending = true;
    BeginInvoke((Action)(() => { pending = false; Present(); }));
  }

  internal static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF rc, float r) {
    var p = new System.Drawing.Drawing2D.GraphicsPath();
    float d = Math.Min(2 * r, Math.Min(rc.Width, rc.Height));
    p.AddArc(rc.X, rc.Y, d, d, 180, 90); p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
    p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90); p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  void Present() {
    if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;
    using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) {
        int a = (int)Math.Round(BackOpacity * 2.55);
        float r = CornerRadius;
        if (r <= 0) g.Clear(Color.FromArgb(Math.Max(1, a), BackColor));
        else {
          g.Clear(Color.Transparent);
          g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
          using (var path = RoundRect(new RectangleF(0, 0, bmp.Width, bmp.Height), r))
          using (var b = new SolidBrush(Color.FromArgb(Math.Max(1, a), BackColor))) g.FillPath(b, path);
        }
        OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, bmp.Size)));
      }
      IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
      try {
        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)Math.Round(PanelOpacity * 2.55 * Math.Max(0, Math.Min(1, Fade))), AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
      } finally {
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
      }
    }
  }
}
