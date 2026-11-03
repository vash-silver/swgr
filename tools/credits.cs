// Credits tracker: your cash and bank from the player's CreatureObject, with what you earned and spent this session,
// the net rate per hour and a log of the latest changes. Shares parts.cs's read-only process handle.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only (in parts.cs). Never add
// WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:CreditsTracker -out:../bin/swgr-credits.exe parts.cs ui.cs credits.cs
// usage: swgr-credits [--render out.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static partial class Parts {
  // Cash and bank are AutoDeltaVariable<int>s on the player's CreatureObject: vtable, then the value at +0x18 and a copy
  // at +0x1c. Found 2026-11-01 by tipping 1,234 credits (cash +0x518 dropped by exactly that). Each has its own vtable,
  // so after a patch they're found again by scanning for it. See notes/memory.md → "Credits".
  const ulong VT_CASH = 0x1cf2e58, VT_BANK = 0x1cf2f18;
  static int cashOffset = 0x500, bankOffset = 0x530;

  // (cash, bank, player address), or null when the game isn't readable
  internal static Tuple<int, int, ulong> ReadCredits() {
    ulong player = Ptr(baseAddr + RVA_PLAYER);
    if (!IsA(player, VT_CREATURE)) return null;
    if (!IsA(player + (ulong)cashOffset, VT_CASH) || !IsA(player + (ulong)bankOffset, VT_BANK)) {
      var cb = Read(player, 0x2000);
      if (cb == null) return null;
      int c = -1, b = -1;
      for (int o = 0; o + 0x20 <= cb.Length; o += 8) {
        ulong v = BitConverter.ToUInt64(cb, o);
        if (v == baseAddr + VT_CASH && c < 0) c = o;
        if (v == baseAddr + VT_BANK && b < 0) b = o;
      }
      if (c < 0 || b < 0) return null;
      cashOffset = c; bankOffset = b;
    }
    var cv = Read(player + (ulong)cashOffset + 0x18, 8);
    var bv = Read(player + (ulong)bankOffset + 0x18, 8);
    if (cv == null || bv == null) return null;
    int cash = BitConverter.ToInt32(cv, 0), bank = BitConverter.ToInt32(bv, 0);
    if (cash < 0 || bank < 0) return null;
    return Tuple.Create(cash, bank, player);
  }
}

sealed class CreditsTracker : LayeredForm {
  const int W = 300, Pad = 10, HeadH = 30, LogRows = 6, LogH = 17;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Label = Color.FromArgb(233, 214, 90), Gain = Color.FromArgb(110, 220, 130);
  static readonly Color Loss = Color.FromArgb(255, 110, 100), Accent = Color.FromArgb(233, 190, 70), Rule = Color.FromArgb(44, 52, 60);

  sealed class Change { public DateTime At; public long Delta; public string What; public bool Transfer; }

