// Floating combat text: our own replacement for the game's flytext, in three movable zones (damage you deal, damage
// you take, heals). Each number floats and fades; crits pop bigger, DoT ticks are small and coloured by damage type,
// glances are dimmed. Click-through; "Unlock to move" shows the zones with a dashed frame so they can be dragged.
// Source: the game's chat log only (CombatTracker in dps-meter.cs, its own tail of the file), no client memory.
// Needs combat messages on (Options > Chat > Combat Message Filter: Self or Group). Turn the game's own numbers off
// with Options > Combat > Show Damage Numbers over Others / over Self.
//
// build (standalone): cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:FlyText -out:../bin/swgr-flytext.exe ui.cs dps-meter.cs flytext.cs
// usage: swgr-flytext [--render out.png [background.png]]   (no args: live, with the zones unlocked for a first look)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

sealed class FlyText {
  public static readonly string[] Styles = { "Clean", "Outline", "Glow" };
  public static readonly string[] Motions = { "Scroll up", "Scroll down", "Fountain" };
  public static readonly string[] Weights = { "Regular", "Medium", "SemiBold" };
  public static readonly string[] SizeNames = { "Small", "Normal", "Large", "Huge" };
  static readonly float[] SizeFactors = { 0.8f, 1f, 1.25f, 1.6f };
  public static readonly string[] ZoneNames = { "Damage you deal", "Damage you take", "Heals" };
  public const int OUT = 0, IN = 1, HEAL = 2;

  internal readonly FlyZone[] Zones = new FlyZone[3];
  internal readonly CombatTracker Tracker = new CombatTracker();
  readonly Timer timer = new Timer { Interval = 30 };
  readonly string cfgFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-flytext.cfg");
  readonly Dictionary<string, DateTime> attackers = new Dictionary<string, DateTime>(); // who hit you lately: their misses were aimed at you
  readonly List<Tuple<DateTime, Action>> demo = new List<Tuple<DateTime, Action>>();
  readonly bool hosted;
  DateTime lastPoll;
  internal string MeOverride; // --render: a fake character name
  internal DateTime? Frozen;
  internal DateTime Now { get { return Frozen ?? DateTime.Now; } }

  int style, motion, weight = 1, size = 1;
  bool names, merge = true, misses = true, unlocked;
  readonly bool[] zoneOn = { true, true, true };

  public FlyText(bool hosted = false) {
    this.hosted = hosted;
    Load();
    for (int i = 0; i < 3; i++) Zones[i] = new FlyZone(this, i);
    Load(); // positions, now that the zones exist
    Tracker.Event += Take;
    timer.Tick += (s, e) => Step();
    UiLook.Changed += sizePicked => { foreach (var z in Zones) z.Restyle(); };
  }

  // ---- settings (every one is also on the tools window's Overlays page) ----

  public bool Active {
    get { return timer.Enabled; }
    set {
      timer.Enabled = value;
      if (value) { Tracker.Poll(); return; } // starts at the end of the log, so old fights aren't replayed
      foreach (var z in Zones) { z.Clear(); z.Visible = false; }
    }
  }
  public int Style { get { return style; } set { style = value; Save(); Restyle(); } }
  public int Motion { get { return motion; } set { motion = value; Save(); foreach (var z in Zones) z.Clear(); } }
  public int Weight { get { return weight; } set { weight = value; Save(); Restyle(); } }
  public int Size { get { return size; } set { size = value; Save(); Restyle(); } }
  public bool ShowNames { get { return names; } set { names = value; Save(); } }
  public bool MergeTicks { get { return merge; } set { merge = value; Save(); } }
  public bool ShowMisses { get { return misses; } set { misses = value; Save(); } }
  public bool ZoneOn(int z) { return zoneOn[z]; }
  public void SetZoneOn(int z, bool on) { zoneOn[z] = on; Save(); if (!on) { Zones[z].Clear(); Zones[z].Visible = false; } else if (unlocked) Zones[z].ShowUnlocked(true); }
  public bool Unlocked {
    get { return unlocked; }
    set { unlocked = value; for (int i = 0; i < 3; i++) Zones[i].ShowUnlocked(value && zoneOn[i]); if (!value) Save(); }
  }
  internal float Factor { get { return SizeFactors[Math.Max(0, Math.Min(SizeFactors.Length - 1, size))]; } }
  internal string Family {
    get {
      if (weight == 0) return UiLook.Regular;
      if (weight == 1 && UiLook.Regular == "Encode Sans") return "Encode Sans Medium";
      return UiLook.Strong;
    }
  }
  void Restyle() { foreach (var z in Zones) z.Restyle(); }

