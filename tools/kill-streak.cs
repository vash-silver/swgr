// Kill streak banner: fades in on each kill, holds a few seconds, fades out (and briefly shows when a streak ends).
// Click-through; "Unlock to move" makes it draggable. It shows your current streak (kills less than N seconds apart) with a big number that pops on each kill,
// a tier title, a bar draining until the streak ends, the recent kills tallied per mob, and session totals.
// Fed by KillSound.Killed (kill-sound.cs); this file has no client memory access of its own.
//
// build (standalone preview): cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:KillStreak -out:../bin/swgr-streak.exe ui.cs kill-streak.cs
// usage: swgr-streak [--render out.png]   (no args: a live demo with fake kills)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

sealed class KillStreak : LayeredForm {
  const int W = 440, TextH = 56, NameH = 24; // design units: the streak line, and the optional "<mob> killed" line
  public static readonly int[] Windows = { 20, 30, 45, 60 };
  public static readonly int[] Holds = { 3, 5, 8 }; // seconds the banner stays after a kill
  const double FadeIn = 0.25, FadeOut = 0.9, EndHold = 3.5;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Track = Color.FromArgb(38, 44, 52);

  // Tiers: from this many kills, the title and colour
  static readonly int[] TierAt = { 1, 2, 3, 5, 8, 12, 20, 30 };
  static readonly string[] TierName = { "KILL", "DOUBLE", "TRIPLE", "HOT STREAK", "RELENTLESS", "MERCILESS", "LEGENDARY", "GALACTIC THREAT" };
  static readonly Color[] TierColor = {
    Color.FromArgb(80, 210, 200), Color.FromArgb(96, 200, 240), Color.FromArgb(120, 220, 120), Color.FromArgb(240, 200, 70),
    Color.FromArgb(255, 150, 60), Color.FromArgb(255, 84, 76), Color.FromArgb(214, 110, 255), Color.FromArgb(255, 226, 120) };

  sealed class Tally { public string Name; public int Count; public DateTime Last; }

