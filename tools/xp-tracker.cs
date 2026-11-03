// XP tracker: your experience per xp type from the client's PlayerObject, with what you gained this session and the
// rate per hour. Shares parts.cs's read-only process handle; see that file for the guardrails.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only (in parts.cs). Never add
// WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:XpTracker -out:../bin/swgr-xp.exe parts.cs ui.cs mob-details.cs xp-tracker.cs
// usage: swgr-xp [--render out.png | --print]   (--print writes the current totals to swgr-xp.txt next to the exe)
// Xp type names come from the extracted string table exp_n (GameData in ui.cs: bin/data/string/en, else ../beautified/string/en).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static partial class Parts {
  // PlayerObject (the player's "ghost", a child in the creature's slotted container) holds the experience map:
  // AutoDeltaMap<std::string, int, PlayerObject> with an MSVC std::map {head, size} at +0x18. See notes/memory.md.
  const ulong VT_PLAYER_OBJECT = 0x1d0c838, VT_XP_MAP = 0x1d0d538;
  const int PO_XP = 0x690;
  static int xpOffset = PO_XP;

  // The player's PlayerObject, or 0
  static ulong PlayerObject() {
    ulong player = Ptr(baseAddr + RVA_PLAYER);
    if (!IsA(player, VT_CREATURE)) return 0;
    ulong cont = Ptr(player + OBJ_CONTAINER);
    if (!IsA(cont, VT_SLOTTED)) return 0;
    ulong beg = Ptr(cont + CONT_BEGIN), end = Ptr(cont + CONT_END);
    if (end <= beg || (end - beg) % CONT_ENTRY != 0 || end - beg > CONT_ENTRY * 256) return 0;
    var b = Read(beg, (int)(end - beg));
    if (b == null) return 0;
    for (int i = 0; i < b.Length; i += CONT_ENTRY) {
      ulong child = BitConverter.ToUInt64(b, i + ENTRY_OBJ);
      if (IsA(child, VT_PLAYER_OBJECT)) return child;
    }
    return 0;
  }

  // The player's skills: CreatureObject AutoDeltaSet<const SkillObject*> (vtable below), an MSVC std::set {head, size}
  // at +0x18 whose node keys (+0x20) point to SkillObjects; each holds its name as a std::string.
  const ulong VT_SKILL_SET = 0x1cf8b08, VT_SKILL_OBJECT = 0x1d3cf18;
  static int skillOffset = -1, skillNameOffset = 0xb0; // name std::string at +0xb0 (2026-10-30 build)

  internal static List<string> ReadSkills() {
    ulong player = Ptr(baseAddr + RVA_PLAYER);
    if (!IsA(player, VT_CREATURE)) return null;
    if (skillOffset < 0 || !IsA(player + (ulong)skillOffset, VT_SKILL_SET)) {
      var cb = Read(player, 0x2000);
      if (cb == null) return null;
      skillOffset = -1;
      for (int o = 0; o + 8 <= cb.Length; o += 8) if (BitConverter.ToUInt64(cb, o) == baseAddr + VT_SKILL_SET) { skillOffset = o; break; }
      if (skillOffset < 0) return null;
    }
    ulong set = player + (ulong)skillOffset + 0x18;
    ulong head = Ptr(set);
    long size = (long)Ptr(set + 8);
    var skills = new List<string>();
    if (head == 0 || size <= 0 || size > 1000) return skills;
    var stack = new Stack<ulong>();
    ulong node = Ptr(head + 8);
    int guard = 0;
    while ((stack.Count > 0 || !IsNil(node)) && guard++ < 4 * size + 16) {
      if (!IsNil(node)) { stack.Push(node); node = Ptr(node); continue; }
      node = stack.Pop();
      var name = SkillName(Ptr(node + 0x20));
      if (name != null) skills.Add(name);
      node = Ptr(node + 0x10);
    }
    return skills;
  }

  // The SkillObject's name: the first std::string in it that reads as a skill id (offset found once)
  static string SkillName(ulong skill) {
    var b = Read(skill, 0x100);
    if (b == null) return null;
    if (skillNameOffset >= 0) {
      var n = StdStr(b, skillNameOffset, false);
      if (n != null && System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z][a-z0-9_]+$")) return n;
    }
    for (int o = 8; o + 0x20 <= b.Length; o += 8) {
      string s;
      try { s = StdStr(b, o, false); } catch { continue; }
      if (s != null && s.Length > 3 && System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z][a-z0-9_]+$")) { skillNameOffset = o; return s; }
    }
    return null;
  }

  internal static int SkillNameOffset { get { return skillNameOffset; } }
  internal static int SkillSetOffset { get { return skillOffset; } }

  // xp type -> points, or null when the game isn't readable. The map is found by its vtable (PO_XP, else a scan,
  // since a patch may shift it); of two maps of this type the experience one is the non-empty one.
  internal static Dictionary<string, int> ReadXp() {
    ulong po = PlayerObject();
    if (po == 0) return null;
    if (!IsA(po + (ulong)xpOffset, VT_XP_MAP) || (long)Ptr(po + (ulong)xpOffset + 0x20) <= 0) {
      var ob = Read(po, 0x1000);
      if (ob == null) return null;
      xpOffset = -1;
      for (int o = 0; o + 0x28 <= ob.Length; o += 8)
        if (BitConverter.ToUInt64(ob, o) == baseAddr + VT_XP_MAP && BitConverter.ToInt64(ob, o + 0x20) > 0) { xpOffset = o; break; }
      if (xpOffset < 0) { xpOffset = PO_XP; return new Dictionary<string, int>(); }
    }
    ulong map = po + (ulong)xpOffset + 0x18;
    ulong head = Ptr(map);
    long size = (long)Ptr(map + 8);
    var xp = new Dictionary<string, int>();
    if (head == 0 || size <= 0 || size > 500) return xp;
    var stack = new Stack<ulong>();
    ulong node = Ptr(head + 8);
    int guard = 0;
    while ((stack.Count > 0 || !IsNil(node)) && guard++ < 4 * size + 16) {
      if (!IsNil(node)) { stack.Push(node); node = Ptr(node); continue; }
      node = stack.Pop();
      var nb = Read(node + 0x20, 0x24); // key std::string, then the int value at +0x40
      if (nb != null) {
        var k = StdStr(nb, 0, false);
        if (!string.IsNullOrEmpty(k)) xp[k] = BitConverter.ToInt32(nb, 0x20);
      }
      node = Ptr(node + 0x10);
    }
    return xp;
  }
}