  // swgr-flytext.cfg: key=value lines; zones as "zoneN=x,y"
  void Load() {
    try {
      foreach (var line in File.ReadAllLines(cfgFile)) {
        int i = line.IndexOf('=');
        if (i < 0) continue;
        string k = line.Substring(0, i), v = line.Substring(i + 1);
        if (k == "style") style = int.Parse(v);
        else if (k == "motion") motion = int.Parse(v);
        else if (k == "weight") weight = int.Parse(v);
        else if (k == "size") size = int.Parse(v);
        else if (k == "names") names = v == "1";
        else if (k == "merge") merge = v == "1";
        else if (k == "misses") misses = v == "1";
        else if (k.StartsWith("on") && k.Length == 3) zoneOn[k[2] - '0'] = v == "1";
        else if (k.StartsWith("zone") && k.Length == 5 && Zones[k[4] - '0'] != null) {
          var p = v.Split(',');
          var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
          if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(pt))) Zones[k[4] - '0'].Location = pt;
        }
      }
    } catch { }
  }

  internal void Save() {
    if (Frozen != null || Zones[2] == null) return; // --render never touches the saved file
    var l = new List<string> { "style=" + style, "motion=" + motion, "weight=" + weight, "size=" + size, "names=" + (names ? 1 : 0), "merge=" + (merge ? 1 : 0), "misses=" + (misses ? 1 : 0) };
    for (int i = 0; i < 3; i++) { l.Add("on" + i + "=" + (zoneOn[i] ? 1 : 0)); l.Add("zone" + i + "=" + Zones[i].Left + "," + Zones[i].Top); }
    try { File.WriteAllLines(cfgFile, l); } catch { }
  }

  // The settings as a right-click menu (on an unlocked zone, and in the standalone tray); checkmarks re-sync on open
  internal ContextMenuStrip Menu() {
    var m = new ContextMenuStrip();
    Func<string, string[], Func<int>, Action<int>, ToolStripMenuItem> pick = (title, items, get, set) => {
      var sub = new ToolStripMenuItem(title);
      for (int i = 0; i < items.Length; i++) { int v = i; sub.DropDownItems.Add(new ToolStripMenuItem(items[i], null, (s, e) => set(v))); }
      sub.DropDownOpening += (s, e) => { for (int i = 0; i < items.Length; i++) ((ToolStripMenuItem)sub.DropDownItems[i]).Checked = get() == i; };
      return sub;
    };
    m.Items.Add(pick("Style", Styles, () => style, v => Style = v));
    m.Items.Add(pick("Motion", Motions, () => motion, v => Motion = v));
    m.Items.Add(pick("Weight", Weights, () => weight, v => Weight = v));
    m.Items.Add(pick("Size", SizeNames, () => size, v => Size = v));
    var zones = new ToolStripMenuItem("Zones");
    for (int i = 0; i < 3; i++) { int z = i; zones.DropDownItems.Add(new ToolStripMenuItem(ZoneNames[i], null, (s, e) => SetZoneOn(z, !zoneOn[z]))); }
    zones.DropDownOpening += (s, e) => { for (int i = 0; i < 3; i++) ((ToolStripMenuItem)zones.DropDownItems[i]).Checked = zoneOn[i]; };
    m.Items.Add(zones);
    var nm = new ToolStripMenuItem("Show names", null, (s, e) => ShowNames = !names);
    var mg = new ToolStripMenuItem("Merge DoT ticks", null, (s, e) => MergeTicks = !merge);
    var ms = new ToolStripMenuItem("Show misses", null, (s, e) => ShowMisses = !misses);
    var un = new ToolStripMenuItem("Unlock to move", null, (s, e) => Unlocked = !unlocked);
    m.Items.AddRange(new ToolStripItem[] { nm, mg, ms, un });
    m.Items.Add("Test", null, (s, e) => Test());
    m.Opening += (s, e) => { nm.Checked = names; mg.Checked = merge; ms.Checked = misses; un.Checked = unlocked; };
    m.Items.Add(new ToolStripSeparator());
    m.Items.AddRange(UiLook.MenuItems());
    m.Items.Add(new ToolStripSeparator());
    m.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Application.Exit(); });
    return m;
  }

  // ---- events → numbers ----

  internal static readonly Color OutColor = Color.FromArgb(240, 242, 245), CritColor = Color.FromArgb(255, 206, 72), GlanceColor = Color.FromArgb(168, 176, 184);
  internal static readonly Color InColor = Color.FromArgb(255, 92, 80), InCritColor = Color.FromArgb(255, 60, 48), MissColor = Color.FromArgb(150, 172, 196);
  internal static readonly Color HealColor = Color.FromArgb(104, 226, 128), HealOutColor = Color.FromArgb(150, 220, 160), DrainColor = Color.FromArgb(80, 214, 196);

  static Color DotColor(string type) {
    switch (type) {
      case "bleeding": return Color.FromArgb(232, 84, 84);
      case "poison": return Color.FromArgb(150, 214, 76);
      case "disease": return Color.FromArgb(186, 196, 92);
      case "fire": case "heat": return Color.FromArgb(255, 146, 60);
      case "acid": return Color.FromArgb(206, 230, 70);
      case "energy": return Color.FromArgb(110, 176, 255);
      case "electricity": return Color.FromArgb(120, 210, 255);
      case "cold": return Color.FromArgb(150, 222, 255);
      default: return Color.FromArgb(214, 160, 255);
    }
  }

  // Mobs are logged "a dewback"; players get their capital back (the parser lowercases the first letter)
  static string Display(string n) {
    if (n.StartsWith("a ") || n.StartsWith("an ") || n.StartsWith("the ") || n.Length == 0) return n;
    return char.ToUpperInvariant(n[0]) + n.Substring(1);
  }

  static string Full(long v) { return v.ToString("#,0", CultureInfo.InvariantCulture); }

  string Me { get { return MeOverride ?? Tracker.Me; } }

  internal void Take(CombatEvent e) {
    var me = Me;
    if (string.IsNullOrEmpty(me)) return;
    bool mine = e.Source == me, onMe = e.Target == me;
    var now = Now;
    switch (e.Type) {
      case CombatEvent.Kind.Hit:
        if (e.Amount <= 0) return;
        if (onMe && e.Source.Length > 0) attackers[e.Source] = now;
        if (mine && !onMe) {
          if (e.Dot != null) Add(OUT, "", e.Amount, e.Dot, DotColor(e.Dot), FlyItem.Sizes.Small, "dot " + e.Dot + " " + e.Target, e.Target);
          else Add(OUT, "", e.Amount, e.Glance ? "glance" : e.Blocked ? "blocked" : null, e.Crit ? CritColor : e.Glance ? GlanceColor : OutColor,
            e.Crit ? FlyItem.Sizes.Big : e.Glance ? FlyItem.Sizes.Small : FlyItem.Sizes.Normal, null, e.Target);
        } else if (onMe) {
          if (e.Dot != null) Add(IN, "−", e.Amount, e.Dot, DotColor(e.Dot), FlyItem.Sizes.Small, "dot " + e.Dot, e.Source);
          else Add(IN, "−", e.Amount, e.Glance ? "glance" : e.Blocked ? "blocked" : null, e.Crit ? InCritColor : e.Glance ? Color.FromArgb(232, 140, 132) : InColor,
            e.Crit ? FlyItem.Sizes.Big : e.Glance ? FlyItem.Sizes.Small : FlyItem.Sizes.Normal, null, e.Source);
        }
        break;
      case CombatEvent.Kind.Miss:
        if (!misses) return;
        DateTime t;
        if (mine) Add(OUT, "Miss", 0, null, GlanceColor, FlyItem.Sizes.Small, null, e.Target);
        else if (attackers.TryGetValue(e.Source, out t) && (now - t).TotalSeconds < 8) Add(IN, "Miss", 0, null, MissColor, FlyItem.Sizes.Small, null, e.Source);
        break;
      case CombatEvent.Kind.Heal:
        if (e.Amount <= 0) return;
        if (onMe) Add(HEAL, "+", e.Amount, null, HealColor, FlyItem.Sizes.Normal, null, mine ? "" : e.Source);
        else if (mine) Add(HEAL, "+", e.Amount, null, HealOutColor, FlyItem.Sizes.Small, null, "→ " + Display(e.Target), true);
        break;
      case CombatEvent.Kind.Drain:
        if (mine && e.Amount > 0) Add(HEAL, "+", e.Amount, "drain", DrainColor, FlyItem.Sizes.Small, "drain", "");
        break;
    }
  }

  // text: a word instead of a number (Miss); amount: the number (with prefix); tag: a small word after it.
  // mergeKey: DoT ticks of one kind within a moment of each other become one number with ×N.
  void Add(int zone, string prefix, long amount, string tag, Color color, FlyItem.Sizes sz, string mergeKey, string who, bool alwaysName = false) {
    if (!zoneOn[zone]) return;
    string name = (names || alwaysName) && !string.IsNullOrEmpty(who) ? (alwaysName ? who : Display(who)) : null;
    var it = new FlyItem { Prefix = amount > 0 ? prefix : "", Word = amount > 0 ? null : prefix, Amount = amount, Tag = tag, Name = name, Color = color, Size = sz };
    Zones[zone].Add(it, merge ? mergeKey : null);
  }

  // A scripted burst of sample numbers in every zone (Test button, previews)
  public void Test() {
    if (!timer.Enabled) Active = true;
    var t0 = DateTime.Now;
    string me = Me.Length > 0 ? Me : "you";
    var seq = SampleEvents(me);
    foreach (var s in seq) { var ev = s.Item2; demo.Add(Tuple.Create(t0.AddSeconds(s.Item1), (Action)(() => TakeAs(ev, me)))); }
  }

  void TakeAs(CombatEvent e, string me) { var keep = MeOverride; MeOverride = me; Take(e); MeOverride = keep; }

  internal static List<Tuple<double, CombatEvent>> SampleEvents(string me) {
    var l = new List<Tuple<double, CombatEvent>>();
    Action<double, CombatEvent> at = (s, e) => l.Add(Tuple.Create(s, e));
    Func<string, string, int, CombatEvent> hit = (a, b, n) => new CombatEvent { Type = CombatEvent.Kind.Hit, Source = a, Target = b, Amount = n };
    string mob = "a dune lizard";
    at(0.00, hit(me, mob, 1273));
    at(0.15, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = mob, Target = me, Amount = 412 });
    at(0.55, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = me, Target = mob, Amount = 2846, Crit = true });
    at(0.70, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = me, Target = mob, Amount = 312, Dot = "bleeding" });
    at(0.72, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = me, Target = "a bantha", Amount = 298, Dot = "bleeding" });
    at(0.80, new CombatEvent { Type = CombatEvent.Kind.Heal, Source = "chacoi", Target = me, Amount = 1850 });
    at(0.95, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = mob, Target = me, Amount = 236, Glance = true });
    at(1.10, new CombatEvent { Type = CombatEvent.Kind.Drain, Source = me, Target = mob, Amount = 670 });
    at(1.20, hit(me, mob, 1047));
    at(1.35, new CombatEvent { Type = CombatEvent.Kind.Miss, Source = mob });
    at(1.50, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = "", Target = me, Amount = 126, Dot = "poison" });
    at(1.70, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = me, Target = mob, Amount = 812, Glance = true });
    at(1.85, new CombatEvent { Type = CombatEvent.Kind.Hit, Source = mob, Target = me, Amount = 958, Crit = true, Blocked = true });
    at(2.00, new CombatEvent { Type = CombatEvent.Kind.Heal, Source = me, Target = "cooper", Amount = 1320 });
    at(2.10, new CombatEvent { Type = CombatEvent.Kind.Miss, Source = me });
    at(2.30, hit(me, mob, 1310));
    return l;
  }

  void Step() {
    var now = Now;
    if ((now - lastPoll).TotalMilliseconds >= 60) { lastPoll = now; if (Frozen == null) Tracker.Poll(); }
    for (int i = demo.Count - 1; i >= 0; i--) if (demo[i].Item1 <= now) { var a = demo[i].Item2; demo.RemoveAt(i); a(); }
    if (attackers.Count > 50) foreach (var k in attackers.Where(kv => (now - kv.Value).TotalSeconds > 30).Select(kv => kv.Key).ToList()) attackers.Remove(k);
    foreach (var z in Zones) z.Step();
  }

  public void Close() { timer.Stop(); foreach (var z in Zones) z.Close(); }

  // ---- standalone ----

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length >= 2 && args[0] == "--render") { FlyZone.RenderSamples(args[1], args.Length > 2 ? args[2] : null); return; }
    var ft = new FlyText();
    ft.Active = true;
    var tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR combat text", ContextMenuStrip = ft.Menu(), Visible = true };
    LayeredForm.TrackMenu(tray.ContextMenuStrip);
    Application.ApplicationExit += (s, e) => { tray.Visible = false; tray.Dispose(); ft.Close(); };
    ft.Unlocked = true;
    ft.Test();
    Application.Run();
  }
}