  readonly Timer timer = new Timer { Interval = 33 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-streak.cfg");
  readonly List<Tally> recent = new List<Tally>(); // this streak's kills per mob, newest first
  readonly bool hosted;
  Font bigFont, titleFont, rowFont, smallFont, nameFont;
  bool showName;
  float scale = 1f;
  int window = 30, best, sessionKills;
  int hold = 5;
  bool unlocked, endShown;
  DateTime appearAt, showUntil;
  Point home; // where the banner sits (it slides up into place)
  int streak, lastStreak, lastTier = -1;
  DateTime lastKill, streakStart, sessionStart = DateTime.Now, tierChanged;
  Point dragFrom;
  NotifyIcon tray;

  public int Count { get { return Alive ? streak : 0; } }
  bool Alive { get { return streak > 0 && (Now - lastKill).TotalSeconds < window; } }
  DateTime? frozen; // for --render
  DateTime Now { get { return frozen ?? DateTime.Now; } }

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; else Step(); }
  }
  public int Window { get { return window; } set { window = value; SavePos(); } }
  public bool ShowName { get { return showName; } set { showName = value; SavePos(); Relayout(); } }
  public int HoldSeconds { get { return hold; } set { hold = value; SavePos(); } }

  // Unlocked: fully shown with a dashed frame, draggable and right-clickable; locking saves the position
  public bool Unlocked {
    get { return unlocked; }
    set { unlocked = value; SetClickThrough(!value); if (!value) SavePos(); else if (timer.Enabled && !Visible) Visible = true; Step(); }
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
  void SetClickThrough(bool on) {
    int ex = GetWindowLong(Handle, -20);
    SetWindowLong(Handle, -20, on ? ex | 0x20 : ex & ~0x20);
  }

  public KillStreak(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR kill streak";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Color.Black;
    BackOpacity = 0; // text only: no panel behind it
    home = Location = LoadPos();
    Fade = 0;
    Restyle();
    UiLook.Changed += OnLook;

    var menu = new ContextMenuStrip();
    var wm = new ToolStripMenuItem("Streak window");
    foreach (var w in Windows) { int v = w; wm.DropDownItems.Add(new ToolStripMenuItem(v + " s between kills", null, (s, e) => Window = v)); }
    wm.DropDownOpening += (s, e) => { for (int i = 0; i < Windows.Length; i++) ((ToolStripMenuItem)wm.DropDownItems[i]).Checked = Windows[i] == window; };
    menu.Items.Add(wm);
    var hm = new ToolStripMenuItem("Show for");
    foreach (var h in Holds) { int v = h; hm.DropDownItems.Add(new ToolStripMenuItem(v + " s after a kill", null, (s, e) => HoldSeconds = v)); }
    hm.DropDownOpening += (s, e) => { for (int i = 0; i < Holds.Length; i++) ((ToolStripMenuItem)hm.DropDownItems[i]).Checked = Holds[i] == hold; };
    menu.Items.Add(hm);
    menu.Items.Add("Lock position", null, (s, e) => Unlocked = false);
    menu.Items.Add("Test kill", null, (s, e) => Test());
    menu.Items.Add("Reset session", null, (s, e) => ResetSession());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR kill streak", ContextMenuStrip = trayMenu, Visible = true };
    }
    timer.Tick += (s, e) => Step();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { bigFont, titleFont, rowFont, smallFont, nameFont }) if (f != null) f.Dispose();
    bigFont = UiLook.PanelFont(UiLook.Strong, 30f);
    titleFont = UiLook.PanelFont(UiLook.Strong, 16f);
    nameFont = UiLook.PanelFont(UiLook.Regular, 12f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 7.5f);
    scale = UiLook.Scale;
    Relayout();
  }

  // swgr-streak.cfg: "x,y,window,hold,best,showName"
  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      if (p.Length > 2) window = Math.Max(5, int.Parse(p[2]));
      if (p.Length > 3) hold = Math.Max(1, int.Parse(p[3]));
      if (p.Length > 4) best = int.Parse(p[4]);
      if (p.Length > 5) showName = p[5].Trim() == "1";
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Left + wa.Width / 2 - (int)(W * UiLook.Scale * UiLook.Dpi) / 2, wa.Top + wa.Height * 12 / 100); // top centre
  }

  void SavePos() { if (frozen != null) return; // --render never touches the saved file
    try { File.WriteAllText(posFile, home.X + "," + home.Y + "," + window + "," + hold + "," + best + "," + (showName ? 1 : 0)); } catch { } }


  void Relayout() {
    int h = TextH + (showName ? NameH : 0) + 4;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(h * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  // ---- kills ----

  public void Add(string name) { Add(name, false); }

  // test: a sample kill (Test kill, previews) that doesn't count toward the session or the best streak
  void Add(string name, bool test) {
    var now = Now;
    if (!Alive) { if (streak > 0) lastStreak = streak; streak = 0; recent.Clear(); streakStart = now; }
    streak++;
    if (!test) sessionKills++;
    lastKill = now;
    var t = recent.FirstOrDefault(r => r.Name == name);
    if (t == null) t = new Tally { Name = name };
    else recent.Remove(t);
    t.Count++; t.Last = now;
    recent.Insert(0, t);
    int tier = Tier(streak);
    if (tier != lastTier) { lastTier = tier; tierChanged = now; }
    if (!test && streak > best) { best = streak; SavePos(); }
    endShown = false;
    if (Fade < 0.05 || !Visible) appearAt = now; // coming in from hidden: fade in again
    showUntil = now.AddSeconds(hold);
    Relayout();
    Step();
  }

  public void Test() {
    var names = new[] { "a dune lizard", "a vicious Rill", "Tusken Raider", "a dune lizard" };
    Add(names[streak % names.Length], true);
  }

  public void ResetSession() { sessionKills = 0; best = streak = lastStreak = 0; lastTier = -1; recent.Clear(); sessionStart = DateTime.Now; SavePos(); Relayout(); }

  static int Tier(int n) { int t = 0; for (int i = 0; i < TierAt.Length; i++) if (n >= TierAt[i]) t = i; return t; }

  static double Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

  void Step() {
    if (!timer.Enabled) return;
    var now = Now;
    // a streak of 2+ just ran out: show the result briefly
    if (streak >= 2 && !Alive && !endShown) { endShown = true; if (Fade < 0.05 || !Visible) appearAt = now; showUntil = now.AddSeconds(EndHold); }
    double tIn = (now - appearAt).TotalSeconds, tOut = (now - showUntil).TotalSeconds;
    double a = unlocked ? 1 : Smooth(tIn / FadeIn) * (tOut <= 0 ? 1 : 1 - Smooth(tOut / FadeOut));
    if (a <= 0.002) { Fade = 0; if (Visible) Visible = false; timer.Interval = 250; return; }
    Fade = a;
    int rise = unlocked ? 0 : (int)Math.Round((1 - Smooth(tIn / 0.35)) * 14 * UiLook.Dpi);
    var at = new Point(home.X, home.Y + rise);
    if (Location != at) Location = at;
    if (!Visible) { Visible = true; SetClickThrough(!unlocked); }
    timer.Interval = 33;
    KeepOnTop();
    Invalidate();
  }

  // ---- drawing ----

  static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
  }

  static Color Mix(Color a, Color b, double t) {
    t = Math.Max(0, Math.Min(1, t));
    return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
  }

  static string Ago(double s) { return s < 60 ? (int)s + "s" : (int)(s / 60) + "m"; }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var now = Now;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    if (unlocked) using (var p = new Pen(Color.FromArgb(220, 80, 210, 200), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
    g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);

    bool alive = Alive;
    int tier = Tier(Math.Max(1, streak));
    Color tc = alive ? TierColor[tier] : Muted;
    double sinceKill = (now - lastKill).TotalSeconds;
    string num = streak > 0 ? streak.ToString(CultureInfo.InvariantCulture) : "0";
    string title = alive ? TierName[tier] : streak > 0 ? "STREAK ENDED" : unlocked ? "DRAG ME" : "KILL STREAK";
    double pop = alive ? Math.Max(0, 1 - sinceKill / 0.3) : 0;
    double newTier = alive ? Math.Max(0, 1 - (now - tierChanged).TotalSeconds / 0.8) : 0;

    using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone()) {
      fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
      float wNum = g.MeasureString(num, bigFont, 10000, fmt).Width, wTitle = g.MeasureString(title, titleFont, 10000, fmt).Width;
      const float gap = 10;
      float x = (W - wNum - gap - wTitle) / 2, cy = TextH / 2f;
      // the number pops (1.4x shrinking to 1x over 0.3 s) around its own centre, with a glow in the tier colour
      var st = g.Save();
      g.TranslateTransform(x + wNum / 2, cy);
      float sc = (float)(1 + 0.4 * pop * pop);
      g.ScaleTransform(sc, sc);
      OutlinedText(g, num, bigFont, new PointF(-wNum / 2, -bigFont.GetHeight(g) / 2), alive ? Mix(tc, Color.White, 0.2 + 0.5 * pop) : Muted, fmt, Color.FromArgb((int)(150 * pop), tc));
      g.Restore(st);
      // the tier title, sliding in and brightening when the tier changes
      float tx = x + wNum + gap + (float)(newTier * newTier * 14);
      OutlinedText(g, title, titleFont, new PointF(tx, cy - titleFont.GetHeight(g) / 2 + 2), Mix(tc, Color.White, newTier * 0.6), fmt, Color.Empty);

      // optional: the mob that was just killed, small, under the streak
      if (showName && recent.Count > 0) {
        string line = recent[0].Name + " killed";
        float wl = g.MeasureString(line, nameFont, 10000, fmt).Width;
        OutlinedText(g, line, nameFont, new PointF((W - wl) / 2, TextH + 1), Color.FromArgb(215, 222, 228), fmt, Color.Empty);
      }
    }
  }

  // Text with a soft dark shadow and thin outline (readable over any scenery), plus an optional coloured glow
  static void OutlinedText(Graphics g, string s, Font f, PointF at, Color fill, StringFormat fmt, Color glow) {
    using (var path = new GraphicsPath()) {
      path.AddString(s, f.FontFamily, (int)f.Style, f.Size, at, fmt);
      if (glow.A > 0) using (var p = new Pen(glow, f.Size * 0.22f) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      for (int i = 3; i >= 1; i--)
        using (var p = new Pen(Color.FromArgb(190 / (i + 2), 0, 0, 0), f.Size * 0.09f * i) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      using (var p = new Pen(Color.FromArgb(190, 0, 0, 0), Math.Max(1f, f.Size * 0.06f)) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      using (var b = new SolidBrush(fill)) g.FillPath(b, path);
    }
  }

  protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragFrom = e.Location; base.OnMouseDown(e); }
  protected override void OnMouseMove(MouseEventArgs e) {
    if (unlocked && e.Button == MouseButtons.Left) home = Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
    base.OnMouseMove(e);
  }
  protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SavePos(); base.OnMouseUp(e); }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x20 | 0x08000000 | 0x80; return cp; } // WS_EX_TRANSPARENT (click-through) | NOACTIVATE | TOOLWINDOW
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    timer.Stop();
    UiLook.Changed -= OnLook;
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    base.OnFormClosed(e);
  }

  // --render: the panel at several moments of a streak, side by side
  void RenderSamples(string file) {
    var t0 = new DateTime(2026, 11, 2, 20, 0, 0);
    var shots = new List<Bitmap>();
    Action<double> snap = s => {
      frozen = t0.AddSeconds(s);
      Relayout();
      var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
      using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(150, 128, 100)); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
      shots.Add(bmp);
    };
    sessionStart = t0.AddMinutes(-40); sessionKills = 37; best = 9;
    var seq = new[] { "a dune lizard", "a dune lizard", "a vicious Rill", "Tusken Raider", "a dune lizard", "Tusken Raider", "a dune lizard", "a womp rat", "a dune lizard", "Tusken Raider", "a dune lizard", "a dune lizard" };
    double at = 0;
    frozen = t0; Add(seq[0]); snap(0.05);                           // first kill, mid-pop
    for (int i = 1; i < 3; i++) { at += 9; frozen = t0.AddSeconds(at); Add(seq[i]); } snap(at + 0.12); // triple, tier just changed
    for (int i = 3; i < 8; i++) { at += 8; frozen = t0.AddSeconds(at); Add(seq[i]); } snap(at + 4);  // 8: relentless
    for (int i = 8; i < 12; i++) { at += 7; frozen = t0.AddSeconds(at); Add(seq[i]); } snap(at + 24); // 12, timer almost out
    snap(at + 33);                                                   // ended
    int gap = 12, w = shots.Sum(b => b.Width) + gap * (shots.Count + 1), h = shots.Max(b => b.Height) + 2 * gap;
    using (var all = new Bitmap(w, h)) {
      using (var g = Graphics.FromImage(all)) {
        g.Clear(Color.FromArgb(120, 104, 82));
        int x = gap;
        foreach (var b in shots) { g.DrawImage(b, x, gap); x += b.Width + gap; b.Dispose(); }
      }
      all.Save(file);
    }
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render") {
      // out.png without the mob line, out-name.png with it
      using (var f = new KillStreak()) { f.tray.Visible = false; f.showName = false; f.RenderSamples(args[1]); }
      using (var f = new KillStreak()) { f.tray.Visible = false; f.showName = true; f.RenderSamples(Path.ChangeExtension(args[1], null) + "-name.png"); }
      return;
    }
    var ks = new KillStreak();
    ks.Active = true;
    var demo = new Timer { Interval = 2500 };
    demo.Tick += (s, e) => ks.Test();
    demo.Start();
    ks.Test();
    Application.Run(ks);
  }
}
