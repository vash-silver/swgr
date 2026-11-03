// SWGR tools: one tray app hosting the target HP overlay (overlay.cs), the part verdict panel (parts-overlay.cs),
// the mob details panel (mob-details.cs), the XP tracker (xp-tracker.cs), the DPS meter (dps-meter.cs), the threat panel (threat.cs), the credits tracker (credits.cs), the kill sound and kill streak panel (kill-sound.cs, kill-streak.cs), the nearby mobs panel (mobs-panel.cs), the combat text (flytext.cs) and the inventory part scan (parts.cs),
// plus a main window to set them up. They read client memory only (the DPS meter reads just the game's chat log file); see those files for the guardrails.
// Never add WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
//
// build: cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:SwgrTools -out:../bin/swgr-tools.exe swgr-tools.cs overlay.cs parts.cs ui.cs parts-overlay.cs mob-details.cs xp-tracker.cs dps-meter.cs threat.cs credits.cs kill-sound.cs kill-streak.cs mobs-panel.cs flytext.cs
// Settings (which overlays are on) live in swgr-tools.cfg next to the exe; text size and font in swgr-ui.cfg;
// each overlay keeps its own .pos file.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

sealed class SwgrTools : ApplicationContext {
  readonly string cfgFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-tools.cfg");
  internal readonly OverlayForm hp = new OverlayForm(true);
  internal readonly OverlayForm self = new OverlayForm(true, true); // your own health/action/mind and buffs
  readonly PartsOverlay parts = new PartsOverlay(true);
  internal readonly MobDetails mob = new MobDetails(true);
  internal readonly XpTracker xp = new XpTracker(true);
  internal readonly DpsMeter dps = new DpsMeter(true);
  internal readonly ThreatPanel threat = new ThreatPanel(true);
  internal readonly CreditsTracker credits = new CreditsTracker(true);
  internal readonly KillSound killSound = new KillSound();
  internal readonly KillStreak streak = new KillStreak(true);
  internal readonly NearbyMobs mobs = new NearbyMobs(true);
  internal readonly FlyText fly = new FlyText(true);
  readonly NotifyIcon tray;
  readonly ToolStripMenuItem hpItem, partsItem, mobItem, xpItem, dpsItem, selfItem, threatItem, creditsItem, streakItem, mobsItem, flyItem;
  ToolsWindow main;

  // The overlays, in the order the menus and the main window list them
  internal const int HP = 0, MOB = 1, PARTS = 2, XP = 3, DPS = 4, SELF = 5, THREAT = 6, CREDITS = 7, STREAK = 8, MOBS = 9, FLY = 10;
  internal static readonly string[] OverlayNames = { "Target HP overlay", "Mob details panel", "Part verdict panel", "XP tracker", "DPS meter", "Self overlay", "Threat panel", "Credits tracker", "Kill streak", "Nearby mobs", "Combat text" };
  internal static readonly string[] OverlayHelp = {
    "Your target's name, health, action and mind, in one of six styles (Questlog matches the DPS meter).",
    "Everything the client knows about your target: level, pools, posture, flags, faction, con.",
    "The 1M+ verdict for the ship part you click in the inventory window.",
    "Experience gained this session and per hour, by xp type.",
    "Damage per second, crits, damage taken and damage per target, read from the game's chat log. Needs combat messages on (Options > Chat > Combat Message Filter: Self, or Group to see your party).",
    "Your own health, action and mind with numbers, and your buffs and debuffs with timers. Same styles as the target overlay.",
    "For tanking: every mob near you that is attacking a player. Loose ones (on someone else) in red with whom they're hitting, the ones on you in green. The target and self overlays also show ON YOU / ON <name> under the panel.",
    "Your cash, bank and total, credits earned and spent this session, the net per hour, and the latest changes (moves between cash and bank are shown but not counted).",
    "Your kill streak as floating text over the game: the count, popping on each kill, and a title that climbs from Double to Galactic threat. It fades in on each kill and out a few seconds later, and shows STREAK ENDED when a streak runs out. Click-through; tick Unlock to move to drag it.",
    "Every mob around you with a full HP bar: name, level, current / max and distance. Mobs on you have a green stripe, mobs on another player a red one (with whom they're hitting), and your target is outlined in blue.",
    "Our own floating combat text, from the game's chat log: what you deal (crits bigger and gold, DoT ticks coloured by type), what you take and heals, each in its own zone you can put anywhere. Click-through; tick Unlock to move to drag the zones. Turn the game's numbers off in Options > Combat > Show Damage Numbers over Others / over Self.",
  };

  SwgrTools() {
    bool hpOn = true, partsOn = true, mobOn = true, xpOn = true, dpsOn = true, selfOn = true, threatOn = true, creditsOn = true, streakOn = true, mobsOn = true, flyOn = true;
    try {
      foreach (var line in File.ReadAllLines(cfgFile)) {
        var kv = line.Split('=');
        if (kv.Length != 2) continue;
        if (kv[0] == "hp") hpOn = kv[1] == "1";
        if (kv[0] == "parts") partsOn = kv[1] == "1";
        if (kv[0] == "mob") mobOn = kv[1] == "1";
        if (kv[0] == "xp") xpOn = kv[1] == "1";
        if (kv[0] == "dps") dpsOn = kv[1] == "1";
        if (kv[0] == "self") selfOn = kv[1] == "1";
        if (kv[0] == "threat") threatOn = kv[1] == "1";
        if (kv[0] == "credits") creditsOn = kv[1] == "1";
        if (kv[0] == "streak") streakOn = kv[1] == "1";
        if (kv[0] == "mobs") mobsOn = kv[1] == "1";
        if (kv[0] == "fly") flyOn = kv[1] == "1";
      }
    } catch { }

    var menu = new ContextMenuStrip();
    menu.Items.Add(new ToolStripMenuItem("Open SWGR tools", null, (s, e) => ShowMain(null)) { Font = new Font(menu.Font, FontStyle.Bold) });
    menu.Items.Add(new ToolStripSeparator());
    hpItem = new ToolStripMenuItem(OverlayNames[HP], null, (s, e) => SetOn(HP, !IsOn(HP)));
    mobItem = new ToolStripMenuItem(OverlayNames[MOB], null, (s, e) => SetOn(MOB, !IsOn(MOB)));
    partsItem = new ToolStripMenuItem(OverlayNames[PARTS], null, (s, e) => SetOn(PARTS, !IsOn(PARTS)));
    xpItem = new ToolStripMenuItem(OverlayNames[XP], null, (s, e) => SetOn(XP, !IsOn(XP)));
    dpsItem = new ToolStripMenuItem(OverlayNames[DPS], null, (s, e) => SetOn(DPS, !IsOn(DPS)));
    selfItem = new ToolStripMenuItem(OverlayNames[SELF], null, (s, e) => SetOn(SELF, !IsOn(SELF)));
    threatItem = new ToolStripMenuItem(OverlayNames[THREAT], null, (s, e) => SetOn(THREAT, !IsOn(THREAT)));
    creditsItem = new ToolStripMenuItem(OverlayNames[CREDITS], null, (s, e) => SetOn(CREDITS, !IsOn(CREDITS)));
    streakItem = new ToolStripMenuItem(OverlayNames[STREAK], null, (s, e) => SetOn(STREAK, !IsOn(STREAK)));
    mobsItem = new ToolStripMenuItem(OverlayNames[MOBS], null, (s, e) => SetOn(MOBS, !IsOn(MOBS)));
    flyItem = new ToolStripMenuItem(OverlayNames[FLY], null, (s, e) => SetOn(FLY, !IsOn(FLY)));
    menu.Items.Add(selfItem);
    menu.Items.Add(hpItem);
    menu.Items.Add(new ToolStripMenuItem("Target HP style") { DropDown = hp.HostMenu });
    menu.Items.Add(mobItem);
    menu.Items.Add(partsItem);
    menu.Items.Add(xpItem);
    menu.Items.Add(dpsItem);
    menu.Items.Add(threatItem);
    menu.Items.Add(mobsItem);
    var threatAlways = new ToolStripMenuItem("Threat panel always visible", null, (s, e) => threat.AlwaysVisible = !threat.AlwaysVisible);
    menu.Opening += (s, e) => threatAlways.Checked = threat.AlwaysVisible;
    menu.Items.Add(threatAlways);
    menu.Items.Add(creditsItem);
    menu.Items.Add(streakItem);
    menu.Items.Add(flyItem);
    menu.Items.Add(killSound.Menu("Kill sound"));
    menu.Items.Add("Scan inventory for ship parts...", null, (s, e) => ShowMain(ToolsWindow.SCAN));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Exit", null, (s, e) => ExitThread());
    // The overlays' own "Turn off" items flip Active; keep the checkmarks in sync when the menu opens
    menu.Opening += (s, e) => { hpItem.Checked = IsOn(HP); partsItem.Checked = IsOn(PARTS); mobItem.Checked = IsOn(MOB); xpItem.Checked = IsOn(XP); dpsItem.Checked = IsOn(DPS); selfItem.Checked = IsOn(SELF); threatItem.Checked = IsOn(THREAT); creditsItem.Checked = IsOn(CREDITS); streakItem.Checked = IsOn(STREAK); mobsItem.Checked = IsOn(MOBS); flyItem.Checked = IsOn(FLY); };

    LayeredForm.TrackMenu(menu); // the overlays stop re-asserting topmost while it is open
    tray = new NotifyIcon { Icon = MakeIcon(), Text = "SWGR tools", ContextMenuStrip = menu, Visible = true };
    tray.DoubleClick += (s, e) => ShowMain(null);

    hp.Active = hpOn;
    parts.Active = partsOn;
    mob.Active = mobOn;
    xp.Active = xpOn;
    dps.Active = dpsOn;
    self.Active = selfOn;
    threat.Active = threatOn;
    credits.Active = creditsOn;
    streak.Active = streakOn;
    mobs.Active = mobsOn;
    fly.Active = flyOn;
    killSound.Killed += streak.Add;
    killSound.PitchStep = () => streak.Count - 1;
    killSound.Watch = true; // the streak panel counts kills even with the sound off
    CombatTracker.GameName = () => { if (!Parts.Alive()) Parts.Attach(); return Parts.PlayerName(); }; // your name from the game, so the DPS meter can tell you from your group
  }

