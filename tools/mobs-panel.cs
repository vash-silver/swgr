// Nearby mobs panel: the NPCs/creatures around you (by default only ones fighting or hurt) with a full HP bar (name, level, current / max, distance), in the
// Questlog look of the target overlay. Mobs on you are marked green, mobs on another player red, your target outlined.
// Reads client memory through overlay.cs's GameReader, which opens the process with PROCESS_QUERY_INFORMATION |
// PROCESS_VM_READ only. Never add WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any
// other access right.
//
// build (standalone): cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:NearbyMobs -out:../bin/swgr-mobs.exe overlay.cs ui.cs mobs-panel.cs
// usage: swgr-mobs [--render out.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

sealed class NearbyMobs : LayeredForm {
  const int W = 300, Pad = 8, HeadH = 30, RowH = 24, RowStep = 27;
  public static readonly string[] Shows = { "All mobs", "In combat only", "Aggro or damaged" };
  public static readonly string[] Sorts = { "Fighting first", "Closest", "Lowest HP" };
  public static readonly int[] Ranges = { 30, 50, 80, 120 };
  public static readonly int[] RowCounts = { 5, 8, 12, 16 };

  static readonly Color Back = Color.FromArgb(18, 18, 18);
  static readonly Color OnYouC = Color.FromArgb(80, 210, 120), LooseC = Color.FromArgb(255, 84, 76);

  readonly GameReader reader = new GameReader();
  readonly Timer timer = new Timer { Interval = 250 };
  readonly string cfgFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-mobs.cfg");
  readonly bool hosted;
  readonly Dictionary<ulong, double> trail = new Dictionary<ulong, double>(); // damage trail per mob (fraction)
  DateTime lastTick = DateTime.Now;
  Font nameF, numF, smallF, headF;
  float scale = 1f;
  int show = 2, sort = 0, range = 50, maxRows = 8;
  bool always, connected;
  List<NearbyMob> rows = new List<NearbyMob>();
  int total, onYou, loose;
  int reconnectTick;
  Point dragFrom;
  NotifyIcon tray;

  public bool Active { get { return timer.Enabled; } set { timer.Enabled = value; if (!value) Visible = false; else Tick(); } }
  public int Filter { get { return show; } set { show = value; Save(); Tick(); } }
  public int Sort { get { return sort; } set { sort = value; Save(); Tick(); } }
  public int Range { get { return range; } set { range = value; Save(); Tick(); } }
  public int MaxRows { get { return maxRows; } set { maxRows = value; Save(); Tick(); } }
  public bool AlwaysVisible { get { return always; } set { always = value; Save(); Tick(); } }

  protected override float CornerRadius { get { return 10 * scale * UiLook.Dpi; } }

