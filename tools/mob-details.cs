// Mob details panel: everything the client knows about your current target (name, level, pools, posture,
// condition flags, faction, con text). Shares parts.cs's read-only process handle; see that file for the guardrails.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only (in parts.cs). Never add
// WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:MobDetails -out:../bin/swgr-mob.exe parts.cs ui.cs mob-details.cs
// usage: swgr-mob [--render out.png]
// Generic mob names come from the extracted string tables (GameData in ui.cs: bin/data/string/en, else ../beautified/string/en).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static partial class Parts {
  // CreatureObject fields (see notes/memory.md, "Creature details")
  const int CR_TARGET = 0xd18, CR_CUR_HAM = 0xa00, CR_MAX_HAM = 0xa70, CR_LEVEL = 0x1320;
  const int CR_POSTURE = 0x988, CR_CONDITION = 0x720, CR_FACTION = 0x780, CR_NAME_ID = 0x368;
  const int OBJ_TEMPLATE = 0x18, TEMPLATE_PATH = 0x20, POS_X = 0x84, POS_Y = 0x94, POS_Z = 0xa4;
  const uint FACTION_REBEL = 0x16148850, FACTION_IMPERIAL = 0xdb4acc54;

  internal sealed class Mob {
    public string Name, Template, NameId, Faction, Con;
    public Color? ConColor;
    public int Health, HealthMax, Action, ActionMax, Mind, MindMax, Level, Posture;
    public uint Condition;
    public float Distance = -1;
    public ulong Id;
    public bool IsPlayer;
  }

  static string CStr(ulong addr) {
    var b = Read(addr, 128);
    if (b == null) return null;
    int z = Array.IndexOf(b, (byte)0);
    return Encoding.ASCII.GetString(b, 0, z >= 0 ? z : b.Length);
  }

  // The player's current target, or null
  internal static Mob ReadTarget() {
    ulong player = Ptr(baseAddr + RVA_PLAYER);
    if (!IsA(player, VT_CREATURE)) return null;
    return ReadMob(player, Ptr(player + CR_TARGET));
  }

  // One CreatureObject (distance measured from player), or null
  internal static Mob ReadMob(ulong player, ulong t) {
    if (!IsA(t, VT_CREATURE)) return null;
    var cur = Read(Ptr(t + CR_CUR_HAM), 20);
    var max = Read(Ptr(t + CR_MAX_HAM), 20);
    var lvl = Read(t + CR_LEVEL, 2);
    var flags = Read(t + CR_CONDITION, 4);
    var pos = Read(t + CR_POSTURE, 1);
    var fac = Read(t + CR_FACTION, 4);
    var sid = Read(t + CR_NAME_ID, 0x40); // StringId: table std::string, key std::string
    if (cur == null || max == null) return null;
    var m = new Mob {
      Id = Ptr(t + OBJ_ID),
      Health = BitConverter.ToInt32(cur, 0), HealthMax = BitConverter.ToInt32(max, 0),
      Action = BitConverter.ToInt32(cur, 8), ActionMax = BitConverter.ToInt32(max, 8),
      Mind = BitConverter.ToInt32(cur, 16), MindMax = BitConverter.ToInt32(max, 16),
      Level = lvl == null ? 0 : BitConverter.ToInt16(lvl, 0),
      Condition = flags == null ? 0 : BitConverter.ToUInt32(flags, 0),
      Posture = pos == null ? -1 : pos[0],
      Template = CStr(Ptr(Ptr(t + OBJ_TEMPLATE) + TEMPLATE_PATH)),
      Distance = Distance(player, t),
    };
    m.IsPlayer = m.Template != null && m.Template.StartsWith("object/creature/player/");
    uint f = fac == null ? 0 : BitConverter.ToUInt32(fac, 0);
    m.Faction = f == FACTION_REBEL ? "Rebel" : f == FACTION_IMPERIAL ? "Imperial" : null;
    string table = sid == null ? null : StdStr(sid, 0, false), key = sid == null ? null : StdStr(sid, 0x20, false);
    if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(key)) m.NameId = table + ":" + key;
    var shown = ObjectName(t);
    m.Name = shown.Length > 0 ? shown : Names.Lookup(table, key) ?? "Target";

    // "consider" is cached once the mob has been examined, e.g. "\#808080***\#. a dune lizard is no match for you.\#."
    var attrs = ReadAttrs(m.Id);
    string con;
    if (attrs != null && attrs.TryGetValue("consider", out con)) {
      var c = Regex.Match(con, @"\\#([0-9a-fA-F]{6})");
      if (c.Success) m.ConColor = Color.FromArgb(int.Parse(c.Groups[1].Value, NumberStyles.HexNumber) | unchecked((int)0xff000000));
      m.Con = Regex.Replace(con, @"\\#([0-9a-fA-F]{6}|\.)", " ").Replace("  ", " ").Trim();
    }
    return m;
  }

  static float[] Pos(ulong obj) {
    var b = Read(obj + POS_X, POS_Z - POS_X + 4);
    return b == null ? null : new[] { BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, POS_Y - POS_X), BitConverter.ToSingle(b, POS_Z - POS_X) };
  }

  // Parent-relative positions: wrong when only one of the two is inside a building cell. -1 when unknown.
  static float Distance(ulong a, ulong b) {
    var p = Pos(a); var q = Pos(b);
    if (p == null || q == null) return -1;
    double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2];
    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
    return double.IsNaN(d) || d > 100000 ? -1 : (float)d;
  }
}