  // The panel behind each OverlayNames entry
  internal LayeredForm Panel(int which) { return which == HP ? hp : which == MOB ? (LayeredForm)mob : which == PARTS ? (LayeredForm)parts : which == XP ? (LayeredForm)xp : which == DPS ? (LayeredForm)dps : which == THREAT ? (LayeredForm)threat : which == CREDITS ? (LayeredForm)credits : which == STREAK ? (LayeredForm)streak : which == MOBS ? (LayeredForm)mobs : which == FLY ? (LayeredForm)fly.Zones[FlyText.OUT] : self; }

  internal bool IsOn(int which) { return which == HP ? hp.Active : which == MOB ? mob.Active : which == XP ? xp.Active : which == DPS ? dps.Active : which == SELF ? self.Active : which == THREAT ? threat.Active : which == CREDITS ? credits.Active : which == STREAK ? streak.Active : which == MOBS ? mobs.Active : which == FLY ? fly.Active : parts.Active; }

  internal void SetOn(int which, bool on) {
    if (which == HP) hp.Active = on; else if (which == MOB) mob.Active = on; else if (which == XP) xp.Active = on; else if (which == DPS) dps.Active = on; else if (which == SELF) self.Active = on; else if (which == THREAT) threat.Active = on; else if (which == CREDITS) credits.Active = on; else if (which == STREAK) streak.Active = on; else if (which == MOBS) mobs.Active = on; else if (which == FLY) fly.Active = on; else parts.Active = on;
    try { File.WriteAllLines(cfgFile, new[] { "hp=" + (hp.Active ? 1 : 0), "parts=" + (parts.Active ? 1 : 0), "mob=" + (mob.Active ? 1 : 0), "xp=" + (xp.Active ? 1 : 0), "dps=" + (dps.Active ? 1 : 0), "self=" + (self.Active ? 1 : 0), "threat=" + (threat.Active ? 1 : 0), "credits=" + (credits.Active ? 1 : 0), "streak=" + (streak.Active ? 1 : 0), "mobs=" + (mobs.Active ? 1 : 0), "fly=" + (fly.Active ? 1 : 0) }); } catch { }
    if (main != null && !main.IsDisposed) main.SyncOverlays();
  }

  internal void ShowMain(int? page) {
    if (main == null || main.IsDisposed) main = new ToolsWindow(this);
    main.Show();
    if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
    main.Activate();
    if (page.HasValue) main.ShowPage(page.Value);
  }

  internal void Exit() { ExitThread(); }

  // Tray icon: a small teal ring with a dot, drawn at runtime
  internal static Icon MakeIcon() {
    using (var bmp = new Bitmap(32, 32)) {
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Color.FromArgb(80, 210, 200), 4)) g.DrawEllipse(p, 4, 4, 24, 24);
        using (var b = new SolidBrush(Color.FromArgb(233, 214, 90))) g.FillEllipse(b, 12, 12, 8, 8);
      }
      return Icon.FromHandle(bmp.GetHicon());
    }
  }

  protected override void ExitThreadCore() {
    tray.Visible = false;
    tray.Dispose();
    hp.Close();
    parts.Close();
    mob.Close();
    xp.Close();
    dps.Close();
    self.Close();
    threat.Close();
    credits.Close();
    killSound.Dispose();
    streak.Close();
    mobs.Close();
    fly.Close();
    if (main != null && !main.IsDisposed) main.Close();
    Parts.Detach();
    base.ExitThreadCore();
  }

  // --render-main <prefix> [height [bottom]]: saves each main window page as <prefix>-<n>.png and exits (no game needed)
  [STAThread]
  static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    var app = new SwgrTools();
    if (args.Length >= 2 && args[0] == "--render-main") {
      app.dps.LoadSample(); // the Damage page with a sample group fight
      app.dps.Tracker.CloseIfIdle(DateTime.Now);
      app.ShowMain(0);
      int tall; if (args.Length > 2 && int.TryParse(args[2], out tall)) app.main.Height = tall; // optional height, to see a whole page
      for (int p = 0; p < ToolsWindow.PageCount; p++) {
        app.main.ShowPage(p);
        Application.DoEvents();
        if (args.Length > 3 && args[3] == "bottom") app.main.ScrollToBottom();
        using (var bmp = new Bitmap(app.main.Width, app.main.Height)) {
          app.main.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
          bmp.Save(args[1] + "-" + p + ".png");
        }
      }
      app.ExitThread();
      return;
    }
    Application.Run(app);
  }
}