  public NearbyMobs(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR nearby mobs";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadCfg();
    Restyle();
    UiLook.Changed += OnLook;
    OverlayForm.QlColorsChanged += Invalidate;
    UseOpacity("mobs");

    var menu = new ContextMenuStrip();
    menu.Items.Add(Pick("Show", Shows, () => show, v => Filter = v));
    menu.Items.Add(Pick("Sort", Sorts, () => sort, v => Sort = v));
    menu.Items.Add(Pick("Range", Ranges.Select(r => r + " m").ToArray(), () => Array.IndexOf(Ranges, range), v => Range = Ranges[v]));
    menu.Items.Add(Pick("Rows", RowCounts.Select(r => r + " rows").ToArray(), () => Array.IndexOf(RowCounts, maxRows), v => MaxRows = RowCounts[v]));
    var alwaysItem = new ToolStripMenuItem("Always visible", null, (s, e) => AlwaysVisible = !always);
    menu.Opening += (s, e) => alwaysItem.Checked = always;
    menu.Items.Add(alwaysItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR nearby mobs", ContextMenuStrip = trayMenu, Visible = true };
    }
    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  static ToolStripMenuItem Pick(string title, string[] items, Func<int> get, Action<int> set) {
    var m = new ToolStripMenuItem(title);
    for (int i = 0; i < items.Length; i++) { int v = i; m.DropDownItems.Add(new ToolStripMenuItem(items[i], null, (s, e) => set(v))); }
    m.DropDownOpening += (s, e) => { for (int i = 0; i < items.Length; i++) ((ToolStripMenuItem)m.DropDownItems[i]).Checked = i == get(); };
    return m;
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { nameF, numF, smallF, headF }) if (f != null) f.Dispose();
    nameF = UiLook.MakeFont("Encode Sans SemiBold", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    headF = UiLook.MakeFont("Encode Sans SemiBold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    numF = UiLook.MakeFont("Encode Sans", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    smallF = UiLook.MakeFont("Encode Sans", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
    scale = UiLook.Scale;
    Relayout();
  }

  // swgr-mobs.cfg: "x,y,show,sort,range,rows,always"
  Point LoadCfg() {
    try {
      var p = File.ReadAllText(cfgFile).Split(',');
      show = int.Parse(p[2]); sort = int.Parse(p[3]); range = int.Parse(p[4]); maxRows = int.Parse(p[5]); always = p[6].Trim() == "1";
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Left + 40, wa.Top + wa.Height / 2 - 100);
  }

  void Save() { try { File.WriteAllText(cfgFile, Left + "," + Top + "," + show + "," + sort + "," + range + "," + maxRows + "," + (always ? 1 : 0)); } catch { } }

  void Relayout() {
    int n = Math.Max(1, Math.Min(rows.Count, maxRows));
    int h = HeadH + n * RowStep + (rows.Count > maxRows ? 16 : 0) + 6;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(h * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  void Tick() {
    if (!timer.Enabled) return;
    if (!reader.Connected) { if (++reconnectTick % 8 == 1) reader.Connect(); }
    var list = reader.Connected ? reader.ReadNearby(range) : null;
    connected = list != null;
    if (tray != null) tray.Text = connected ? "SWGR nearby mobs" : "SWGR nearby mobs: waiting for game";
    list = list ?? new List<NearbyMob>();
    Apply(list, DateTime.Now);
    if (rows.Count == 0 && !always) { if (Visible) Visible = false; return; }
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  void Apply(List<NearbyMob> list, DateTime now) {
    total = list.Count; onYou = list.Count(m => m.OnYou); loose = list.Count(m => m.OnOther != null);
    // 1: fighting (or your target); 2: fighting or hurt, so untouched mobs stay out
    IEnumerable<NearbyMob> q = show == 1 ? list.Where(m => m.InCombat || m.IsTarget) : show == 2 ? list.Where(m => m.InCombat || m.Health < m.HealthMax) : list;
    if (sort == 1) q = q.OrderBy(m => m.Distance);
    else if (sort == 2) q = q.OrderBy(m => (double)m.Health / m.HealthMax).ThenBy(m => m.Distance);
    else q = q.OrderBy(m => m.OnOther != null ? 0 : m.OnYou ? 1 : m.IsTarget ? 2 : 3).ThenBy(m => m.Distance); // loose ones first: they need a taunt
    rows = q.ToList();
    total = rows.Count; // the header counts what's listed
    // damage trail: the lost part fades down at 60% of the bar per second
    double dt = Math.Min(1, (now - lastTick).TotalSeconds); lastTick = now;
    var seen = new HashSet<ulong>();
    foreach (var m in rows) {
      double f = (double)m.Health / m.HealthMax, tr;
      seen.Add(m.Id);
      trail[m.Id] = trail.TryGetValue(m.Id, out tr) && tr > f ? Math.Max(f, tr - 0.6 * dt) : f;
    }
    foreach (var id in trail.Keys.Where(k => !seen.Contains(k)).ToList()) trail.Remove(id);
    Relayout();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    float k = scale * UiLook.Dpi;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using (var path = RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10 * k)) using (var p = new Pen(OverlayForm.QlBorder)) g.DrawPath(p, path);
    g.ScaleTransform(k, k);

    // header: title and counts, range on the right
    float tw = g.MeasureString("Nearby", headF).Width;
    OverlayForm.QlShadowText(g, "Nearby", headF, new RectangleF(10, 4, 80, 22), OverlayForm.QlText, StringAlignment.Near);
    float x = 10 + tw + 4;
    string count = total + (total == 1 ? " mob" : " mobs");
    DrawT(g, count, numF, new RectangleF(x, 5, 70, 22), OverlayForm.QlDarker, StringAlignment.Near);
    x += g.MeasureString(count, numF).Width + 6;
    if (onYou > 0) { string s = onYou + " on you"; DrawT(g, s, numF, new RectangleF(x, 5, 70, 22), OnYouC, StringAlignment.Near); x += g.MeasureString(s, numF).Width + 6; }
    if (loose > 0) DrawT(g, loose + " loose", numF, new RectangleF(x, 5, 70, 22), LooseC, StringAlignment.Near);
    DrawT(g, range + " m", smallF, new RectangleF(W - 60, 5, 50, 22), OverlayForm.QlDarker, StringAlignment.Far);

    float y = HeadH;
    if (rows.Count == 0) {
      DrawT(g, !connected ? "Waiting for game" : show == 1 ? "Nothing fighting within " + range + " m" : show == 2 ? "No aggro or hurt mobs within " + range + " m" : "No mobs within " + range + " m", numF, new RectangleF(12, y, W - 24, RowH), OverlayForm.QlDarker, StringAlignment.Near);
      return;
    }
    for (int i = 0; i < rows.Count && i < maxRows; i++, y += RowStep) {
      var m = rows[i];
      var rc = new RectangleF(Pad, y, W - 2 * Pad, RowH);
      double f = (double)m.Health / m.HealthMax, tr;
      if (!trail.TryGetValue(m.Id, out tr)) tr = f;
      OverlayForm.QlBar(g, rc, f, tr, OverlayForm.QlHealth, 6);
      if (m.IsTarget) using (var path = RoundRect(rc, 6)) using (var p = new Pen(OverlayForm.QlAccent, 1.5f)) g.DrawPath(p, path);
      // who it's fighting: a stripe on the left
      if (m.InCombat) using (var b = new SolidBrush(m.OnYou ? OnYouC : LooseC)) g.FillRectangle(b, rc.X + 2, rc.Y + 4, 3, rc.Height - 8);

      float nx = rc.X + 10, right = rc.Right - 6;
      string dist = Math.Round(m.Distance) + "m", hp = m.Health.ToString("N0") + " / " + m.HealthMax.ToString("N0");
      float dw = 30, hw = g.MeasureString(hp, numF).Width + 2;
      OverlayForm.QlShadowText(g, dist, smallF, new RectangleF(right - dw, rc.Y, dw, rc.Height), OverlayForm.QlDarker, StringAlignment.Far);
      OverlayForm.QlShadowText(g, hp, numF, new RectangleF(right - dw - 4 - hw, rc.Y, hw, rc.Height), OverlayForm.QlDark, StringAlignment.Far);
      string tag = m.OnOther != null ? "→ " + m.OnOther : m.Level > 0 ? "Lv " + m.Level : "";
      float nameMax = right - dw - hw - 10 - nx;
      float tagW = tag.Length > 0 ? Math.Min(nameMax * 0.45f, g.MeasureString(tag, smallF).Width + 2) : 0;
      float nameW = Math.Min(nameMax - tagW - 4, g.MeasureString(m.Name, nameF).Width + 2);
      OverlayForm.QlShadowText(g, m.Name, nameF, new RectangleF(nx, rc.Y, nameW, rc.Height), OverlayForm.QlText, StringAlignment.Near);
      if (tagW > 0) OverlayForm.QlShadowText(g, tag, smallF, new RectangleF(nx + nameW + 4, rc.Y + 1, tagW, rc.Height), m.OnOther != null ? LooseC : OverlayForm.QlDarker, StringAlignment.Near);
    }
    if (rows.Count > maxRows) DrawT(g, "+" + (rows.Count - maxRows) + " more", smallF, new RectangleF(12, y - 2, W - 24, 16), OverlayForm.QlDarker, StringAlignment.Near);
  }

  static void DrawT(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
  }

  protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragFrom = e.Location; base.OnMouseDown(e); }
  protected override void OnMouseMove(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
    base.OnMouseMove(e);
  }
  protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Save(); base.OnMouseUp(e); }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    timer.Stop();
    UiLook.Changed -= OnLook;
    OverlayForm.QlColorsChanged -= Invalidate;
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    reader.Dispose();
    base.OnFormClosed(e);
  }

  // --render: a sample fight (no game needed), sorted three ways
  void RenderSample(string file) {
    var sample = new List<NearbyMob> {
      new NearbyMob { Id = 1, Name = "a dune lizard", Level = 12, Health = 640, HealthMax = 1923, Distance = 4, OnYou = true, IsTarget = true },
      new NearbyMob { Id = 2, Name = "a dune lizard", Level = 12, Health = 1923, HealthMax = 1923, Distance = 6, OnYou = true },
      new NearbyMob { Id = 3, Name = "Tusken Raider", Level = 18, Health = 2100, HealthMax = 3400, Distance = 14, OnOther = "Bealdwine" },
      new NearbyMob { Id = 4, Name = "a vicious Rill", Level = 3, Health = 316, HealthMax = 316, Distance = 22 },
      new NearbyMob { Id = 5, Name = "a swarming lesser dewback", Level = 9, Health = 1180, HealthMax = 2100, Distance = 31 },
      new NearbyMob { Id = 6, Name = "a womp rat", Level = 2, Health = 90, HealthMax = 240, Distance = 44 },
    };
    connected = true;
    var shots = new List<Bitmap>();
    foreach (var so in new[] { 0, 1, 2 }) {
      sort = so;
      trail.Clear();
      Apply(sample, DateTime.Now);
      trail[1] = 0.55; // show a damage trail on the target
      var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
      using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
      shots.Add(bmp);
    }
    int gap = 12;
    using (var all = new Bitmap(shots.Sum(b => b.Width) + gap * 4, shots.Max(b => b.Height) + 2 * gap)) {
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
      using (var f = new NearbyMobs()) { f.timer.Stop(); f.tray.Visible = false; f.RenderSample(args[1]); }
      return;
    }
    Application.Run(new NearbyMobs());
  }
}