  readonly Timer timer = new Timer { Interval = 200 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-credits.pos");
  // The session (start, earned, spent, change log) survives restarts and relogs; only Reset session clears it
  readonly string sessionFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-credits.cfg");
  Font nameFont, rowFont, bigFont, smallFont;
  float scale = 1f;
  int designH;
  NotifyIcon tray;
  int reconnectTick, lastDrawSecond = -1;
  Point dragFrom;
  readonly bool hosted;

  // Committed balances (the last settled reading), the reading waiting to settle, and the session totals.
  // The balances on screen follow every read (200 ms). A change is committed to the log and session totals once the
  // reading has held still for SettleMs, so a cash -> bank move that the game applies in two steps still logs as one
  // transfer instead of a loss and a gain.
  int cash = -1, bank = -1, shownCash = -1, shownBank = -1;
  const int SettleMs = 800;
  DateTime pendingSince;
  const int SettleSeconds = 5;
  DateTime settleUntil;
  Tuple<int, int> pending;
  ulong playerAddr;
  long earned, spent;
  readonly List<Change> log = new List<Change>();
  DateTime since = DateTime.Now;

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  public CreditsTracker(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR credits tracker";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadPos();
    LoadSession();
    if (!File.Exists(sessionFile)) SaveSession(); // pin the session start now
    Restyle();
    UiLook.Changed += OnLook;

    UseOpacity("credits");
    var menu = new ContextMenuStrip();
    menu.Items.Add("Reset session", null, (s, e) => Reset());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR credits tracker", ContextMenuStrip = trayMenu, Visible = true };
    }

    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { nameFont, rowFont, bigFont, smallFont }) if (f != null) f.Dispose();
    nameFont = UiLook.PanelFont(UiLook.Strong, 10f);
    bigFont = UiLook.PanelFont(UiLook.Strong, 11f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 7.5f);
    scale = UiLook.Scale;
    Relayout();
  }

  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Left + 380, wa.Top + 480);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top); } catch { } }

  public void ResetSession() { Reset(); }

  void Reset() { earned = spent = 0; log.Clear(); since = DateTime.Now; SaveSession(); Relayout(); }

  // swgr-credits.cfg: "since <ticks>", "earned <n>", "spent <n>", then "log <ticks> <delta> <transfer 0/1> <what>" newest first
  void LoadSession() {
    try {
      foreach (var l in File.ReadAllLines(sessionFile)) {
        var p = l.Split(new[] { ' ' }, 5);
        if (p[0] == "since") since = new DateTime(long.Parse(p[1]));
        else if (p[0] == "earned") earned = long.Parse(p[1]);
        else if (p[0] == "spent") spent = long.Parse(p[1]);
        else if (p[0] == "log" && p.Length == 5)
          log.Add(new Change { At = new DateTime(long.Parse(p[1])), Delta = long.Parse(p[2]), Transfer = p[3] == "1", What = p[4] });
      }
    } catch { }
  }

  void SaveSession() {
    if (rendering) return;
    try {
      var lines = new List<string> { "since " + since.Ticks, "earned " + earned, "spent " + spent };
      foreach (var c in log) lines.Add(string.Format("log {0} {1} {2} {3}", c.At.Ticks, c.Delta, c.Transfer ? 1 : 0, c.What));
      File.WriteAllLines(sessionFile, lines);
    } catch { }
  }

  bool rendering;

  void Tick() {
    if (!Parts.Alive() && ++reconnectTick % 10 == 1) Parts.Attach(); // try every 2 s
    var r = Parts.Alive() ? Parts.ReadCredits() : null;
    if (tray != null) tray.Text = Parts.Alive() ? "SWGR credits tracker" : "SWGR credits tracker: waiting for game";
    // Nothing readable (game closed, loading screen, character select): hide but keep the session
    if (r == null) { if (Visible) Visible = false; pending = null; return; }
    Take(r.Item1, r.Item2, r.Item3);
    // Redraw every second even without a change: per hour (session net / session time) falls while nothing comes in,
    // and the session clock and "ago" times move on
    if (DateTime.Now.Second != lastDrawSecond) { lastDrawSecond = DateTime.Now.Second; Invalidate(); }
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  void Take(int c, int b, ulong player) {
    if (c != 0 || b != 0) {
      if (c != shownCash || b != shownBank) { shownCash = c; shownBank = b; Invalidate(); }
    }
    // First reading, or another character (or a relog): take the balances as they are, without counting them.
    // While a character loads, cash and bank read 0 before the real values arrive (seen 2026-11-03: the whole
    // balance logged as +1.06M earned), so a 0/0 reading is skipped (also mid-session, e.g. while zoning), and
    // anything that changes in the first few seconds after the baseline only moves the baseline.
    if (c == 0 && b == 0) { pending = null; return; }
    if (cash < 0 || player != playerAddr) {
      cash = c; bank = b; playerAddr = player; pending = null; shownCash = c; shownBank = b; settleUntil = DateTime.Now.AddSeconds(SettleSeconds);
      Relayout(); return;
    }
    if (DateTime.Now < settleUntil) {
      if (c != cash || b != bank) { cash = c; bank = b; settleUntil = DateTime.Now.AddSeconds(SettleSeconds); Relayout(); }
      pending = null; return;
    }
    if (c == cash && b == bank) { pending = null; return; }
    var now = Tuple.Create(c, b);
    if (pending == null || !pending.Equals(now)) { pending = now; pendingSince = DateTime.Now; return; }
    if ((DateTime.Now - pendingSince).TotalMilliseconds < SettleMs) return; // wait for it to settle
    pending = null;
    long dc = c - cash, db = b - bank, net = dc + db;
    var ch = new Change { At = DateTime.Now, Delta = net };
    if (net == 0) { ch.Transfer = true; ch.Delta = Math.Abs(dc); ch.What = dc < 0 ? "cash → bank" : "bank → cash"; }
    else {
      ch.What = dc != 0 && db != 0 ? "cash + bank" : dc != 0 ? "cash" : "bank";
      if (net > 0) earned += net; else spent -= net;
    }
    log.Insert(0, ch);
    if (log.Count > 50) log.RemoveAt(log.Count - 1);
    cash = c; bank = b;
    SaveSession();
    Relayout();
  }

  void Relayout() {
    int rows = Math.Max(1, Math.Min(LogRows, log.Count));
    designH = HeadH + 36 + 36 + 22 + rows * LogH + Pad;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  static string Full(long v) { return v.ToString("#,0", CultureInfo.InvariantCulture); }

  static string Signed(long v) { return (v > 0 ? "+" : v < 0 ? "−" : "") + Full(Math.Abs(v)); }

  static string Short(double v) {
    double a = Math.Abs(v);
    string s = a >= 1e6 ? (a / 1e6).ToString(a >= 1e7 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "M"
      : a >= 1e4 ? (a / 1e3).ToString(a >= 1e5 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k"
      : Math.Round(a).ToString("#,0", CultureInfo.InvariantCulture);
    return (v > 0.5 ? "+" : v < -0.5 ? "−" : "") + s;
  }

  static string Ago(DateTime t) {
    var s = DateTime.Now - t;
    return s.TotalMinutes < 1 ? "now" : s.TotalHours < 1 ? (int)s.TotalMinutes + "m" : (int)s.TotalHours + "h";
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
    using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, 4, designH);

    var span = DateTime.Now - since;
    DrawText(g, "Credits", nameFont, new RectangleF(Pad, 4, W - 2 * Pad - 90, 22), TextColor, StringAlignment.Near);
    // A running clock: m:ss, then h:mm:ss after the first hour
    var clock = span.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
      : string.Format("{0}:{1:00}", span.Minutes, span.Seconds);
    DrawText(g, "session " + clock, smallFont, new RectangleF(W - Pad - 90, 4, 90, 22), Muted, StringAlignment.Far);

    // Balances: cash, bank, total
    int y = HeadH;
    float col = (W - 2 * Pad) / 3f;
    string[] labels = { "Cash", "Bank", "Total" };
    long[] vals = { Math.Max(0, shownCash), Math.Max(0, shownBank), Math.Max(0, shownCash) + Math.Max(0, shownBank) };
    for (int i = 0; i < 3; i++) {
      var h = i == 0 ? StringAlignment.Near : i == 1 ? StringAlignment.Center : StringAlignment.Far;
      DrawText(g, labels[i], smallFont, new RectangleF(Pad + i * col, y, col, 14), Muted, h);
      DrawText(g, shownCash < 0 ? "-" : Full(vals[i]), i == 2 ? nameFont : rowFont, new RectangleF(Pad + i * col, y + 14, col, 20), i == 2 ? Label : TextColor, h);
    }
    y += 36;

    // Session: net, per hour, earned / spent
    long net = earned - spent;
    double hours = Math.Max(span.TotalHours, 1 / 60.0); // no wild rates in the first minute
    DrawText(g, "Session", smallFont, new RectangleF(Pad, y, 60, 14), Muted, StringAlignment.Near);
    DrawText(g, "Per hour", smallFont, new RectangleF(W - Pad - 100, y, 100, 14), Muted, StringAlignment.Far);
    DrawText(g, Signed(net), bigFont, new RectangleF(Pad, y + 12, 150, 22), net > 0 ? Gain : net < 0 ? Loss : TextColor, StringAlignment.Near);
    DrawText(g, net == 0 ? "-" : Short(net / hours), bigFont, new RectangleF(W - Pad - 120, y + 12, 120, 22), net > 0 ? Gain : net < 0 ? Loss : TextColor, StringAlignment.Far);
    y += 36;
    DrawText(g, string.Format("earned {0}   spent {1}", Short(earned), Short(-spent)), smallFont, new RectangleF(Pad, y, W - 2 * Pad, 14), Muted, StringAlignment.Near);
    y += 18;
    using (var p = new Pen(Rule)) g.DrawLine(p, Pad, y, W - Pad, y);
    y += 4;

    // Latest changes
    if (log.Count == 0) {
      DrawText(g, "No credit changes yet this session", rowFont, new RectangleF(Pad, y, W - 2 * Pad, LogH), Muted, StringAlignment.Near);
      return;
    }
    foreach (var c in log.Take(LogRows)) {
      DrawText(g, Ago(c.At), smallFont, new RectangleF(Pad, y, 34, LogH), Muted, StringAlignment.Near);
      DrawText(g, c.What, rowFont, new RectangleF(Pad + 36, y, 120, LogH), c.Transfer ? Muted : TextColor, StringAlignment.Near);
      DrawText(g, c.Transfer ? Full(c.Delta) : Signed(c.Delta), rowFont, new RectangleF(W - Pad - 120, y, 120, LogH),
        c.Transfer ? Muted : c.Delta > 0 ? Gain : Loss, StringAlignment.Far);
      y += LogH;
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

  // A sample session for --render: an hour of looting, a repair bill, a vendor sale and a deposit
  void RenderSample(string file) {
    rendering = true; // sample values, never saved
    since = DateTime.Now.AddMinutes(-62);
    cash = shownCash = 491578; bank = shownBank = 172170; playerAddr = 1;
    var now = DateTime.Now;
    log.AddRange(new[] {
      new Change { At = now.AddSeconds(-20), Delta = 1234, What = "cash" },
      new Change { At = now.AddMinutes(-3), Delta = 50000, What = "cash → bank", Transfer = true },
      new Change { At = now.AddMinutes(-9), Delta = -12850, What = "cash" },
      new Change { At = now.AddMinutes(-21), Delta = 125000, What = "bank" },
      new Change { At = now.AddMinutes(-40), Delta = 2210, What = "cash" },
      new Change { At = now.AddMinutes(-58), Delta = -1234, What = "cash" },
    });
    earned = 128444; spent = 14084;
    var shots = new List<Bitmap>();
    Relayout();
    var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
    using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
    shots.Add(bmp);
    log.Clear(); earned = spent = 0; since = DateTime.Now;
    Relayout();
    bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
    using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
    shots.Add(bmp);
    const int gap = 12;
    using (var sheet = new Bitmap(shots.Sum(b => b.Width + gap) + gap, shots.Max(b => b.Height) + 2 * gap))
    using (var g = Graphics.FromImage(sheet)) {
      g.Clear(Color.FromArgb(40, 30, 60));
      int x = gap;
      foreach (var b in shots) { g.DrawImage(b, x, gap); x += b.Width + gap; b.Dispose(); }
      sheet.Save(file);
    }
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render") {
      using (var f = new CreditsTracker()) { f.timer.Stop(); f.tray.Visible = false; f.RenderSample(args[1]); }
      return;
    }
    Application.Run(new CreditsTracker());
  }
}