// The main window: a sidebar with Overlays / Appearance / Part scan / Value sheet / Damage pages. Closing it only hides it (the tray icon stays).
sealed class ToolsWindow : Form {
  internal const int OVERLAYS = 0, APPEARANCE = 1, SCAN = 2, VALUES = 3, DAMAGE = 4;
  static readonly string[] PageNames = { "Overlays", "Appearance", "Part scan", "Value sheet", "Damage" };

  internal static readonly Color Back = Color.FromArgb(18, 22, 28), Side = Color.FromArgb(24, 29, 36), Hover = Color.FromArgb(32, 39, 48);
  internal static readonly Color Selected = Color.FromArgb(38, 47, 58), TextColor = Color.FromArgb(235, 238, 242), Muted = Color.FromArgb(140, 152, 160);
  internal static readonly Color Accent = Color.FromArgb(80, 210, 200), Line = Color.FromArgb(44, 52, 62);

  internal static int PageCount { get { return PageNames.Length; } }

  readonly SwgrTools host;
  readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Back, Padding = UiLook.Pad(24, 18, 24, 18) };
  readonly Button[] nav = new Button[PageNames.Length];
  readonly Control[] pages = new Control[PageNames.Length];
  readonly CheckBox[] overlayBoxes = new CheckBox[SwgrTools.OverlayNames.Length];
  readonly ScanPage scan = new ScanPage { Dock = DockStyle.Fill };
  int page = -1;
  bool syncing;

  public ToolsWindow(SwgrTools host) {
    this.host = host;
    Text = "SWGR tools";
    Icon = SwgrTools.MakeIcon();
    ClientSize = UiLook.Sz(820, 560);
    MinimumSize = UiLook.Sz(640, 420);
    BackColor = Back;
    ForeColor = TextColor;
    Font = new Font("Segoe UI", 9.5f);
    StartPosition = FormStartPosition.CenterScreen;

    var side = new Panel { Dock = DockStyle.Left, Width = UiLook.D(170), BackColor = Side, Padding = UiLook.Pad(0, 14, 0, 0) };
    var title = new Label { Text = "SWGR tools", Dock = DockStyle.Top, Height = UiLook.D(40), Padding = UiLook.Pad(18, 0, 0, 0), ForeColor = TextColor,
      Font = new Font("Segoe UI Semibold", 12f), TextAlign = ContentAlignment.MiddleLeft };
    for (int i = PageNames.Length - 1; i >= 0; i--) {
      int idx = i;
      var b = nav[i] = new NavButton {
        Text = PageNames[i], Dock = DockStyle.Top, Height = UiLook.D(38), FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
        Padding = UiLook.Pad(16, 0, 0, 0), ForeColor = TextColor, BackColor = Side, Cursor = Cursors.Hand,
      };
      b.FlatAppearance.BorderSize = 0;
      b.FlatAppearance.MouseOverBackColor = Hover;
      b.Click += (s, e) => ShowPage(idx);
      side.Controls.Add(b);
    }
    side.Controls.Add(title);
    var exit = new NavButton { Text = "Exit SWGR tools", Dock = DockStyle.Bottom, Height = UiLook.D(36), FlatStyle = FlatStyle.Flat, ForeColor = Muted, BackColor = Side,
      TextAlign = ContentAlignment.MiddleLeft, Padding = UiLook.Pad(16, 0, 0, 0) };
    exit.FlatAppearance.BorderSize = 0;
    exit.FlatAppearance.MouseOverBackColor = Hover;
    exit.Click += (s, e) => host.Exit();
    side.Controls.Add(exit);

    pages[OVERLAYS] = BuildOverlays();
    pages[APPEARANCE] = BuildAppearance();
    pages[SCAN] = scan;
    pages[VALUES] = BuildValues();
    pages[DAMAGE] = new DpsPage(host.dps) { Dock = DockStyle.Fill };
    Controls.Add(content);
    Controls.Add(side);
    Activated += (s, e) => SyncOverlays(); // an overlay may have been turned off from its own right-click menu
    ShowPage(OVERLAYS);
  }

  // --render-main ... bottom: scroll the page to its end
  internal void ScrollToBottom() {
    foreach (Control c in content.Controls) {
      var sc = c as ScrollableControl;
      if (sc != null && sc.AutoScroll && sc.Controls.Count > 0) { sc.ScrollControlIntoView(sc.Controls[sc.Controls.Count - 1]); Application.DoEvents(); }
    }
  }

  internal void ShowPage(int p) {
    if (p == page) { if (p == SCAN) scan.Rescan(); return; }
    page = p;
    content.Controls.Clear();
    content.Controls.Add(pages[p]);
    for (int i = 0; i < nav.Length; i++) nav[i].BackColor = i == p ? Selected : Side;
    if (p == SCAN) scan.Rescan();
  }

  static Label Heading(string text) {
    return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = TextColor, Margin = UiLook.Pad(0, 0, 0, 4) };
  }

  static Label Help(string text, int indent = 0) {
    return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = UiLook.Pad(indent, 0, 0, 14), MaximumSize = UiLook.Sz(560, 0) };
  }

  static FlowLayoutPanel Column() {
    return new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Back };
  }

  Control BuildOverlays() {
    var col = Column();
    col.Controls.Add(Heading("Overlays"));
    col.Controls.Add(Help("Small always-on-top windows over the game. Drag one to move it; right-click it for its own options."));
    for (int i = 0; i < overlayBoxes.Length; i++) {
      int idx = i;
      var cb = overlayBoxes[i] = new CheckBox { Text = SwgrTools.OverlayNames[i], AutoSize = true, ForeColor = TextColor,
        Font = new Font("Segoe UI Semibold", 10f), Margin = UiLook.Pad(0, 6, 0, 0), Checked = host.IsOn(i) };
      cb.CheckedChanged += (s, e) => { if (!syncing) host.SetOn(idx, cb.Checked); };
      col.Controls.Add(cb);
      col.Controls.Add(Help(SwgrTools.OverlayHelp[i], 20));
      var opts = Options(i);
      if (opts != null) col.Controls.Add(opts);
    }
    col.Controls.Add(Heading("Kill sound"));
    col.Controls.Add(Help("A short sound when a mob you were fighting dies (one that was attacking you, or your damaged target). With rising pitch, each kill of a streak plays a semitone higher, up to an octave. Picking a sound or a volume plays it."));
    var ks = Row(0);
    var names = new List<string> { "Off" }; names.AddRange(KillSound.Names);
    var vols = new List<string>(); foreach (var v in KillSound.Volumes) vols.Add(v + " %");
    Combo(ks, "Kill sound", names.ToArray(), 120,
      () => host.killSound.Sound == null ? 0 : Array.IndexOf(KillSound.Names, host.killSound.Sound) + 1,
      v => { host.killSound.Set(v == 0 ? null : KillSound.Names[v - 1], host.killSound.Volume); host.killSound.Play(); });
    Combo(ks, "Volume", vols.ToArray(), 80,
      () => Math.Max(0, Array.IndexOf(KillSound.Volumes, host.killSound.Volume)),
      v => { host.killSound.Set(host.killSound.Sound ?? KillSound.Names[0], KillSound.Volumes[v]); host.killSound.Play(); SyncOverlays(); });
    ks.Margin = UiLook.Pad(0, 0, 0, 8);
    col.Controls.Add(ks);
    var ks2 = Row(0);
    Check(ks2, "Rising pitch in a streak", () => host.killSound.Rising, v => host.killSound.Rising = v);
    Btn(ks2, "Play", 70, () => host.killSound.Play());
    col.Controls.Add(ks2);
    SyncOverlays(); // fill every option from its window
    return col;
  }

  // ---------- per-overlay options on the Overlays page (the same settings as each window's right-click menu) ----------

  readonly List<Action> refresh = new List<Action>(); // re-read each setting (the menus can change them too)

  static FlowLayoutPanel Row(int indent) { return new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = UiLook.Pad(indent, 0, 0, 14), BackColor = Back }; }
  static Label RowLabel(string text, int left) { return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = UiLook.Pad(left, 6, 10, 0) }; }

  // A dark DropDownList (the stock one draws a white box)
  static ComboBox DarkCombo(string[] items, int width) {
    var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = UiLook.D(width), BackColor = Selected, ForeColor = TextColor,
      DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = UiLook.D(22) };
    c.Items.AddRange(items);
    c.DrawItem += (s, e) => {
      if (e.Index < 0) return;
      bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
      using (var b = new SolidBrush(sel ? Hover : Selected)) e.Graphics.FillRectangle(b, e.Bounds);
      TextRenderer.DrawText(e.Graphics, (string)c.Items[e.Index], c.Font, new Rectangle(e.Bounds.X + UiLook.D(6), e.Bounds.Y, e.Bounds.Width - UiLook.D(6), e.Bounds.Height),
        TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    };
    return c;
  }

  void Combo(FlowLayoutPanel row, string label, string[] items, int width, Func<int> get, Action<int> set) {
    row.Controls.Add(RowLabel(label, row.Controls.Count == 0 ? 0 : 14));
    var c = DarkCombo(items, width);
    c.SelectedIndexChanged += (s, e) => { if (!syncing && c.SelectedIndex >= 0) set(c.SelectedIndex); };
    refresh.Add(() => c.SelectedIndex = get());
    row.Controls.Add(c);
  }

  void Check(FlowLayoutPanel row, string text, Func<bool> get, Action<bool> set) {
    var cb = new CheckBox { Text = text, AutoSize = true, ForeColor = TextColor, Margin = UiLook.Pad(row.Controls.Count == 0 ? 0 : 14, 5, 0, 0) };
    cb.CheckedChanged += (s, e) => { if (!syncing) set(cb.Checked); };
    refresh.Add(() => cb.Checked = get());
    row.Controls.Add(cb);
  }

  void Btn(FlowLayoutPanel row, string text, int width, Action act) {
    var b = FlatButton(text);
    b.Width = UiLook.D(width);
    b.Margin = UiLook.Pad(row.Controls.Count == 0 ? 0 : 10, 0, 0, 0);
    b.Click += (s, e) => act();
    row.Controls.Add(b);
  }

  // Turn an overlay on so its preview can be seen
  void On(int which) { if (!host.IsOn(which)) { host.SetOn(which, true); SyncOverlays(); } }

  // Two option rows under one overlay
  static Control Stack(FlowLayoutPanel first, FlowLayoutPanel second) {
    var both = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = UiLook.Pad(0, 0, 0, 0), BackColor = Back };
    first.Margin = UiLook.Pad(20, 0, 0, 8);
    both.Controls.Add(first);
    both.Controls.Add(second);
    return both;
  }

  Control Options(int i) {
    var row = Row(20);
    if (i == SwgrTools.HP) {
      Combo(row, "Style", OverlayForm.Styles, 160, () => host.hp.Style, v => host.hp.Style = v);
      Combo(row, "Colours", OverlayForm.QlColorNames, 100, () => OverlayForm.QlColors, v => { OverlayForm.QlColors = v; host.hp.Invalidate(); host.self.Invalidate(); host.hp.Preview(); SyncOverlays(); });
      var b2 = Row(20);
      Btn(b2, "Preview", 90, () => { On(i); host.hp.Preview(); });
      Btn(b2, "Preview ship", 110, () => { On(i); host.hp.PreviewShip(); });
      Btn(b2, "Reset size", 100, () => host.hp.ResetSize());
      return Stack(row, b2);
    } else if (i == SwgrTools.SELF) {
      Combo(row, "Style", OverlayForm.Styles, 160, () => host.self.Style, v => host.self.Style = v);
      Combo(row, "Colours", OverlayForm.QlColorNames, 100, () => OverlayForm.QlColors, v => { OverlayForm.QlColors = v; host.hp.Invalidate(); host.self.Invalidate(); host.self.Preview(); SyncOverlays(); });
      var b2 = Row(20);
      Btn(b2, "Preview", 90, () => { On(i); host.self.Preview(); });
      Btn(b2, "Reset size", 100, () => host.self.ResetSize());
      return Stack(row, b2);
    } else if (i == SwgrTools.XP) {
      Check(row, "Show all xp types", () => host.xp.ShowAll, v => host.xp.ShowAll = v);
      Btn(row, "Reset session", 120, () => host.xp.ResetSession());
    } else if (i == SwgrTools.DPS) {
      Combo(row, "Style", DpsMeter.Looks, 120, () => host.dps.Look, v => host.dps.Look = v);
      Check(row, "Session totals", () => host.dps.SessionView, v => host.dps.SessionView = v);
      Check(row, "Targets instead of party", () => host.dps.TargetsView, v => host.dps.TargetsView = v);
    } else if (i == SwgrTools.THREAT) {
      var rs = new string[ThreatPanel.Ranges.Length];
      for (int k = 0; k < rs.Length; k++) rs[k] = ThreatPanel.Ranges[k] + " m";
      Combo(row, "Range", rs, 80, () => Math.Max(0, Array.IndexOf(ThreatPanel.Ranges, host.threat.Range)), v => host.threat.Range = ThreatPanel.Ranges[v]);
      Check(row, "Always visible", () => host.threat.AlwaysVisible, v => host.threat.AlwaysVisible = v);
    } else if (i == SwgrTools.CREDITS) {
      Btn(row, "Reset session", 120, () => host.credits.ResetSession());
    } else if (i == SwgrTools.MOBS) {
      Combo(row, "Show", NearbyMobs.Shows, 130, () => host.mobs.Filter, v => host.mobs.Filter = v);
      Combo(row, "Sort", NearbyMobs.Sorts, 130, () => host.mobs.Sort, v => host.mobs.Sort = v);
      var r2 = Row(20);
      Combo(r2, "Range", NearbyMobs.Ranges.Select(x => x + " m").ToArray(), 80, () => Math.Max(0, Array.IndexOf(NearbyMobs.Ranges, host.mobs.Range)), v => host.mobs.Range = NearbyMobs.Ranges[v]);
      Combo(r2, "Rows", NearbyMobs.RowCounts.Select(x => x.ToString()).ToArray(), 70, () => Math.Max(0, Array.IndexOf(NearbyMobs.RowCounts, host.mobs.MaxRows)), v => host.mobs.MaxRows = NearbyMobs.RowCounts[v]);
      Check(r2, "Always visible", () => host.mobs.AlwaysVisible, v => host.mobs.AlwaysVisible = v);
      return Stack(row, r2);
    } else if (i == SwgrTools.STREAK) {
      var ws = new string[KillStreak.Windows.Length];
      for (int k = 0; k < ws.Length; k++) ws[k] = KillStreak.Windows[k] + " s";
      Combo(row, "Window", ws, 80, () => Math.Max(0, Array.IndexOf(KillStreak.Windows, host.streak.Window)), v => host.streak.Window = KillStreak.Windows[v]);
      var hs = new string[KillStreak.Holds.Length];
      for (int k = 0; k < hs.Length; k++) hs[k] = KillStreak.Holds[k] + " s";
      Combo(row, "Show for", hs, 70, () => Math.Max(0, Array.IndexOf(KillStreak.Holds, host.streak.HoldSeconds)), v => host.streak.HoldSeconds = KillStreak.Holds[v]);
      Check(row, "Unlock to move", () => host.streak.Unlocked, v => { On(i); host.streak.Unlocked = v; });
      var row2 = Row(20);
      Btn(row2, "Test kill", 90, () => { On(i); host.streak.Test(); });
      Btn(row2, "Reset session", 120, () => host.streak.ResetSession());
      Check(row2, "Show the mob's name", () => host.streak.ShowName, v => { host.streak.ShowName = v; On(i); host.streak.Test(); });
      return Stack(row, row2);
    } else if (i == SwgrTools.FLY) {
      Combo(row, "Style", FlyText.Styles, 100, () => host.fly.Style, v => { host.fly.Style = v; On(i); host.fly.Test(); });
      Combo(row, "Motion", FlyText.Motions, 110, () => host.fly.Motion, v => { host.fly.Motion = v; On(i); host.fly.Test(); });
      Check(row, "Unlock to move", () => host.fly.Unlocked, v => { On(i); host.fly.Unlocked = v; });
      var r2 = Row(20);
      Combo(r2, "Weight", FlyText.Weights, 100, () => host.fly.Weight, v => { host.fly.Weight = v; On(i); host.fly.Test(); });
      Combo(r2, "Size", FlyText.SizeNames, 90, () => host.fly.Size, v => { host.fly.Size = v; On(i); host.fly.Test(); });
      Btn(r2, "Test", 80, () => { On(i); host.fly.Test(); });
      var r3 = Row(20);
      for (int z = 0; z < 3; z++) { int zz = z; Check(r3, FlyText.ZoneNames[z], () => host.fly.ZoneOn(zz), v => host.fly.SetZoneOn(zz, v)); }
      var r4 = Row(20);
      Check(r4, "Show names", () => host.fly.ShowNames, v => host.fly.ShowNames = v);
      Check(r4, "Merge DoT ticks", () => host.fly.MergeTicks, v => host.fly.MergeTicks = v);
      Check(r4, "Show misses", () => host.fly.ShowMisses, v => host.fly.ShowMisses = v);
      var all = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = UiLook.Pad(0, 0, 0, 0), BackColor = Back };
      foreach (var r in new[] { row, r2, r3 }) { r.Margin = UiLook.Pad(20, 0, 0, 8); all.Controls.Add(r); }
      all.Controls.Add(r4);
      return all;
    } else return null;
    return row;
  }

  Control BuildAppearance() {
    var col = Column();
    col.Controls.Add(Heading("Appearance"));
    col.Controls.Add(Help("Text size, font and opacity for every overlay. Changes apply right away; the HP overlay shows a 5-second preview."));

    col.Controls.Add(new Label { Text = "Text size", AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = TextColor, Margin = UiLook.Pad(0, 4, 0, 6) });
    var sizes = new FlowLayoutPanel { AutoSize = true, Margin = UiLook.Pad(0, 0, 0, 18), BackColor = Back };
    var sizeButtons = new Button[UiLook.Sizes.Length];
    for (int i = 0; i < UiLook.Sizes.Length; i++) {
      float v = UiLook.Sizes[i];
      var b = sizeButtons[i] = FlatButton((int)Math.Round(v * 100) + "%");
      b.Width = UiLook.D(64);
      b.Margin = UiLook.Pad(0, 0, 6, 0);
      b.Click += (s, e) => { UiLook.PickSize(v); MarkSizes(sizeButtons); };
      sizes.Controls.Add(b);
    }
    MarkSizes(sizeButtons);
    col.Controls.Add(sizes);

    // opacity, per window: pick the window, then its background and overall opacity
    col.Controls.Add(new Label { Text = "Opacity", AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = TextColor, Margin = UiLook.Pad(0, 4, 0, 2) });
    col.Controls.Add(Help("Each window has its own. Background opacity makes only the panel background see-through (text, bars and icons stay solid); overall opacity fades the whole panel. Also on each window's right-click menu."));
    var which = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = UiLook.D(220), Margin = UiLook.Pad(0, 0, 0, 10) };
    which.Items.AddRange(SwgrTools.OverlayNames);
    which.SelectedIndex = 0;
    col.Controls.Add(which);
    var marks = new List<Action>();
    Func<LayeredForm> win = () => host.Panel(which.SelectedIndex);
    col.Controls.Add(new Label { Text = "Background", AutoSize = true, ForeColor = Muted, Margin = UiLook.Pad(0, 0, 0, 4) });
    col.Controls.Add(OpacityRow(UiLook.BackOpacities, () => win().BackOpacity, v => win().SetOpacity(v, win().PanelOpacity), marks));
    col.Controls.Add(new Label { Text = "Overall", AutoSize = true, ForeColor = Muted, Margin = UiLook.Pad(0, 0, 0, 4) });
    col.Controls.Add(OpacityRow(UiLook.PanelOpacities, () => win().PanelOpacity, v => win().SetOpacity(win().BackOpacity, v), marks));
    which.SelectedIndexChanged += (s, e) => { foreach (var m in marks) m(); };
    VisibleChanged += (s, e) => { foreach (var m in marks) m(); }; // a window's right-click menu may have changed it

    col.Controls.Add(new Label { Text = "Font", AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = TextColor, Margin = UiLook.Pad(0, 4, 0, 2) });
    col.Controls.Add(Help("Until you pick one, the HP overlay keeps each style's own fonts. Terminal always stays monospace."));
    var fonts = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = UiLook.D(34), Width = UiLook.D(480), BorderStyle = BorderStyle.None,
      BackColor = Side, ForeColor = TextColor, IntegralHeight = false };
    fonts.Height = fonts.ItemHeight * UiLook.Fonts.Length + UiLook.D(4);
    foreach (var f in UiLook.Fonts) fonts.Items.Add(f[0]);
    if (UiLook.Chosen) fonts.SelectedIndex = UiLook.FontIndex;
    // Each entry is a sample line drawn in its own font
    fonts.DrawItem += (s, e) => {
      if (e.Index < 0) return;
      bool sel = (e.State & DrawItemState.Selected) != 0;
      using (var b = new SolidBrush(sel ? Selected : Side)) e.Graphics.FillRectangle(b, e.Bounds);
      if (sel) using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y, UiLook.D(3), e.Bounds.Height);
      e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      using (var f = UiLook.MakeFont(UiLook.Fonts[e.Index][1], 10.5f))
      using (var b = new SolidBrush(TextColor))
        e.Graphics.DrawString(UiLook.Fonts[e.Index][0] + "   a dune lizard  1,210 / 1,923", f, b, e.Bounds.X + UiLook.D(12), e.Bounds.Y + UiLook.D(7));
    };
    fonts.SelectedIndexChanged += (s, e) => { if (fonts.SelectedIndex >= 0 && (!UiLook.Chosen || fonts.SelectedIndex != UiLook.FontIndex)) UiLook.PickFont(fonts.SelectedIndex); };
    col.Controls.Add(fonts);
    return col;
  }

  // ---------- Value sheet: the 1M+ thresholds the part scanner and verdict panel use ----------

  static readonly string[] TypeKeys = { "reactor", "engine", "shield", "armor", "capacitor", "booster", "weapon" };

  Control BuildValues() {
    var page = new Panel { Dock = DockStyle.Fill, BackColor = Back };
    var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Back };
    head.Controls.Add(Heading("Value sheet"));
    head.Controls.Add(Help("What the part scanner and the verdict panel count as valuable (1M+), per component type and level. " +
      "≥ means at least, ≤ at most. Leave a cell empty for no requirement; a level with no values isn't on the sheet. Changes apply and save right away."));
    var bar = new FlowLayoutPanel { AutoSize = true, BackColor = Back, Margin = UiLook.Pad(0, 0, 0, 10) };
    var type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = UiLook.D(160), BackColor = Selected, ForeColor = TextColor };
    foreach (var t in TypeKeys) type.Items.Add(char.ToUpper(t[0]) + t.Substring(1));
    type.DrawMode = DrawMode.OwnerDrawFixed;
    type.ItemHeight = UiLook.D(22);
    type.DrawItem += (o, e) => { // dark items, like the HP style picker
      if (e.Index < 0) return;
      bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
      using (var br = new SolidBrush(sel ? Hover : Selected)) e.Graphics.FillRectangle(br, e.Bounds);
      TextRenderer.DrawText(e.Graphics, (string)type.Items[e.Index], type.Font, new Rectangle(e.Bounds.X + UiLook.D(6), e.Bounds.Y, e.Bounds.Width - UiLook.D(6), e.Bounds.Height),
        TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    };
    var reset = FlatButton("Reset to defaults");
    reset.Width = UiLook.D(150);
    reset.Margin = UiLook.Pad(12, 0, 0, 0);
    var status = new Label { AutoSize = true, ForeColor = Muted, Margin = UiLook.Pad(14, 7, 0, 0) };
    bar.Controls.Add(type);
    bar.Controls.Add(reset);
    bar.Controls.Add(status);
    head.Controls.Add(bar);

    var grid = new DataGridView {
      Dock = DockStyle.Fill, BackgroundColor = Back, BorderStyle = BorderStyle.None, GridColor = Line, RowHeadersVisible = false,
      AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToOrderColumns = false,
      AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
      ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = UiLook.D(32), MultiSelect = false,
    };
    grid.RowTemplate.Height = UiLook.D(28);
    grid.DefaultCellStyle.BackColor = Side;
    grid.DefaultCellStyle.ForeColor = TextColor;
    grid.DefaultCellStyle.SelectionBackColor = Selected;
    grid.DefaultCellStyle.SelectionForeColor = TextColor;
    grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
    grid.ColumnHeadersDefaultCellStyle.BackColor = Selected;
    grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
    grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Selected;
    grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

    Action showStatus = () => status.Text = Parts.Customised ? "Edited (saved in bin\\swgr-values.cfg)" : "Using the built-in sheet";
    Action fill = () => {
      string key = TypeKeys[type.SelectedIndex];
      grid.Columns.Clear();
      grid.Rows.Clear();
      var level = new DataGridViewTextBoxColumn { HeaderText = "Level", ReadOnly = true, FillWeight = 60 };
      level.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
      level.DefaultCellStyle.ForeColor = Muted;
      grid.Columns.Add(level);
      foreach (var f in Parts.FIELDS[key])
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = f[1] + (Parts.OPS[key][f[0]] == '>' ? "  ≥" : "  ≤"), Tag = f[0], SortMode = DataGridViewColumnSortMode.NotSortable });
      for (int lvl = 1; lvl <= 10; lvl++) {
        var cells = new List<object> { lvl };
        foreach (var f in Parts.FIELDS[key]) {
          var v = Parts.Threshold(key, lvl, f[0]);
          cells.Add(v.HasValue ? v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "");
        }
        grid.Rows.Add(cells.ToArray());
      }
      showStatus();
    };
    Func<object, double?> parse = o => {
      var t = (Convert.ToString(o) ?? "").Replace(",", "").Trim();
      double d;
      if (t.Length == 0) return null;
      return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d >= 0 ? d : double.NaN;
    };
    grid.CellValidating += (o, e) => {
      if (e.ColumnIndex == 0) return;
      var v = parse(e.FormattedValue);
      if (v.HasValue && double.IsNaN(v.Value)) { status.Text = "Enter a number, or leave the cell empty"; e.Cancel = true; }
    };
    grid.CellEndEdit += (o, e) => {
      if (e.ColumnIndex == 0) return;
      var v = parse(grid[e.ColumnIndex, e.RowIndex].Value);
      Parts.SetThreshold(TypeKeys[type.SelectedIndex], e.RowIndex + 1, (string)grid.Columns[e.ColumnIndex].Tag, v);
      grid[e.ColumnIndex, e.RowIndex].Value = v.HasValue ? v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
      showStatus();
    };
    type.SelectedIndexChanged += (o, e) => fill();
    reset.Click += (o, e) => {
      if (MessageBox.Show(this, "Put every threshold back to the built-in 1M+ sheet? Your edits are deleted.", "Reset value sheet",
        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
      Parts.ResetSheet();
      fill();
    };
    type.SelectedIndex = 0;

    page.Controls.Add(grid);
    page.Controls.Add(head);
    return page;
  }

  // A row of percentage buttons; the current one is highlighted
  FlowLayoutPanel OpacityRow(int[] values, Func<int> current, Action<int> pick, List<Action> marks) {
    var row = new FlowLayoutPanel { AutoSize = true, Margin = UiLook.Pad(0, 0, 0, 18), BackColor = Back };
    var buttons = new Button[values.Length];
    Action mark = () => {
      for (int i = 0; i < buttons.Length; i++) {
        bool on = values[i] == current();
        buttons[i].BackColor = on ? Accent : Selected;
        buttons[i].ForeColor = on ? Back : TextColor;
      }
    };
    for (int i = 0; i < values.Length; i++) {
      int v = values[i];
      var b = buttons[i] = FlatButton(v + "%");
      b.Width = UiLook.D(56);
      b.Margin = UiLook.Pad(0, 0, 6, 0);
      b.Click += (o, e) => { pick(v); mark(); };
      row.Controls.Add(b);
    }
    mark();
    marks.Add(mark);
    return row;
  }

  static void MarkSizes(Button[] buttons) {
    for (int i = 0; i < buttons.Length; i++) {
      bool on = Math.Abs(UiLook.Sizes[i] - UiLook.Scale) < 0.01f;
      buttons[i].BackColor = on ? Accent : Selected;
      buttons[i].ForeColor = on ? Back : TextColor;
    }
  }

  internal static Button FlatButton(string text) {
    var b = new Button { Text = text, AutoSize = false, Height = UiLook.D(30), Width = UiLook.D(90), FlatStyle = FlatStyle.Flat, BackColor = Selected, ForeColor = TextColor, Cursor = Cursors.Hand };
    b.FlatAppearance.BorderColor = Line;
    return b;
  }

  // Re-read which overlays are on (they can be turned off from their own menus or the tray)
  internal void SyncOverlays() {
    syncing = true;
    for (int i = 0; i < overlayBoxes.Length; i++) overlayBoxes[i].Checked = host.IsOn(i);
    foreach (var r in refresh) r();
    syncing = false;
  }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    base.OnFormClosing(e);
  }
}