// StringId -> English text from the extracted string tables, one table loaded on first use
static class Names {
  static readonly Dictionary<string, Dictionary<string, string>> tables = new Dictionary<string, Dictionary<string, string>>();

  internal static string Lookup(string table, string key) {
    if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key) || table.Contains("..")) return null;
    Dictionary<string, string> t;
    if (!tables.TryGetValue(table, out t)) {
      tables[table] = t = new Dictionary<string, string>();
      try {
        foreach (var line in File.ReadLines(GameData.File("string", "en", table.Replace('/', Path.DirectorySeparatorChar) + ".txt"))) {
          int eq = line.IndexOf(" = ");
          if (eq > 0) t[line.Substring(0, eq)] = line.Substring(eq + 3);
        }
      } catch { }
    }
    string v;
    return t.TryGetValue(key, out v) ? v : key.Replace('_', ' ');
  }
}

sealed class MobDetails : LayeredForm {
  const int W = 280, Pad = 10, HeadH = 46, BarH = 26, RowH = 17;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Label = Color.FromArgb(233, 214, 90), Track = Color.FromArgb(38, 44, 52);
  static readonly Color HealthC = Color.FromArgb(226, 84, 84), ActionC = Color.FromArgb(232, 190, 70), MindC = Color.FromArgb(80, 150, 235);
  static readonly Color Hostile = Color.FromArgb(255, 92, 92), Calm = Color.FromArgb(80, 210, 200), Safe = Color.FromArgb(120, 130, 140);
  static readonly Color RebelC = Color.FromArgb(232, 110, 80), ImperialC = Color.FromArgb(110, 160, 235);

  // TangibleObject condition bits (SWG's TangibleObject::Conditions)
  const uint C_CONVERSABLE = 0x8, C_AGGRESSIVE = 0x40, C_INVULNERABLE = 0x100, C_INTERESTING = 0x800;

  static readonly string[] Postures = {
    "Upright", "Crouched", "Prone", "Sneaking", "Blocking", "Climbing", "Flying", "Lying down", "Sitting",
    "Skill animating", "Driving", "Riding", "Knocked down", "Incapacitated", "Dead",
  };

