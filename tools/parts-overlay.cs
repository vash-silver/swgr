// Ship part verdict overlay: shows the 1M+ verdict for the item you click in the inventory window.
// Shares parts.cs (value sheet, attribute parsing, read-only memory access); see that file for the guardrails.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only (in parts.cs). Never add
// WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:PartsOverlay -out:../bin/swgr-parts-overlay.exe parts.cs ui.cs parts-overlay.cs
// usage: swgr-parts-overlay [--render out.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class PartsOverlay : LayeredForm {
  const int W = 300, Pad = 10, HeadH = 62, RowH = 17;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Label = Color.FromArgb(233, 214, 90);
  static readonly Color Good = Color.FromArgb(62, 224, 122), Warn = Color.FromArgb(242, 184, 59), Bad = Color.FromArgb(255, 92, 92);

  readonly Timer timer = new Timer { Interval = 250 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-parts-overlay.pos");
  // Fonts and scale come from UiLook (right-click → Text size / Font); drawing happens in the 100% design space
  Font wordFont, nameFont, smallFont, rowFont, markFont;
  float scale = 1f;
  int designH;
  NotifyIcon tray;
  bool attached;
  int reconnectTick;
  Point dragFrom;

  // What is on screen: an item name plus either a rating or a short note ("not a ship component", ...)
  string name, note;
  Parts.Rating rating;

  readonly bool hosted;

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  public PartsOverlay(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR part verdict";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadPos();
    Restyle();
    SetDesignSize(HeadH);
    UiLook.Changed += OnLook;

    UseOpacity("parts");
    var menu = new ContextMenuStrip();
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR part verdict", ContextMenuStrip = trayMenu, Visible = true };
    }

    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { wordFont, nameFont, smallFont, rowFont, markFont }) if (f != null) f.Dispose();
    wordFont = UiLook.PanelFont(UiLook.Strong, 12f);
    nameFont = UiLook.PanelFont(UiLook.Strong, 9.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 8f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    markFont = UiLook.PanelFont("Segoe UI Semibold", 8.5f); // ✓ / ✗ glyphs
    scale = UiLook.Scale;
    SetDesignSize(designH > 0 ? designH : HeadH);
    Invalidate();
  }

  void SetDesignSize(int h) {
    designH = h;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(h * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
  }

  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Right - W - 40, wa.Top + 160);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top); } catch { } }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

  ulong shownId, shownSig;

  void Tick() {
    if (!attached || !Parts.Alive()) {
      attached = false;
      if (++reconnectTick % 8 == 1) attached = Parts.Attach(); // try every 2 s
    }
    ulong obj = attached ? Parts.SelectedItem() : 0;
    var vendor = attached ? Parts.CurrentVendorItem() : null;
    if (tray != null) tray.Text = attached ? "SWGR part verdict" : "SWGR part verdict: waiting for game";

    // Two sources: the inventory info panel and a vendor/bazaar "Item Details" window. Whichever changed last wins.
    ulong invId = obj == 0 ? 0 : Parts.ObjectId(obj), vendId = vendor == null ? 0 : vendor.Id;
    if (invId != lastInvId) { lastInvId = invId; if (invId != 0) useVendor = false; }
    if (vendId != lastVendId) { lastVendId = vendId; if (vendId != 0) useVendor = true; }
    if (useVendor && vendId == 0) useVendor = false;
    if (!useVendor && invId == 0 && vendId != 0) useVendor = true;
    if (!useVendor && invId == 0) { if (Visible) Visible = false; shownId = 0; return; }

    ulong id = useVendor ? vendId : invId;
    var attrs = useVendor ? vendor.Attrs : Parts.ReadAttrs(id);
    // Only re-rate when the item or its cached attributes change
    ulong sig = attrs == null ? 0 : (ulong)attrs.Count * 0x9E3779B97F4A7C15UL ^ (ulong)string.Join("|", attrs.Values).GetHashCode();
    if (id != shownId || sig != shownSig) {
      shownId = id; shownSig = sig;
      name = useVendor ? Parts.TemplateName(vendor.Template) : Parts.ObjectName(obj);
      rating = attrs == null ? null : useVendor ? Parts.RateVendor(vendor) : Parts.RateAttrs(attrs);
      note = attrs == null ? (useVendor ? "no stats in the vendor details yet" : "no stats cached yet (hover the item)")
        : rating == null ? "not a rateable ship component" : null;
      Show(name, rating, note);
    }
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  ulong lastInvId, lastVendId;
  bool useVendor;

  void Show(string n, Parts.Rating r, string why) {
    name = n; rating = r; note = why;
    SetDesignSize(r == null ? 44 : HeadH + r.Rows.Count * RowH + Pad);
    Invalidate();
  }

  static Color WordColor(string w) { return w == "VALUABLE" ? Good : w == "NOT VALUABLE" ? Bad : Warn; }

  static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using (var p = new Pen(Edge)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);
    if (rating == null) {
      DrawText(g, name ?? "", nameFont, new RectangleF(Pad, 4, W - 2 * Pad, 20), TextColor, StringAlignment.Near);
      DrawText(g, note ?? "", smallFont, new RectangleF(Pad, 22, W - 2 * Pad, 18), Muted, StringAlignment.Near);
      return;
    }
    var wc = WordColor(rating.Word);
    // Verdict bar on the left edge, verdict word, then name / type line / reason
    using (var b = new SolidBrush(wc)) g.FillRectangle(b, 0, 0, 4, designH);
    DrawText(g, rating.Word, wordFont, new RectangleF(Pad, 3, W - 2 * Pad, 22), wc, StringAlignment.Near);
    DrawText(g, rating.TypeName + "  L" + (rating.Level.HasValue ? rating.Level.ToString() : "?"), smallFont, new RectangleF(Pad, 6, W - 2 * Pad, 18), Muted, StringAlignment.Far);
    DrawText(g, name ?? "", nameFont, new RectangleF(Pad, 24, W - 2 * Pad, 18), TextColor, StringAlignment.Near);
    DrawText(g, rating.Sub, smallFont, new RectangleF(Pad, 41, W - 2 * Pad, 16), Muted, StringAlignment.Near);
    int y = HeadH;
    using (var p = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(p, Pad, y - 3, W - Pad, y - 3);
    foreach (var row in rating.Rows) {
      DrawText(g, row.Label, rowFont, new RectangleF(Pad, y, 100, RowH), Label, StringAlignment.Near);
      DrawText(g, row.Value, rowFont, new RectangleF(Pad + 96, y, 74, RowH), TextColor, StringAlignment.Far);
      DrawText(g, row.Need, rowFont, new RectangleF(Pad + 182, y, 74, RowH), Muted, StringAlignment.Near);
      if (row.Result.Length > 0)
        DrawText(g, row.Result == "PASS" ? "✓" : row.Result == "FAIL" ? "✗" : "?", markFont, new RectangleF(W - Pad - 20, y, 20, RowH),
          row.Result == "PASS" ? Good : row.Result == "FAIL" ? Bad : Warn, StringAlignment.Far);
      y += RowH;
    }
  }

  protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragFrom = e.Location; base.OnMouseDown(e); }

  protected override void OnMouseMove(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
    base.OnMouseMove(e);
  }

  protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SavePos(); base.OnMouseUp(e); }

  protected override bool ShowWithoutActivation { get { return true; } }

  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    timer.Stop();
    UiLook.Changed -= OnLook;
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    if (!hosted) Parts.Detach();
    base.OnFormClosed(e);
  }

  // Sample panels for --render, rated with the real sheet
  void RenderAll(string file) {
    var samples = new List<Tuple<string, Parts.Rating, string>> {
      Tuple.Create("Hoersch-Kessel Military-Grade Blaster", Parts.Rate("weapon", 4, new Dictionary<string, double> {
        { "red", 2600.1 }, { "mass", 5687.6 }, { "min", 913.5 }, { "max", 1287 }, { "vss", .625 }, { "vsa", .625 }, { "eps", 28 }, { "rr", .425 } }), (string)null),
      Tuple.Create("Sample Reactor Mk. III", Parts.Rate("reactor", 6, new Dictionary<string, double> { { "mass", 7100 }, { "gen", 27250 } }), (string)null),
      Tuple.Create("Sample Armor Plating", Parts.Rate("armor", 8, new Dictionary<string, double> { { "armor", 2105 }, { "mass", 6900 } }), (string)null),
      Tuple.Create("Prototype Reactor", Parts.Rate("reactor", 1, new Dictionary<string, double> { { "mass", 1500 }, { "gen", 8000 } }), (string)null),
      Tuple.Create("Maroj Melon", (Parts.Rating)null, "not a rateable ship component"),
    };
    var bmps = new List<Bitmap>();
    foreach (var s in samples) {
      Show(s.Item1, s.Item2, s.Item3);
      var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
      using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
      bmps.Add(bmp);
    }
    int gap = 12, totalW = 0, maxH = 0;
    foreach (var b in bmps) { totalW += b.Width + gap; maxH = Math.Max(maxH, b.Height); }
    using (var sheet = new Bitmap(totalW + gap, maxH + 2 * gap))
    using (var g = Graphics.FromImage(sheet)) {
      g.Clear(Color.FromArgb(70, 60, 45)); // desert-ish backdrop
      int x = gap;
      foreach (var b in bmps) { g.DrawImage(b, x, gap); x += b.Width + gap; b.Dispose(); }
      sheet.Save(file);
    }
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render") {
      using (var f = new PartsOverlay()) { f.timer.Stop(); f.tray.Visible = false; f.RenderAll(args[1]); }
      return;
    }
    Application.Run(new PartsOverlay());
  }
}