// Sidebar button without the focus rectangle
sealed class NavButton : Button {
  public NavButton() { SetStyle(ControlStyles.Selectable, false); }
  protected override bool ShowFocusCues { get { return false; } }
}

// The inventory report from parts.cs, verdict words coloured like the panel
sealed class ScanPage : UserControl {
  readonly RichTextBox box = new RichTextBox {
    Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false,
    BackColor = ToolsWindow.Side, ForeColor = ToolsWindow.TextColor, Font = new Font("Consolas", 9.5f * UiLook.Dpi), // RichTextBox does not scale its font for DPI like the other controls
  };
  readonly CheckBox every = new CheckBox { Text = "Show every stat", AutoSize = true, ForeColor = ToolsWindow.TextColor, Margin = UiLook.Pad(14, 7, 0, 0) };

  public ScanPage() {
    BackColor = ToolsWindow.Back;
    var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiLook.D(78), BackColor = ToolsWindow.Back, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    head.Controls.Add(new Label { Text = "Part scan", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = ToolsWindow.TextColor });
    var bar = new FlowLayoutPanel { AutoSize = true, BackColor = ToolsWindow.Back, Margin = UiLook.Pad(0, 6, 0, 0) };
    var rescan = ToolsWindow.FlatButton("Rescan");
    rescan.Click += (s, e) => Rescan();
    every.CheckedChanged += (s, e) => Rescan();
    bar.Controls.Add(rescan);
    bar.Controls.Add(every);
    bar.Controls.Add(new Label { Text = "Open your inventory (and any bags) first.", AutoSize = true, ForeColor = ToolsWindow.Muted, Margin = UiLook.Pad(14, 8, 0, 0) });
    head.Controls.Add(bar);
    var gap = new Panel { Dock = DockStyle.Top, Height = UiLook.D(8), BackColor = ToolsWindow.Back };
    Controls.Add(box);
    Controls.Add(gap);
    Controls.Add(head);
  }

