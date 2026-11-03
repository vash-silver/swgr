// Threat panel: every mob near you that is attacking a player, for tanking. Loose ones (on someone else) come
// first in red with whom they're hitting, then the ones on you in green. Reads client memory through overlay.cs's
// GameReader, which opens the process with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only. Never add
// WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:ThreatPanel -out:../bin/swgr-threat.exe overlay.cs ui.cs threat.cs
// usage: swgr-threat [--render out.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class ThreatPanel : LayeredForm {
  const int W = 250, Pad = 8, HeadH = 22, RowH = 22, MaxRows = 12;
  public static readonly float[] Ranges = { 30, 50, 80, 120 };

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Track = Color.FromArgb(38, 44, 52), HealthC = Color.FromArgb(226, 84, 84);
  static readonly Color You = Color.FromArgb(80, 210, 120), Loose = Color.FromArgb(255, 84, 76);

  readonly GameReader reader = new GameReader();
  readonly Timer timer = new Timer { Interval = 200 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-threat.pos");
  Font headFont, rowFont, smallFont;
  float scale = 1f, range = 50;
  bool always; // Always visible: keep the panel up (with "No aggro") when nothing is attacking
  bool connected;
  int designH = HeadH;
  NotifyIcon tray;
  int reconnectTick;
  Point dragFrom;
  List<Threat> rows = new List<Threat>();
  readonly bool hosted;
  readonly List<ToolStripMenuItem> rangeItems = new List<ToolStripMenuItem>();

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  // Always visible (also offered in the tools tray menu, since the panel is hidden while there's no aggro)
  public bool AlwaysVisible {
    get { return always; }
    set { always = value; SavePos(); if (timer.Enabled) Tick(); }
  }

  public ThreatPanel(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR threat";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadPos();
    Restyle();
    UiLook.Changed += OnLook;

    UseOpacity("threat");
    var menu = new ContextMenuStrip();
    var rm = new ToolStripMenuItem("Range");
    foreach (var r in Ranges) {
      float v = r;
      var it = new ToolStripMenuItem(v + " m", null, (s, e) => { range = v; SyncRange(); SavePos(); }) { Checked = v == range };
      rangeItems.Add(it);
      rm.DropDownItems.Add(it);
    }
    menu.Items.Add(rm);
    var alwaysItem = new ToolStripMenuItem("Always visible", null, (s, e) => AlwaysVisible = !always) { Checked = always };
    menu.Opening += (s, e) => alwaysItem.Checked = always;
    menu.Items.Add(alwaysItem);
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR threat", ContextMenuStrip = trayMenu, Visible = true };
    }

    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  // For the swgr-tools main window
  public float Range { get { return range; } set { range = value; SyncRange(); SavePos(); if (timer.Enabled) Tick(); } }

  void SyncRange() { for (int i = 0; i < Ranges.Length; i++) rangeItems[i].Checked = Ranges[i] == range; }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { headFont, rowFont, smallFont }) if (f != null) f.Dispose();
    headFont = UiLook.PanelFont(UiLook.Strong, 8.5f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 7.5f);
    scale = UiLook.Scale;
    Resize();
  }

  // swgr-threat.pos holds "x,y[,range[,always]]"
  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      if (p.Length > 2) range = float.Parse(p[2], CultureInfo.InvariantCulture);
      if (p.Length > 3) always = p[3].Trim() == "1";
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Right - W - 340, wa.Top + 160);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top + "," + range.ToString(CultureInfo.InvariantCulture) + "," + (always ? 1 : 0)); } catch { } }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

  void Tick() {
    if (!reader.Connected) { if (++reconnectTick % 10 == 1) reader.Connect(); } // try every 2 s
    var list = reader.Connected ? reader.ReadThreats(range) : null;
    if (tray != null) tray.Text = reader.Connected ? "SWGR threat" : "SWGR threat: waiting for game";
    connected = list != null;
    // Shown only while something is attacking a player near you, unless Always visible is on
    if (list == null || list.Count == 0) {
      rows.Clear();
      if (!always) { if (Visible) Visible = false; return; }
    } else rows = list;
    Resize();
    Invalidate();
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  new void Resize() {
    designH = HeadH + (rows.Count == 0 ? RowH : Math.Min(rows.Count, MaxRows) * RowH) + (rows.Count > MaxRows ? 14 : 0) + 4;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
  }

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

    int onYou = 0, loose = 0;
    foreach (var t in rows) if (t.OnYou) onYou++; else loose++;
    DrawText(g, "THREAT", headFont, new RectangleF(Pad, 2, 70, HeadH - 4), Muted, StringAlignment.Near);
    float x = W - Pad;
    if (loose > 0) {
      string s = loose + " loose";
      float w = g.MeasureString(s, headFont).Width;
      DrawText(g, s, headFont, new RectangleF(x - w, 2, w, HeadH - 4), Loose, StringAlignment.Far);
      x -= w + 8;
    }
    if (onYou > 0) DrawText(g, onYou + " on you", headFont, new RectangleF(Pad + 60, 2, x - Pad - 60, HeadH - 4), You, StringAlignment.Far);
    using (var sep = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(sep, Pad, HeadH - 1, W - Pad, HeadH - 1);

    int y = HeadH;
    if (rows.Count == 0) DrawText(g, connected ? "No aggro within " + range + " m" : "Waiting for game", smallFont, new RectangleF(Pad + 2, y, W - 2 * Pad, RowH - 2), Muted, StringAlignment.Near);
    for (int i = 0; i < rows.Count && i < MaxRows; i++, y += RowH) {
      var t = rows[i];
      var c = t.OnYou ? You : Loose;
      if (!t.OnYou) using (var band = new SolidBrush(Color.FromArgb(36, Loose))) g.FillRectangle(band, 0, y, W, RowH);
      using (var b = new SolidBrush(c)) g.FillRectangle(b, 0, y + 2, 4, RowH - 4);
      string who = t.OnYou ? "you" : "→ " + t.Target;
      float whoW = Math.Min(110, g.MeasureString(who, smallFont).Width + 2);
      DrawText(g, string.Format("{0:0}m", t.Distance), smallFont, new RectangleF(W - Pad - 30, y, 30, RowH - 5), Muted, StringAlignment.Far);
      DrawText(g, who, smallFont, new RectangleF(W - Pad - 34 - whoW, y, whoW, RowH - 5), c, StringAlignment.Far);
      DrawText(g, t.Name, rowFont, new RectangleF(Pad + 2, y, W - 2 * Pad - 40 - whoW, RowH - 5), TextColor, StringAlignment.Near);
      double frac = t.HealthMax > 0 ? Math.Max(0, Math.Min(1, (double)t.Health / t.HealthMax)) : 0;
      using (var b = new SolidBrush(Track)) g.FillRectangle(b, Pad + 2, y + RowH - 5, W - 2 * Pad - 2, 2.5f);
      using (var b = new SolidBrush(HealthC)) g.FillRectangle(b, Pad + 2, y + RowH - 5, (float)((W - 2 * Pad - 2) * frac), 2.5f);
    }
    if (rows.Count > MaxRows) DrawText(g, "+" + (rows.Count - MaxRows) + " more", smallFont, new RectangleF(Pad, y, W - 2 * Pad, 14), Muted, StringAlignment.Near);
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
    reader.Dispose();
    base.OnFormClosed(e);
  }

  // --render <out.png>: a sample fight (no game needed)
  void RenderSample(string file) {
    rows = new List<Threat> {
      new Threat { Name = "a dune bantha", Target = "Bealdwine", Health = 3100, HealthMax = 5500, Distance = 14 },
      new Threat { Name = "a desert eopie", OnYou = true, Health = 1006, HealthMax = 2380, Distance = 3.5f },
      new Threat { Name = "a desert eopie", OnYou = true, Health = 5050, HealthMax = 5050, Distance = 3.6f },
      new Threat { Name = "a swarming lesser dewback", OnYou = true, Health = 400, HealthMax = 2100, Distance = 6 },
    };
    Resize();
    using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height)) {
      using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
      bmp.Save(file);
    }
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render") {
      using (var f = new ThreatPanel()) { f.timer.Stop(); f.tray.Visible = false; f.RenderSample(args[1]); }
      return;
    }
    Application.Run(new ThreatPanel());
  }
}