// The skill datatable (datatables/skill/skills.tsv via GameData): what each skill costs in which xp type and
// what it requires, to find the next box you can buy with each xp type
static class SkillBook {
  sealed class Skill { public string Name, Parent, XpType; public int Cost; public string[] Requires; public bool GodOnly; }
  static List<Skill> skills;

  static void Load() {
    skills = new List<Skill>();
    try {
      var file = GameData.File("datatables", "skill", "skills.tsv");
      var lines = File.ReadAllLines(file);
      var head = lines[0].Split('\t').ToList();
      int cName = head.IndexOf("NAME"), cParent = head.IndexOf("PARENT"), cGod = head.IndexOf("GOD_ONLY"), cReq = head.IndexOf("SKILLS_REQUIRED"),
        cType = head.IndexOf("XP_TYPE"), cCost = head.IndexOf("XP_COST");
      foreach (var line in lines.Skip(2)) {
        var f = line.Split('\t');
        if (f.Length <= cCost || f[cName].Length == 0) continue;
        int cost;
        int.TryParse(f[cCost], out cost);
        if (cost <= 0 || f[cType].Length == 0) continue;
        skills.Add(new Skill { Name = f[cName], Parent = f[cParent], XpType = f[cType], Cost = cost, GodOnly = f[cGod] == "1",
          Requires = f[cReq].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) });
      }
    } catch { }
  }

  // The cheapest skill(s) you could learn next with this xp type: not owned, every required skill owned (or, with none
  // listed, the parent box owned). Returns the cost and the display names, or null when nothing is left.
  internal static Tuple<int, List<string>> Next(string xpType, HashSet<string> owned) {
    if (skills == null) Load();
    var open = skills.Where(k => k.XpType == xpType && !k.GodOnly && !owned.Contains(k.Name) &&
      (k.Requires.Length > 0 ? k.Requires.All(owned.Contains) : owned.Contains(k.Parent))).ToList();
    if (open.Count == 0) return null;
    int cost = open.Min(k => k.Cost);
    return Tuple.Create(cost, open.Where(k => k.Cost == cost).Select(k => Names.Lookup("skl_n", k.Name)).OrderBy(n => n).ToList());
  }
}