  public void Rescan() {
    box.Clear();
    if (!Parts.Alive() && !Parts.Attach()) { Add("SwgClient_r is not running.", null); return; }
    var err = Parts.Report(false, every.Checked, Add);
    if (err != null) Add(err, null);
    box.SelectionStart = 0;
    box.ScrollToCaret();
  }

  void Add(string text, string verdict) {
    if (verdict != null && text.Length >= 15) {
      box.SelectionColor = verdict == "VALUABLE" ? Color.FromArgb(62, 224, 122) : verdict == "NOT VALUABLE" ? Color.FromArgb(255, 92, 92) : Color.FromArgb(242, 184, 59);
      box.AppendText(text.Substring(0, 15));
      text = text.Substring(15);
    }
    box.SelectionColor = box.ForeColor;
    box.AppendText(text + "\n");
  }
}

// The Damage page: every fight this session (newest first) on the left, the selected one in full on the right.
// Same data as the DPS meter panel (dps-meter.cs reads the game's chat log); it polls the log itself, so it keeps
// updating while the panel is turned off.
sealed class DpsPage : Panel {
  static readonly Color Back = ToolsWindow.Back, TextColor = ToolsWindow.TextColor, Muted = ToolsWindow.Muted, Line = ToolsWindow.Line;
  static readonly Color Red = Color.FromArgb(232, 92, 72), Ally = Color.FromArgb(80, 210, 200), Track = Color.FromArgb(38, 44, 52);
  static readonly Color TakenC = Color.FromArgb(255, 120, 110), HealC = Color.FromArgb(110, 220, 130), Tile = Color.FromArgb(26, 32, 40);