  readonly Timer timer = new Timer { Interval = 200 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-mob.pos");
  // Fonts and scale come from UiLook (right-click → Text size / Font); drawing happens in the 100% design space
  Font nameFont, smallFont, rowFont, tagFont;
  float scale = 1f;
  int designH;
  NotifyIcon tray;
  int reconnectTick;
  Point dragFrom;
  Parts.Mob mob;
  readonly bool hosted;

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  public MobDetails(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR mob details";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadPos();
    Restyle();
    ClientSize = new Size(W, HeadH);
    UiLook.Changed += OnLook;

    UseOpacity("mob");
    var menu = new ContextMenuStrip();
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR mob details", ContextMenuStrip = trayMenu, Visible = true };
    }

    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { nameFont, smallFont, rowFont, tagFont }) if (f != null) f.Dispose();
    nameFont = UiLook.PanelFont(UiLook.Strong, 10.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 8f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    tagFont = UiLook.PanelFont(UiLook.Regular, 7.5f);
    scale = UiLook.Scale;
    if (mob != null) Show(mob);
  }

  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Left + 40, wa.Top + 160);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top); } catch { } }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

  void Tick() {
    if (!Parts.Alive() && ++reconnectTick % 10 == 1) Parts.Attach(); // try every 2 s
    var m = Parts.Alive() ? Parts.ReadTarget() : null;
    if (tray != null) tray.Text = Parts.Alive() ? "SWGR mob details" : "SWGR mob details: waiting for game";
    if (m == null) { if (Visible) Visible = false; mob = null; return; }
    Show(m);
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  // The rows under the pools: label, value, value colour
  List<Tuple<string, string, Color>> Rows(Parts.Mob m) {
    var rows = new List<Tuple<string, string, Color>>();
    rows.Add(Tuple.Create("Posture", m.Posture >= 0 && m.Posture < Postures.Length ? Postures[m.Posture] : "?", m.Posture == 14 ? Hostile : TextColor));
    if (m.Distance >= 0) rows.Add(Tuple.Create("Distance", string.Format("{0:0.0} m", m.Distance), TextColor));
    if (m.Faction != null) rows.Add(Tuple.Create("Faction", m.Faction, m.Faction == "Rebel" ? RebelC : ImperialC));
    if (m.Con != null) rows.Add(Tuple.Create("Con", m.Con, m.ConColor ?? TextColor));
    return rows;
  }

  // Short coloured tags from the condition bits
  List<Tuple<string, Color>> Tags(Parts.Mob m) {
    var tags = new List<Tuple<string, Color>>();
    if (m.IsPlayer) tags.Add(Tuple.Create("Player", Calm));
    if ((m.Condition & C_AGGRESSIVE) != 0) tags.Add(Tuple.Create("Aggressive", Hostile));
    if ((m.Condition & C_INVULNERABLE) != 0) tags.Add(Tuple.Create("Invulnerable", Safe));
    else if (!m.IsPlayer) tags.Add(Tuple.Create("Attackable", Calm));
    if ((m.Condition & C_CONVERSABLE) != 0) tags.Add(Tuple.Create("Talks", Safe));
    if ((m.Condition & C_INTERESTING) != 0) tags.Add(Tuple.Create("Quest", Label));
    return tags;
  }

  void Show(Parts.Mob m) {
    mob = m;
    int rows = 0;
    foreach (var r in Rows(m)) rows += Lines(r);
    designH = HeadH + 3 * BarH + 22 + rows * RowH + Pad;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  // Long values (the con text) wrap onto a second line
  int Lines(Tuple<string, string, Color> row) {
    using (var g = CreateGraphics()) return g.MeasureString(row.Item2, rowFont).Width > W - 2 * Pad - 70 ? 2 : 1;
  }

  static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
  }

  static string Short(string template) {
    if (string.IsNullOrEmpty(template)) return "";
    var s = Path.GetFileNameWithoutExtension(template);
    return s.StartsWith("shared_") ? s.Substring(7) : s;
  }

  protected override void OnPaint(PaintEventArgs e) {
    if (mob == null) return;
    var g = e.Graphics;
    var m = mob;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using (var p = new Pen(Edge)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);
    var accent = m.IsPlayer ? Calm : (m.Condition & C_AGGRESSIVE) != 0 ? Hostile : (m.Condition & C_INVULNERABLE) != 0 ? Safe : Calm;
    using (var b = new SolidBrush(accent)) g.FillRectangle(b, 0, 0, 4, designH);

    // Name and level, then the template name
    DrawText(g, m.Name, nameFont, new RectangleF(Pad, 3, W - 2 * Pad - 50, 22), TextColor, StringAlignment.Near);
    DrawText(g, m.Level > 0 ? "Lvl " + m.Level : "", rowFont, new RectangleF(W - Pad - 50, 3, 50, 22), Label, StringAlignment.Far);
    DrawText(g, Short(m.Template), smallFont, new RectangleF(Pad, 23, W - 2 * Pad, 16), Muted, StringAlignment.Near);

    int y = HeadH;
    Bar(g, y, "Health", m.Health, m.HealthMax, HealthC); y += BarH;
    Bar(g, y, "Action", m.Action, m.ActionMax, ActionC); y += BarH;
    Bar(g, y, "Mind", m.Mind, m.MindMax, MindC); y += BarH;

    // Tags in one line
    float x = Pad;
    foreach (var t in Tags(m)) {
      float w = g.MeasureString(t.Item1, tagFont).Width + 6;
      var r = new RectangleF(x, y + 2, w, 15);
      using (var p = new Pen(Color.FromArgb(150, t.Item2))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
      DrawText(g, t.Item1, tagFont, r, t.Item2, StringAlignment.Center);
      x += w + 5;
    }
    y += 22;

    foreach (var row in Rows(m)) {
      DrawText(g, row.Item1, rowFont, new RectangleF(Pad, y, 70, RowH), Label, StringAlignment.Near);
      int n = Lines(row);
      if (n == 1) DrawText(g, row.Item2, rowFont, new RectangleF(Pad + 70, y, W - 2 * Pad - 70, RowH), row.Item3, StringAlignment.Near);
      else using (var b = new SolidBrush(row.Item3)) g.DrawString(row.Item2, rowFont, b, new RectangleF(Pad + 70, y + 1, W - 2 * Pad - 70, n * RowH));
      y += n * RowH;
    }
  }

  void Bar(Graphics g, int y, string label, int cur, int max, Color c) {
    DrawText(g, label, rowFont, new RectangleF(Pad, y, 80, 15), Label, StringAlignment.Near);
    DrawText(g, cur.ToString("#,0", CultureInfo.InvariantCulture) + " / " + max.ToString("#,0", CultureInfo.InvariantCulture), rowFont,
      new RectangleF(Pad + 80, y, W - 2 * Pad - 80, 15), TextColor, StringAlignment.Far);
    double frac = max > 0 ? Math.Max(0, Math.Min(1, (double)cur / max)) : 0;
    using (var b = new SolidBrush(Track)) g.FillRectangle(b, Pad, y + 16, W - 2 * Pad, 5);
    using (var b = new SolidBrush(c)) g.FillRectangle(b, Pad, y + 16, (float)((W - 2 * Pad) * frac), 5);
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

  // Sample panels for --render (fontsOnly: the first sample in every font at 100% and 150%)
  void RenderAll(string file, bool fontsOnly = false) {
    var samples = new[] {
      new Parts.Mob { Name = "a dune lizard", Template = "object/mobile/shared_dune_lizard_hue.iff", Level = 15, Health = 1210, HealthMax = 1923,
        Action = 1000, ActionMax = 1000, Mind = 1000, MindMax = 1000, Condition = 0xc0, Distance = 12.4f,
        Con = "*** a dune lizard is no match for you.", ConColor = Color.FromArgb(128, 128, 128) },
      new Parts.Mob { Name = "Jabba the Hutt", Template = "object/mobile/shared_jabba_the_hutt.iff", Level = 100, Health = 14646, HealthMax = 14646,
        Action = 1000, ActionMax = 1000, Mind = 1000, MindMax = 1000, Condition = 0x918, Distance = 6.1f },
      new Parts.Mob { Name = "a dune lizard", Template = "object/mobile/shared_dune_lizard_hue.iff", Level = 15, Health = 0, HealthMax = 1829,
        Action = 0, ActionMax = 1000, Mind = 0, MindMax = 1000, Condition = 0xc0, Posture = 14, Distance = 3.2f },
      new Parts.Mob { Name = "Bealdwine Rose", Template = "object/creature/player/shared_human_female.iff", Level = 90, Health = 21500, HealthMax = 24000,
        Action = 9000, ActionMax = 12000, Mind = 8000, MindMax = 8000, Condition = 0x80, Faction = "Rebel", IsPlayer = true, Distance = 22f },
    };
    // One sheet row per look: the current look for --render, every font at 100% and 150% for --render-fonts
    var rows = new List<List<Bitmap>>();
    var looks = new List<Tuple<float, int>>();
    if (fontsOnly) { foreach (var sc in new[] { 1f, 1.5f }) for (int i = 0; i < UiLook.Fonts.Length; i++) looks.Add(Tuple.Create(sc, i)); }
    else looks.Add(Tuple.Create(UiLook.Scale, UiLook.FontIndex));
    float keepScale = UiLook.Scale; int keepFont = UiLook.FontIndex;
    foreach (var look in looks) {
      if (rows.Count == 0 || !fontsOnly || look.Item2 == 0) rows.Add(new List<Bitmap>());
      UiLook.Set(look.Item1, look.Item2, false);
      foreach (var s in fontsOnly ? new[] { samples[0] } : samples) {
        Show(s);
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
        rows[rows.Count - 1].Add(bmp);
      }
    }
    UiLook.Set(keepScale, keepFont, false);
    const int gap = 12;
    int sheetW = rows.Max(r => r.Sum(b => b.Width + gap)) + gap, sheetH = rows.Sum(r => r.Max(b => b.Height) + gap) + gap;
    using (var sheet = new Bitmap(sheetW, sheetH))
    using (var g = Graphics.FromImage(sheet)) {
      g.Clear(Color.FromArgb(70, 60, 45)); // desert-ish backdrop
      int y = gap;
      foreach (var r in rows) {
        int x = gap;
        foreach (var b in r) { g.DrawImage(b, x, y); x += b.Width + gap; }
        y += r.Max(b => b.Height) + gap;
        foreach (var b in r) b.Dispose();
      }
      sheet.Save(file);
    }
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && (args[0] == "--render" || args[0] == "--render-fonts")) {
      using (var f = new MobDetails()) { f.timer.Stop(); f.tray.Visible = false; f.RenderAll(args[1], args[0] == "--render-fonts"); }
      return;
    }
    Application.Run(new MobDetails());
  }
}