sealed class XpTracker : LayeredForm {
  const int W = 320, Pad = 10, HeadH = 30, RowH = 18, RowH2 = 40, ColGain = 70, ColRate = 70;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Label = Color.FromArgb(233, 214, 90), Gain = Color.FromArgb(110, 220, 130);
  static readonly Color Accent = Color.FromArgb(80, 210, 200), Track = Color.FromArgb(38, 44, 52), Ready = Color.FromArgb(110, 220, 130);

  readonly Timer timer = new Timer { Interval = 1000 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-xp.pos");
  Font nameFont, rowFont, smallFont;
  float scale = 1f;
  int designH;
  NotifyIcon tray;
  int reconnectTick;
  Point dragFrom;
  readonly bool hosted;
  bool showAll;

  // Session: xp earned per type (the sum of every increase, so spending xp on a skill box doesn't count against it),
  // the latest totals, and when each type last grew
  readonly Dictionary<string, long> earned = new Dictionary<string, long>();
  Dictionary<string, int> now;
  HashSet<string> owned = new HashSet<string>();
  readonly Dictionary<string, DateTime> lastGain = new Dictionary<string, DateTime>();
  DateTime since = DateTime.Now;

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  public XpTracker(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR XP tracker";
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

    UseOpacity("xp");
    var menu = new ContextMenuStrip();
    menu.Items.Add("Reset session", null, (s, e) => Reset());
    var all = new ToolStripMenuItem("Show all xp types") { CheckOnClick = true };
    all.CheckedChanged += (s, e) => { showAll = all.Checked; Relayout(); };
    menu.Opening += (s, e) => all.Checked = showAll;
    menu.Items.Add(all);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR XP tracker", ContextMenuStrip = trayMenu, Visible = true };
    }

    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { nameFont, rowFont, smallFont }) if (f != null) f.Dispose();
    nameFont = UiLook.PanelFont(UiLook.Strong, 10f);
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
    return new Point(wa.Left + 40, wa.Top + 480);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top); } catch { } }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

  // For the swgr-tools main window
  public bool ShowAll { get { return showAll; } set { showAll = value; Relayout(); } }
  public void ResetSession() { Reset(); }

  void Reset() { earned.Clear(); lastGain.Clear(); since = DateTime.Now; Relayout(); }

  void Tick() {
    if (!Parts.Alive() && ++reconnectTick % 2 == 1) Parts.Attach(); // try every 2 s
    var xp = Parts.Alive() ? Parts.ReadXp() : null;
    if (tray != null) tray.Text = Parts.Alive() ? "SWGR XP tracker" : "SWGR XP tracker: waiting for game";
    // Nothing readable (game closed, loading screen, character select): hide but keep the session
    if (xp == null || xp.Count == 0) { if (Visible) Visible = false; return; }
    var sk = Parts.ReadSkills();
    if (sk != null && sk.Count > 0) owned = new HashSet<string>(sk);
    Take(xp);
    if (!Visible) Visible = true;
    KeepOnTop();
  }

  void Take(Dictionary<string, int> xp) {
    if (now != null) foreach (var kv in xp) {
      int was;
      if (!now.TryGetValue(kv.Key, out was)) was = 0; // a new xp type: everything in it was just earned
      if (kv.Value > was) { earned[kv.Key] = Gained(kv.Key) + (kv.Value - was); lastGain[kv.Key] = DateTime.Now; }
    }
    now = xp;
    Relayout();
  }

  // The rows: types gained this session (most recent first), then types with a skill box still to buy, then, with
  // "Show all", the rest by total
  List<string> Rows() {
    if (now == null) return new List<string>();
    var rows = now.Keys.Where(k => Gained(k) > 0).OrderByDescending(k => lastGain.ContainsKey(k) ? lastGain[k] : DateTime.MinValue).ThenByDescending(Gained).ToList();
    rows.AddRange(now.Keys.Where(k => Gained(k) <= 0 && Next(k) != null).OrderByDescending(k => Progress(k)));
    if (showAll) rows.AddRange(now.Keys.Where(k => !rows.Contains(k)).OrderByDescending(k => now[k]));
    return rows;
  }

  Tuple<int, List<string>> Next(string k) { return SkillBook.Next(k, owned); }

  double Progress(string k) { var n = Next(k); return n == null ? 0 : Math.Min(1, (double)now[k] / n.Item1); }

  long Gained(string k) { long v; return earned.TryGetValue(k, out v) ? v : 0; }

  void Relayout() {
    var rows = Rows();
    designH = HeadH + 16 + (rows.Count == 0 ? RowH : rows.Sum(k => Next(k) != null ? RowH2 : RowH)) + Pad;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  static string XpName(string type) {
    var s = Names.Lookup("exp_n", type);
    return string.IsNullOrEmpty(s) ? type : char.ToUpper(s[0]) + s.Substring(1);
  }

  static string Num(double v) {
    if (v >= 1e6) return (v / 1e6).ToString(v >= 1e7 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "M";
    if (v >= 1e4) return (v / 1e3).ToString(v >= 1e5 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k";
    return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture);
  }

  // Time to go, e.g. "7h 40m", "25m", "3d 4h"
  static string Eta(double hours) {
    if (double.IsInfinity(hours) || double.IsNaN(hours) || hours > 24 * 99) return "";
    var t = TimeSpan.FromHours(hours);
    return t.TotalDays >= 1 ? string.Format("{0}d {1}h", (int)t.TotalDays, t.Hours) : t.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes) : string.Format("{0}m", Math.Max(1, t.Minutes));
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
    DrawText(g, "Experience", nameFont, new RectangleF(Pad, 4, W - 2 * Pad - 90, 22), TextColor, StringAlignment.Near);
    DrawText(g, string.Format("session {0}:{1:00}", (int)span.TotalHours, span.Minutes), smallFont, new RectangleF(W - Pad - 90, 4, 90, 22), Muted, StringAlignment.Far);

    int y = HeadH;
    float nameW = W - 2 * Pad - ColGain - ColRate;
    DrawText(g, "Type", smallFont, new RectangleF(Pad, y, nameW, 14), Muted, StringAlignment.Near);
    DrawText(g, "Gained", smallFont, new RectangleF(Pad + nameW, y, ColGain, 14), Muted, StringAlignment.Far);
    DrawText(g, "Per hour", smallFont, new RectangleF(Pad + nameW + ColGain, y, ColRate, 14), Muted, StringAlignment.Far);
    y += 16;

    var rows = Rows();
    if (rows.Count == 0) {
      DrawText(g, "No xp gained yet this session", rowFont, new RectangleF(Pad, y, W - 2 * Pad, RowH), Muted, StringAlignment.Near);
      return;
    }
    double hours = Math.Max(span.TotalHours, 1 / 60.0); // no wild rates in the first minute
    foreach (var k in rows) {
      long gain = Gained(k);
      var next = Next(k);
      DrawText(g, XpName(k), rowFont, new RectangleF(Pad, y, nameW, RowH), gain > 0 ? Label : TextColor, StringAlignment.Near);
      DrawText(g, gain > 0 ? "+" + Num(gain) : next == null ? Num(now[k]) : "", rowFont, new RectangleF(Pad + nameW, y, ColGain, RowH), gain > 0 ? Gain : Muted, StringAlignment.Far);
      DrawText(g, gain > 0 ? Num(gain / hours) : "", rowFont, new RectangleF(Pad + nameW + ColGain, y, ColRate, RowH), TextColor, StringAlignment.Far);
      if (next == null) { y += RowH; continue; }

      // Current xp out of what the next box costs (as in the game's skill tooltip), the box, and time to go at this session's rate
      int cur = now[k], cost = next.Item1;
      bool ready = cur >= cost;
      string box = next.Item2[0] + (next.Item2.Count > 1 ? " +" + (next.Item2.Count - 1) : "");
      string left = ready ? "ready" : gain > 0 ? Eta((cost - cur) / (gain / hours)) : "";
      DrawText(g, cur.ToString("#,0", CultureInfo.InvariantCulture) + " / " + cost.ToString("#,0", CultureInfo.InvariantCulture) + "   " + box, smallFont,
        new RectangleF(Pad, y + RowH - 2, W - 2 * Pad - 60, 15), Muted, StringAlignment.Near);
      DrawText(g, left, smallFont, new RectangleF(W - Pad - 60, y + RowH - 2, 60, 15), ready ? Ready : Muted, StringAlignment.Far);
      float frac = (float)Math.Min(1, (double)cur / cost);
      using (var b = new SolidBrush(Track)) g.FillRectangle(b, Pad, y + RowH + 15, W - 2 * Pad, 4);
      using (var b = new SolidBrush(ready ? Ready : Accent)) g.FillRectangle(b, Pad, y + RowH + 15, (W - 2 * Pad) * frac, 4);
      y += RowH2;
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

  // A sample session for --render: 40 minutes of space combat
  void RenderSample(string file) {
    since = DateTime.Now.AddMinutes(-40);
    earned["space_combat_general"] = 87590; earned["prestige_piracy"] = 600;
    owned = new HashSet<string> { "pilot_neutral_novice", "pilot_neutral_starships_01", "pilot_neutral_weapons_01", "pilot_neutral_procedures_01", "pilot_neutral_droid_01",
      "pilot_neutral_starships_02", "pilot_neutral_weapons_02", "pilot_neutral_procedures_02", "pilot_neutral_droid_02",
      "pilot_neutral_starships_03", "pilot_neutral_weapons_03", "pilot_neutral_procedures_03", "pilot_neutral_droid_03", "pilot_neutral_starships_04", "pilot_neutral_weapons_04",
      "prestige_piracy_novice" };
    now = new Dictionary<string, int> { { "space_combat_general", 124498 }, { "combat_general", 300000 }, { "prestige_piracy", 1000 }, { "scout", 432 } };
    lastGain["space_combat_general"] = DateTime.Now;
    lastGain["prestige_piracy"] = DateTime.Now.AddMinutes(-5);
    var shots = new List<Bitmap>();
    foreach (var all in new[] { false, true }) {
      showAll = all;
      Relayout();
      var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
      using (var g = Graphics.FromImage(bmp)) { g.Clear(Back); OnPaint(new PaintEventArgs(g, ClientRectangle)); }
      shots.Add(bmp);
    }
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
      using (var f = new XpTracker()) { f.timer.Stop(); f.tray.Visible = false; f.RenderSample(args[1]); }
      return;
    }
    if (args.Length == 1 && args[0] == "--print") {
      var xp = Parts.Attach() ? Parts.ReadXp() : null;
      var sk = xp == null ? null : Parts.ReadSkills();
      var own = new HashSet<string>(sk ?? new List<string>());
      File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-xp.txt"),
        xp == null ? new[] { "no PlayerObject found (game not running or not logged in)" } : xp.Select(kv => {
          var n = SkillBook.Next(kv.Key, own);
          return kv.Key + "\t" + kv.Value + "\t" + XpName(kv.Key) + (n == null ? "" : "\tnext " + n.Item1 + " " + string.Join(", ", n.Item2));
        }));
      File.AppendAllLines(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-xp.txt"),
        new[] { "skills (set +0x" + Parts.SkillSetOffset.ToString("x") + ", name +0x" + Parts.SkillNameOffset.ToString("x") + "): " + (sk == null ? "none" : string.Join(" ", sk)) });
      Parts.Detach();
      return;
    }
    Application.Run(new XpTracker());
  }
}