  readonly CombatTracker tracker;
  readonly ListBox list = new ListBox { Dock = DockStyle.Left, DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None, IntegralHeight = false,
    BackColor = ToolsWindow.Side, ForeColor = ToolsWindow.TextColor };
  readonly Detail detail;
  readonly Timer timer = new Timer { Interval = 1000 };
  readonly List<FightStats> items = new List<FightStats>();
  readonly Font titleFont = new Font("Segoe UI Semibold", 9.5f), subFont = new Font("Segoe UI", 8.5f);

  public DpsPage(DpsMeter meter) {
    tracker = meter.Tracker;
    BackColor = Back;
    list.Width = UiLook.D(190);
    list.ItemHeight = UiLook.D(40);
    list.DrawItem += DrawItem;
    list.SelectedIndexChanged += (s, e) => detail.Invalidate();

    var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Back };
    head.Controls.Add(new Label { Text = "Damage", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = TextColor, Margin = UiLook.Pad(0, 0, 0, 4) });
    head.Controls.Add(new Label { Text = "Every fight this session from the game's chat log. Needs combat messages on (Options > Chat > Combat Message Filter); set it to Group to compare with your party.",
      AutoSize = true, ForeColor = Muted, MaximumSize = UiLook.Sz(560, 0), Margin = UiLook.Pad(0, 0, 0, 8) });
    var reset = new Button { Text = "Reset session", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = TextColor, BackColor = ToolsWindow.Selected, Margin = UiLook.Pad(0, 0, 0, 10) };
    reset.FlatAppearance.BorderColor = Line;
    reset.Click += (s, e) => { tracker.Reset(); Refill(); };
    head.Controls.Add(reset);