sealed class FlyItem {
  public enum Sizes { Small, Normal, Big }
  public string Prefix = "", Word, Tag, Name, Key;
  public long Amount;
  public int Count = 1;
  public Color Color;
  public Sizes Size;
  public DateTime Born, Popped;
  public float Push, Vx;
  public string Main { get { return Word ?? Prefix + Amount.ToString("#,0", CultureInfo.InvariantCulture) + (Count > 1 ? " ×" + Count : ""); } }
}

// One zone: a click-through, see-through window where its numbers float
sealed class FlyZone : LayeredForm {
  const int W = 260, H = 250; // design units
  const int MaxItems = 16;
  readonly FlyText owner;
  readonly int zone;
  readonly List<FlyItem> items = new List<FlyItem>(); // oldest first
  Font small, normal, big, tag, label;
  float scale = 1f;
  Point dragFrom;
  bool unlocked;
  int side; // fountain: alternate left/right

  public FlyZone(FlyText owner, int zone) {
    this.owner = owner;
    this.zone = zone;
    Text = "SWGR combat text: " + FlyText.ZoneNames[zone];
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Color.Black;
    BackOpacity = 0;
    UseOpacity("flytext");
    BackOpacity = 0; // numbers only, never a panel behind them
    Restyle();
    Location = DefaultSpot();
    ContextMenuStrip = owner.Menu();
  }