    detail = new Detail(this) { Dock = DockStyle.Fill };
    var gap = new Panel { Dock = DockStyle.Left, Width = UiLook.D(16), BackColor = Back };
    Controls.Add(detail);
    Controls.Add(gap);
    Controls.Add(list);
    Controls.Add(head);

    timer.Tick += (s, e) => { if (!Visible) return; tracker.Poll(); Refill(); };
    timer.Start();
    VisibleChanged += (s, e) => { if (Visible) { tracker.Poll(); Refill(); } };
    Refill();
  }

  // Session first, then the fight in progress, then finished fights newest first. Keeps the selection by fight.
  void Refill() {
    var sel = list.SelectedIndex >= 0 && list.SelectedIndex < items.Count ? items[list.SelectedIndex] : null;
    bool wasSession = list.SelectedIndex <= 0;
    var now = new List<FightStats> { tracker.Session };
    if (tracker.Current != null) now.Add(tracker.Current);
    for (int i = tracker.History.Count - 1; i >= 0; i--) now.Add(tracker.History[i]);
    if (!now.SequenceEqual(items)) {
      items.Clear(); items.AddRange(now);
      list.BeginUpdate();
      list.Items.Clear();
      foreach (var f in items) list.Items.Add(f);
      int idx = sel == null || wasSession ? 0 : items.IndexOf(sel);
      list.SelectedIndex = idx >= 0 ? idx : 0;
      list.EndUpdate();
    }
    list.Invalidate();
    detail.Invalidate();
  }

  internal FightStats Selected { get { int i = list.SelectedIndex; return i >= 0 && i < items.Count ? items[i] : tracker.Session; } }

  double SecondsOf(FightStats f) { return f == tracker.Session ? tracker.SessionSeconds(DateTime.Now) : f == tracker.Current ? tracker.LiveSeconds(DateTime.Now) : f.Seconds; }

  static string Clock(double secs) {
    var t = TimeSpan.FromSeconds(Math.Round(secs));
    return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
  }

  static string Num(double v) {
    if (v >= 1e6) return (v / 1e6).ToString(v >= 1e7 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + "M";
    return Math.Round(v).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
  }

  static string Cap(string s) { return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s; }

  void DrawItem(object sender, DrawItemEventArgs e) {
    if (e.Index < 0 || e.Index >= items.Count) return;
    var f = items[e.Index];
    bool sel = (e.State & DrawItemState.Selected) != 0;
    using (var b = new SolidBrush(sel ? ToolsWindow.Selected : ToolsWindow.Side)) e.Graphics.FillRectangle(b, e.Bounds);
    if (sel) using (var b = new SolidBrush(Red)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y, UiLook.D(3), e.Bounds.Height);
    double secs = SecondsOf(f);
    string title = f == tracker.Session ? "Session" : f == tracker.Current ? "In combat" : f.First.ToString("HH:mm") + "  ·  " + Clock(secs);
    string top = f.ByTarget.Count > 0 ? Cap(f.ByTarget.OrderByDescending(kv => kv.Value).First().Key) : "";
    string sub = f == tracker.Session ? "combat " + Clock(secs) + "  ·  " + Num(f.Dealt / Math.Max(1, secs)) + " dps" : Num(f.Dealt / Math.Max(1, secs)) + " dps  ·  " + top;
    int x = e.Bounds.X + UiLook.D(12), w = e.Bounds.Width - UiLook.D(18);
    TextRenderer.DrawText(e.Graphics, title, titleFont, new Rectangle(x, e.Bounds.Y + UiLook.D(3), w, UiLook.D(18)), f == tracker.Current ? Red : TextColor,
      TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    TextRenderer.DrawText(e.Graphics, sub, subFont, new Rectangle(x, e.Bounds.Y + UiLook.D(20), w, UiLook.D(17)), Muted,
      TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
  }

  // The selected fight in full, painted in design units (pixel fonts, scaled by the DPI factor)
  sealed class Detail : Panel {
    readonly DpsPage page;
    readonly Font big = UiLook.PanelFont("Segoe UI Semibold", 14f), head = UiLook.PanelFont("Segoe UI Semibold", 12f), label = UiLook.PanelFont("Segoe UI", 8.5f),
      row = UiLook.PanelFont("Segoe UI", 9.5f), rowBold = UiLook.PanelFont("Segoe UI Semibold", 9.5f);

    public Detail(DpsPage page) { this.page = page; DoubleBuffered = true; AutoScroll = true; BackColor = Back; }

    static void Txt(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
      using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
      using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
    }

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
      float k = UiLook.Dpi;
      g.ScaleTransform(k, k);
      float W = Math.Max(300, (ClientSize.Width - SystemInformation.VerticalScrollBarWidth) / k);
      var t = page.tracker;
      var f = page.Selected;
      double secs = page.SecondsOf(f);
      float y = 0;

      string title = f == t.Session ? "Session" : f == t.Current ? "In combat" : "Fight " + f.First.ToString("HH:mm:ss") + " – " + f.Last.ToString("HH:mm:ss");
      Txt(g, title, head, new RectangleF(0, y, W - 120, 24), f == t.Current ? Red : TextColor, StringAlignment.Near);
      Txt(g, (f == t.Session ? "combat " : "") + Clock(secs), label, new RectangleF(W - 120, y, 120, 24), Muted, StringAlignment.Far);
      y += 32;

      // four tiles: dps, dealt, taken, healed
      string[] names = { "DPS", "Dealt", "Taken", "Healed" };
      string[] vals = { Num(f.Dealt / Math.Max(1, secs)), Num(f.Dealt), Num(f.Taken), Num(f.Healed) };
      Color[] cols = { TextColor, TextColor, TakenC, HealC };
      float tw = (W - 3 * 8) / 4;
      for (int i = 0; i < 4; i++) {
        var r = new RectangleF(i * (tw + 8), y, tw, 56);
        using (var b = new SolidBrush(Tile)) g.FillRectangle(b, r);
        Txt(g, names[i], label, new RectangleF(r.X + 10, r.Y + 4, r.Width - 14, 16), Muted, StringAlignment.Near);
        Txt(g, vals[i], big, new RectangleF(r.X + 10, r.Y + 20, r.Width - 14, 32), cols[i], StringAlignment.Near);
      }
      y += 66;

      int swings = f.Hits + f.Misses;
      Txt(g, string.Format("Hits {0}    Crits {1}{2}    Blocked {3}    Missed {4}", f.Hits, f.Crits, f.Hits > 0 ? " (" + Math.Round(100.0 * f.Crits / f.Hits) + "%)" : "", f.Blocked, f.Misses),
        row, new RectangleF(0, y, W, 20), TextColor, StringAlignment.Near);
      y += 20;
      Txt(g, "Max hit " + Num(f.MaxHit) + "    Average " + (f.Hits > 0 ? Num((double)f.Dealt / f.Hits) : "-") + (swings > 0 ? "    Accuracy " + Math.Round(100.0 * f.Hits / swings) + "%" : "") +
        "    Hits taken " + f.HitsTaken + (f.BlockedTaken > 0 ? " (" + f.BlockedTaken + " blocked)" : ""), row, new RectangleF(0, y, W, 20), Muted, StringAlignment.Near);
      y += 30;

      var party = f.Party(t.Me);
      if (party.Count > 1) {
        long all = party.Sum(m => m.Dealt);
        y = Section(g, "Party", "dps      share", y, W);
        long best = Math.Max(1, party[0].Dealt);
        foreach (var m in party)
          y = Bar(g, Cap(m.Name) + (m.Me ? "  (you)" : ""), Num(m.Dealt / Math.Max(1, secs)) + "      " + Math.Round(100.0 * m.Dealt / Math.Max(1, all)) + "%",
            (double)m.Dealt / best, m.Me ? Red : Ally, m.Me, y, W);
        y += 10;
      }

      var targets = f.ByTarget.OrderByDescending(kv => kv.Value).ToList();
      y = Section(g, "Targets", "damage      share", y, W);
      if (targets.Count == 0) { Txt(g, t.LogFound ? "No damage dealt yet" : "Chat log not found", row, new RectangleF(0, y, W, 20), Muted, StringAlignment.Near); y += 22; }
      long topT = targets.Count > 0 ? Math.Max(1, targets[0].Value) : 1;
      foreach (var kv in targets)
        y = Bar(g, Cap(kv.Key), Num(kv.Value) + "      " + Math.Round(100.0 * kv.Value / Math.Max(1, f.Dealt)) + "%", (double)kv.Value / topT, Red, false, y, W);

      if (f.ByAbility.Count > 0) {
        y += 10;
        y = Section(g, "Abilities", "damage      share", y, W);
        var abilities = f.ByAbility.OrderByDescending(kv => kv.Value).ToList();
        long topA = Math.Max(1, abilities[0].Value);
        foreach (var kv in abilities)
          y = Bar(g, kv.Key, Num(kv.Value) + "      " + Math.Round(100.0 * kv.Value / Math.Max(1, f.Dealt)) + "%", (double)kv.Value / topA, Ally, false, y, W);
      }

      var min = new Size(0, (int)Math.Ceiling((y + 10) * k));
      if (AutoScrollMinSize != min) AutoScrollMinSize = min;
    }

    float Section(Graphics g, string name, string cols, float y, float W) {
      Txt(g, name, rowBold, new RectangleF(0, y, 200, 20), TextColor, StringAlignment.Near);
      Txt(g, cols, label, new RectangleF(W - 200, y, 196, 20), Muted, StringAlignment.Far);
      using (var p = new Pen(Line)) g.DrawLine(p, 0, y + 21, W, y + 21);
      return y + 26;
    }

    float Bar(Graphics g, string name, string value, double frac, Color c, bool bold, float y, float W) {
      using (var b = new SolidBrush(Track)) g.FillRectangle(b, 0, y + 2, W, 18);
      using (var b = new SolidBrush(Color.FromArgb(bold ? 150 : 100, c))) g.FillRectangle(b, 0, y + 2, (float)(W * Math.Min(1, frac)), 18);
      Txt(g, name, bold ? rowBold : row, new RectangleF(6, y, W - 180, 22), TextColor, StringAlignment.Near);
      Txt(g, value, row, new RectangleF(W - 180, y, 174, 22), TextColor, StringAlignment.Far);
      return y + 23;
    }
  }
}