  // Around the screen centre, where your character stands: what you deal to the right, what you take to the left, heals under it
  Point DefaultSpot() {
    var wa = Screen.PrimaryScreen.Bounds;
    int cx = wa.Left + wa.Width / 2, cy = wa.Top + wa.Height / 2;
    int w = Width, h = Height, gap = (int)(70 * UiLook.Dpi);
    if (zone == FlyText.OUT) return new Point(cx + gap, cy - h - gap / 2);
    if (zone == FlyText.IN) return new Point(cx - gap - w, cy - h - gap / 2);
    return new Point(cx - gap - w, cy + gap / 4);
  }

  internal void Restyle() {
    foreach (var f in new[] { small, normal, big, tag, label }) if (f != null) f.Dispose();
    string fam = owner.Family;
    small = UiLook.PanelFont(fam, 12.5f);
    normal = UiLook.PanelFont(fam, 17f);
    big = UiLook.PanelFont(fam, 23f);
    tag = UiLook.PanelFont(UiLook.Regular, 9f);
    label = UiLook.PanelFont(UiLook.Regular, 9.5f);
    scale = UiLook.Scale * owner.Factor;
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(H * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  internal void Clear() { items.Clear(); Invalidate(); }

  internal void ShowUnlocked(bool on) {
    unlocked = on;
    SetClickThrough(!on);
    if (on) { Visible = true; KeepOnTop(); }
    else if (items.Count == 0) Visible = false;
    Invalidate();
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
  void SetClickThrough(bool on) {
    int ex = GetWindowLong(Handle, -20);
    SetWindowLong(Handle, -20, on ? ex | 0x20 : ex & ~0x20);
  }

  static double Life(FlyItem it) { return it.Size == FlyItem.Sizes.Big ? 2.1 : it.Size == FlyItem.Sizes.Small ? 1.4 : 1.7; }
  float LineH(FlyItem it) { return it.Size == FlyItem.Sizes.Big ? 38 : it.Size == FlyItem.Sizes.Small ? 23 : 29; }

  internal void Add(FlyItem it, string key) {
    var now = owner.Now;
    it.Born = it.Popped = now;
    if (key != null) {
      var same = items.LastOrDefault(x => x.Key == key && (now - x.Born).TotalSeconds < 0.45);
      if (same != null) { same.Amount += it.Amount; same.Count++; same.Popped = now; Invalidate(); return; }
      it.Key = key;
    }
    if (owner.Motion == 2) { side = 1 - side; it.Vx = it.Size == FlyItem.Sizes.Big ? 0 : (side == 0 ? -1 : 1) * (22 + (items.Count * 13) % 30); }
    if (items.Count > 0) {
      // keep a line of space between the newest number and this one: push the others along
      var last = items[items.Count - 1];
      float gap = (LineH(last) + LineH(it)) / 2, d = Travel(last, now);
      if (d < gap) foreach (var x in items) x.Push += gap - d;
    }
    items.Add(it);
    while (items.Count > MaxItems) items.RemoveAt(0);
    if (owner.Frozen != null) return; // --render
    if (!Visible) { Visible = true; SetClickThrough(!unlocked); }
    Step();
  }

  // Scroll: how far the item has moved from where it appeared
  float Travel(FlyItem it, DateTime now) {
    double age = (now - it.Born).TotalSeconds;
    double speed = (H - 70) / 1.7;
    return (float)(age * speed) + it.Push;
  }

  static double Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

  internal void Step() {
    var now = owner.Now;
    items.RemoveAll(x => (now - x.Born).TotalSeconds > Life(x));
    if (items.Count == 0 && !unlocked) { if (Visible) Visible = false; return; }
    if (zone != FlyText.OUT) PanelOpacity = owner.Zones[FlyText.OUT].PanelOpacity; // one opacity setting for all three zones
    if (owner.Frozen == null) KeepOnTop();
    Invalidate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var now = owner.Now;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    if (unlocked) using (var p = new Pen(Color.FromArgb(220, 80, 210, 200), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
    g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);
    if (unlocked) {
      var lb = new RectangleF(0, 4, W, 16);
      using (var fmt = new StringFormat { Alignment = StringAlignment.Center }) using (var b = new SolidBrush(Color.FromArgb(230, 80, 210, 200)))
        g.DrawString(FlyText.ZoneNames[zone] + " · drag to move", label, b, lb, fmt);
    }
    foreach (var it in items) Draw(g, it, now);
  }

  void Draw(Graphics g, FlyItem it, DateTime now) {
    double age = (now - it.Born).TotalSeconds, life = Life(it);
    double alpha = Smooth(age / 0.08) * (1 - Smooth((age - (life - 0.5)) / 0.5));
    if (alpha <= 0.01) return;
    double sinceP = (now - it.Popped).TotalSeconds;
    float pop = it.Size == FlyItem.Sizes.Big ? (float)(1 + 0.55 * Math.Pow(Math.Max(0, 1 - sinceP / 0.22), 2)) : (float)(1 + 0.18 * Math.Pow(Math.Max(0, 1 - sinceP / 0.15), 2));
    float x = W / 2f, y;
    int motion = owner.Motion;
    if (motion == 0) y = H - 26 - Travel(it, now);
    else if (motion == 1) y = 34 + Travel(it, now);
    else {
      // fountain: up and out to one side, then down; crits rise straight from the middle
      float t = (float)age;
      if (it.Size == FlyItem.Sizes.Big) { y = H * 0.55f - 60 * (float)Smooth(t / 0.5) - 14 * t; }
      else { x += it.Vx * t * 1.1f; y = H * 0.58f - (190 * t - 190 * t * t * 0.62f); }
      y -= it.Push * 0.8f; // a burst stacks upward instead of piling up
    }
    var f = it.Size == FlyItem.Sizes.Big ? big : it.Size == FlyItem.Sizes.Small ? small : normal;
    using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone()) {
      fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
      string main = it.Main;
      string after = it.Tag != null && it.Name != null ? " " + it.Tag + " · " + it.Name : it.Tag != null ? " " + it.Tag : it.Name != null ? " " + it.Name : null;
      float wm = g.MeasureString(main, f, 10000, fmt).Width;
      float wa = after != null ? g.MeasureString(after, tag, 10000, fmt).Width : 0;
      float fh = f.GetHeight(g);
      var st = g.Save();
      g.TranslateTransform(x, y);
      g.ScaleTransform(pop, pop);
      float left = -(wm + wa) / 2;
      DrawFly(g, main, f, new PointF(left, -fh / 2), it.Color, fmt, alpha);
      if (after != null) DrawFly(g, after, tag, new PointF(left + wm, -fh / 2 + (fh - tag.GetHeight(g)) * 0.72f), Mix(it.Color, Color.FromArgb(200, 206, 212), 0.55), fmt, alpha * 0.9);
      g.Restore(st);
    }
  }

  static Color Mix(Color a, Color b, double t) {
    return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
  }

  static Color A(int a, Color c) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

  // The three looks. Kept light: a thin edge for contrast over bright scenery, no heavy shadow.
  void DrawFly(Graphics g, string s, Font f, PointF at, Color fill, StringFormat fmt, double alpha) {
    using (var path = new GraphicsPath()) {
      path.AddString(s, f.FontFamily, (int)f.Style, f.Size, at, fmt);
      int style = owner.Style;
      if (style == 0) { // Clean: soft shadow below, thin dark edge
        using (var m = new Matrix()) {
          m.Translate(0, f.Size * 0.07f);
          using (var sh = (GraphicsPath)path.Clone()) {
            sh.Transform(m);
            using (var p = new Pen(A((int)(80 * alpha), Color.Black), f.Size * 0.16f) { LineJoin = LineJoin.Round }) g.DrawPath(p, sh);
          }
        }
        using (var p = new Pen(A((int)(170 * alpha), Color.FromArgb(10, 12, 16)), Math.Max(1f, f.Size * 0.075f)) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      } else if (style == 1) { // Outline: a crisp solid black outline, nothing else
        using (var p = new Pen(A((int)(235 * alpha), Color.Black), Math.Max(1.4f, f.Size * 0.12f)) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      } else { // Glow: a soft halo in the number's own colour, and a hairline edge
        for (int i = 3; i >= 1; i--)
          using (var p = new Pen(A((int)(34 * alpha), fill), f.Size * 0.1f * i) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
        using (var p = new Pen(A((int)(150 * alpha), Color.FromArgb(10, 12, 16)), Math.Max(0.8f, f.Size * 0.05f)) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
      }
      using (var b = new SolidBrush(A((int)(255 * alpha), style == 2 ? Mix(fill, Color.White, 0.25) : fill))) g.FillPath(b, path);
    }
  }

  protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragFrom = e.Location; base.OnMouseDown(e); }
  protected override void OnMouseMove(MouseEventArgs e) {
    if (unlocked && e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
    base.OnMouseMove(e);
  }
  protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) owner.Save(); base.OnMouseUp(e); }

  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x20 | 0x08000000 | 0x80; return cp; } // click-through | NOACTIVATE | TOOLWINDOW
  }

  // --render out.png [background]: out.png compares the styles (scroll up, names on), out-motion.png the motions
  // (Clean), each with the three zones side by side 1.6 s into the sample burst. The background is a game screenshot
  // if given, else a plain sandy colour.
  internal static void RenderSamples(string file, string background) {
    Image bg = null;
    try { if (background != null) bg = Image.FromFile(background); } catch { }
    var styles = new List<int[]>();
    for (int i = 0; i < FlyText.Styles.Length; i++) styles.Add(new[] { i, 0, 1 });
    Sheet(file, styles, bg);
    var motions = new List<int[]>();
    for (int i = 0; i < FlyText.Motions.Length; i++) motions.Add(new[] { 0, i, 0 });
    Sheet(Path.ChangeExtension(file, null) + "-motion.png", motions, bg);
    if (bg != null) bg.Dispose();
  }

  // cells: { style, motion, names }
  static void Sheet(string file, List<int[]> cells, Image bg) {
    var ft = new FlyText(false);
    ft.MeOverride = "you";
    var t0 = new DateTime(2026, 11, 3, 20, 0, 0);
    var shots = new List<Tuple<string, Bitmap>>();
    foreach (var c in cells) {
      ft.Frozen = t0;
      ft.Style = c[0]; ft.Motion = c[1]; ft.ShowNames = c[2] == 1; // Frozen is set, so nothing is saved
      foreach (var z in ft.Zones) { z.Clear(); z.Restyle(); }
      const double At = 1.6;
      foreach (var ev in FlyText.SampleEvents("you").Where(x => x.Item1 <= At)) { ft.Frozen = t0.AddSeconds(ev.Item1); ft.Take(ev.Item2); }
      ft.Frozen = t0.AddSeconds(At);
      var zs = new[] { ft.Zones[FlyText.IN], ft.Zones[FlyText.OUT], ft.Zones[FlyText.HEAL] };
      int zw = zs[0].ClientSize.Width, zh = zs[0].ClientSize.Height;
      var bmp = new Bitmap(zw * 3, zh);
      using (var g = Graphics.FromImage(bmp)) {
        if (bg != null) g.DrawImage(bg, new Rectangle(0, 0, bmp.Width, bmp.Height), new Rectangle(Math.Max(0, bg.Width / 2 - bmp.Width / 2), Math.Max(0, bg.Height / 2 - bmp.Height / 2), bmp.Width, bmp.Height), GraphicsUnit.Pixel);
        else g.Clear(Color.FromArgb(150, 132, 104));
        for (int i = 0; i < 3; i++) {
          var st = g.Save();
          g.TranslateTransform(i * zw, 0);
          zs[i].OnPaint(new PaintEventArgs(g, new Rectangle(0, 0, zw, zh)));
          g.Restore(st);
          using (var p = new Pen(Color.FromArgb(60, 255, 255, 255)) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, i * zw, 0, zw - 1, zh - 1);
        }
      }
      shots.Add(Tuple.Create(FlyText.Styles[c[0]] + " · " + FlyText.Motions[c[1]] + (c[2] == 1 ? " · names on" : "") + "   (taken · dealt · heals)", bmp));
    }
    int cw = shots[0].Item2.Width, ch = shots[0].Item2.Height, pad = 10, head = 24;
    using (var all = new Bitmap(cw + 2 * pad, shots.Count * (ch + head + pad) + pad)) {
      using (var g = Graphics.FromImage(all)) {
        g.Clear(Color.FromArgb(24, 26, 30));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using (var lf = new Font("Segoe UI", 10f)) using (var wb = new SolidBrush(Color.FromArgb(220, 224, 228)))
          for (int i = 0; i < shots.Count; i++) {
            int cy = pad + i * (ch + head + pad);
            g.DrawString(shots[i].Item1, lf, wb, pad, cy);
            g.DrawImage(shots[i].Item2, pad, cy + head);
            shots[i].Item2.Dispose();
          }
      }
      all.Save(file);
    }
  }
}
