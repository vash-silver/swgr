// DPS meter for SWG Restoration ground combat. It reads the game's own chat log file only (no client memory):
// the newest x64\profiles\<account>\<server>\*_chatlog.txt, tailed as the game appends to it.
// Needs the combat messages on (Options > Chat > Combat Message Filter: "Self", or "Group" for the party view); brief or verbose both parse.
// The log writes every combat line twice (once per chat window showing the Combat channel), always as adjacent
// copies, so every second identical line is dropped.
//
// build (standalone): csc -nologo -target:winexe -platform:x64 -optimize -main:DpsMeter -out:../bin/swgr-dps.exe ui.cs dps-meter.cs
// --render out.png draws sample panels; --parse <chatlog> <out.txt> replays a log file and writes every fight's summary.
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

// One parsed combat line
sealed class CombatEvent {
  public enum Kind { Hit, Miss, Heal, Drain }
  public Kind Type;
  public DateTime Time;
  public string Source = "", Target = "", Ability = "";
  public int Amount;
  public bool Crit, Blocked, Glance;
  public string Dot; // a damage-over-time tick: its damage type ("bleeding", "poison", "fire", ...), else null
}

static class CombatParser {
  static readonly Regex line = new Regex(@"^\[Combat\]\s+(\d{1,2}):(\d\d):(\d\d)\s+(.*?)\s*$");
  // brief: "Kira'Dorne crits (blocked) a ronto lair 1314 pts"
  static readonly Regex brief = new Regex(@"^(?<a>.+?) (?<v>hits|crits|glances)(?<b> \(blocked\))? (?<t>.+) (?<n>\d+) pts\.?$");
  // DoT ticks: "X has caused a bantha to take 312 points of bleeding damage. (0 absorbed / 0 resisted. )", and with no
  // source on yourself: "Kira'Dorne has taken 126 points of poison damage. (...)"
  static readonly Regex dot = new Regex(@"^(?<a>.+?) has caused (?<t>.+?) to take (?<n>\d+) points? of (?<k>[a-z ]+?) damage");
  static readonly Regex dotSelf = new Regex(@"^(?<t>.+?) has taken (?<n>\d+) points? of (?<k>[a-z ]+?) damage");
  // "Kira'Dorne drains life from a dewback for 670 points." (also "from corpse of a dewback"): a heal on the source
  static readonly Regex drain = new Regex(@"^(?<a>.+?) drains life from (?:corpse of )?(?<t>.+?) for (?<n>\d+) points?");
  // verbose: "Kira'Dorne uses Advanced Strafe on a ronto for 812 points of damage!" (+ ", but a ronto blocks it!")
  static readonly Regex verbose = new Regex(@"^(?<a>.+?) uses (?<ab>.+?) on (?<t>.+?) (?:and (?<c>critically )?hits )?for (?<n>\d+) points? of damage(?<b>, but .+ blocks it)?");
  static readonly Regex heal = new Regex(@"^(?<a>.+?) heals (?<t>.+?) for (?<n>\d+) points? of damage\.?$");
  static readonly Regex miss = new Regex(@"^(?<a>.+?)(?:'s .+?)? miss(?:es)?(?: (?<t>.+?))?[.!]?$");

  // Names start a sentence capitalised ("A ronto hits ..."), so compare without the first letter's case
  public static string Norm(string s) { s = s.Trim(); return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1); }

  public static CombatEvent Parse(string text, DateTime day) {
    var m = line.Match(text);
    if (!m.Success) return null;
    var t = day.Date.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)).AddSeconds(int.Parse(m.Groups[3].Value));
    if (t > day.AddHours(1)) t = t.AddDays(-1); // a line from just before midnight
    string s = m.Groups[4].Value;
    Match x;
    if ((x = brief.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Hit, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value),
        Amount = int.Parse(x.Groups["n"].Value), Crit = x.Groups["v"].Value == "crits", Glance = x.Groups["v"].Value == "glances", Blocked = x.Groups["b"].Success };
    if ((x = verbose.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Hit, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value),
        Ability = x.Groups["ab"].Value, Amount = int.Parse(x.Groups["n"].Value), Crit = x.Groups["c"].Success, Blocked = x.Groups["b"].Success };
    if ((x = dot.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Hit, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value),
        Amount = int.Parse(x.Groups["n"].Value), Dot = x.Groups["k"].Value };
    if ((x = dotSelf.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Hit, Time = t, Target = Norm(x.Groups["t"].Value), Amount = int.Parse(x.Groups["n"].Value), Dot = x.Groups["k"].Value };
    if ((x = drain.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Drain, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value), Amount = int.Parse(x.Groups["n"].Value) };
    if ((x = heal.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Heal, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value), Amount = int.Parse(x.Groups["n"].Value) };
    if ((x = miss.Match(s)).Success)
      return new CombatEvent { Type = CombatEvent.Kind.Miss, Time = t, Source = Norm(x.Groups["a"].Value), Target = Norm(x.Groups["t"].Value) };
    return null;
  }
}

// Totals for one fight (or the whole session)
sealed class FightStats {
  public DateTime First, Last;
  public long Dealt, Taken, Healed, HealedOthers;
  public int Hits, Crits, Blocked, Misses, MaxHit, HitsTaken, BlockedTaken, Dodged;
  public double CombatSeconds; // session: summed fight durations
  public readonly Dictionary<string, long> ByTarget = new Dictionary<string, long>();
  public readonly Dictionary<string, long> ByAbility = new Dictionary<string, long>();
  // Every hit by attacker and target ("src\u0001tgt" → damage, hits, crits), for the party breakdown
  public readonly Dictionary<string, long[]> Pairs = new Dictionary<string, long[]>();

  // Log timestamps are whole seconds, so a fight lasts at least one
  public double Seconds { get { return CombatSeconds > 0 ? CombatSeconds : Math.Max(1, (Last - First).TotalSeconds + 1); } }
  public double Dps { get { return Dealt / Seconds; } }

  public sealed class Member { public string Name; public long Dealt; public int Hits, Crits; public bool Me; }

  // Your group, from who hits whom (needs the "Group" combat filter, otherwise it's just you). Enemies are what you
  // hit and what hits you; anyone else hitting an enemy, or being hit by one, is on your side (pets included).
  public List<Member> Party(string me) {
    var pairs = Pairs.Select(kv => { var p = kv.Key.Split('\u0001'); return Tuple.Create(p[0], p[1], kv.Value); }).ToList();
    var party = new HashSet<string> { me };
    var enemies = new HashSet<string>();
    for (int pass = 0; pass < 4; pass++)
      foreach (var p in pairs) {
        if (party.Contains(p.Item1) && !party.Contains(p.Item2)) enemies.Add(p.Item2);
        if (party.Contains(p.Item2) && !party.Contains(p.Item1)) enemies.Add(p.Item1);
        if (enemies.Contains(p.Item2) && !enemies.Contains(p.Item1)) party.Add(p.Item1);
        if (enemies.Contains(p.Item1) && !enemies.Contains(p.Item2)) party.Add(p.Item2);
      }
    var members = party.ToDictionary(n => n, n => new Member { Name = n, Me = n == me });
    foreach (var p in pairs)
      if (members.ContainsKey(p.Item1) && enemies.Contains(p.Item2)) {
        var m = members[p.Item1]; m.Dealt += p.Item3[0]; m.Hits += (int)p.Item3[1]; m.Crits += (int)p.Item3[2];
      }
    return members.Values.Where(m => m.Dealt > 0 || m.Me).OrderByDescending(m => m.Dealt).ToList();
  }

  public void Add(CombatEvent e, string me) {
    if (e.Type == CombatEvent.Kind.Hit) {
      long[] pv; string key = e.Source + "\u0001" + e.Target;
      if (!Pairs.TryGetValue(key, out pv)) Pairs[key] = pv = new long[3];
      pv[0] += e.Amount; if (e.Dot == null) pv[1]++; if (e.Crit) pv[2]++;
    }
    bool mine = e.Source == me, onMe = e.Target == me;
    if (e.Type == CombatEvent.Kind.Hit && mine) {
      Dealt += e.Amount; if (e.Dot == null) Hits++; if (e.Crit) Crits++; if (e.Blocked) Blocked++;
      MaxHit = Math.Max(MaxHit, e.Amount);
      long v; ByTarget.TryGetValue(e.Target, out v); ByTarget[e.Target] = v + e.Amount;
      string ab = e.Dot != null ? e.Dot + " (DoT)" : e.Ability;
      if (ab.Length > 0) { ByAbility.TryGetValue(ab, out v); ByAbility[ab] = v + e.Amount; }
    } else if (e.Type == CombatEvent.Kind.Hit && onMe) {
      Taken += e.Amount; if (e.Dot == null) HitsTaken++; if (e.Blocked) BlockedTaken++;
    } else if (e.Type == CombatEvent.Kind.Miss) {
      if (mine) Misses++; else Dodged++; // with the "Self" filter, someone else's miss was aimed at you
    } else if (e.Type == CombatEvent.Kind.Heal) {
      if (onMe) Healed += e.Amount; else if (mine) HealedOthers += e.Amount;
    }
  }

  public bool Counts(CombatEvent e, string me) {
    // what keeps a fight going: any damage in the log (only yours with the "Self" filter, your group's with "Group");
    // heals and misses alone don't
    return e.Type == CombatEvent.Kind.Hit;
  }
}

// Follows the newest chat log and turns it into fights
sealed class CombatTracker {
  public const double IdleSeconds = 10;
  public static string ProfilesDir = @"C:\SWG Restoration\x64\profiles";
  // Set by swgr-tools: your character's name read from the game (Parts.PlayerName). Without it, "you" is whoever
  // deals the most hits, which is right with the "Self" filter but can pick a busy groupmate with "Group".
  public static Func<string> GameName;
  DateTime lastNameRead;
  string gameName;

  string path;
  long pos;
  string partial = "", prevLine;
  bool prevSkipped;
  DateTime lastScan;
  readonly Dictionary<string, int> nameCounts = new Dictionary<string, int>();

  public string Me = "";
  public FightStats Current, LastFight;
  public FightStats Session = new FightStats();
  public readonly List<FightStats> History = new List<FightStats>();
  public bool LogFound { get { return path != null; } }
  // Every parsed combat line, after Me is settled (the flytext overlay listens here)
  public event Action<CombatEvent> Event;

  public void Reset() { Current = null; LastFight = null; Session = new FightStats(); History.Clear(); }

  static string NewestLog() {
    try {
      return new DirectoryInfo(ProfilesDir).GetFiles("*_chatlog.txt", SearchOption.AllDirectories)
        .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
    } catch { return null; }
  }

  // Reads what the game appended since the last call. Starts at the end of the file, so old fights aren't replayed.
  public void Poll() {
    if (path == null || (DateTime.Now - lastScan).TotalSeconds > 10) {
      lastScan = DateTime.Now;
      var newest = NewestLog();
      if (newest != null && newest != path) {
        path = newest; partial = ""; prevLine = null;
        try { pos = new FileInfo(path).Length; } catch { pos = 0; }
      }
    }
    if (path == null) return;
    try {
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
        if (fs.Length < pos) pos = 0; // truncated or replaced
        if (fs.Length == pos) { CloseIfIdle(DateTime.Now); return; }
        fs.Seek(pos, SeekOrigin.Begin);
        var buf = new byte[Math.Min(fs.Length - pos, 4 << 20)];
        int n = fs.Read(buf, 0, buf.Length);
        pos += n;
        Feed(Encoding.UTF8.GetString(buf, 0, n), DateTime.Now);
      }
    } catch { }
    CloseIfIdle(DateTime.Now);
  }

  public void Feed(string text, DateTime now) {
    text = partial + text;
    int cut = text.LastIndexOf('\n');
    partial = cut < 0 ? text : text.Substring(cut + 1);
    if (cut < 0) return;
    foreach (var raw in text.Substring(0, cut).Split('\n')) {
      var l = raw.TrimEnd('\r');
      if (!l.StartsWith("[Combat]")) continue;
      // every line is written twice in a row: drop the second copy (a real repeat comes as another pair)
      if (l == prevLine && !prevSkipped) { prevSkipped = true; continue; }
      prevSkipped = false; prevLine = l;
      var e = CombatParser.Parse(l, now);
      if (e != null) Take(e);
    }
  }

  void Take(CombatEvent e) {
    // fallback for "you": whoever deals the most hits (everyone hits the same mobs, so targets would win a plain count)
    if (e.Type == CombatEvent.Kind.Hit && e.Source.Length > 0) { int c; nameCounts.TryGetValue(e.Source, out c); nameCounts[e.Source] = c + 1; }
    if (GameName != null && (DateTime.Now - lastNameRead).TotalSeconds > 5) {
      lastNameRead = DateTime.Now;
      try { var n = GameName(); if (!string.IsNullOrEmpty(n)) gameName = CombatParser.Norm(n); } catch { }
    }
    if (gameName != null) Me = gameName; else if (nameCounts.Count > 0) Me = nameCounts.OrderByDescending(kv => kv.Value).First().Key;

    if (Current != null && (e.Time - Current.Last).TotalSeconds > IdleSeconds) Close();
    bool counts = Session.Counts(e, Me);
    if (Current == null && !counts) { Session.Add(e, Me); if (Event != null) Event(e); return; } // heals/misses outside a fight: session only
    if (Current == null) Current = new FightStats { First = e.Time, Last = e.Time };
    if (counts) Current.Last = e.Time;
    Current.Add(e, Me);
    Session.Add(e, Me);
    if (Event != null) Event(e);
  }

  public void CloseIfIdle(DateTime now) { if (Current != null && (now - Current.Last).TotalSeconds > IdleSeconds) Close(); }

  void Close() {
    if (Current.Dealt > 0 || Current.Taken > 0) {
      LastFight = Current;
      History.Add(Current);
      Session.CombatSeconds += Current.Seconds;
    }
    Current = null;
  }

  // Session combat time: finished fights plus the one in progress
  public double SessionSeconds(DateTime now) { return Math.Max(1, Session.CombatSeconds + LiveSeconds(now)); }

  // Live duration while a fight is on (the log only updates on events)
  public double LiveSeconds(DateTime now) {
    if (Current == null) return 0;
    return Math.Max(Current.Seconds, Math.Min((now - Current.First).TotalSeconds, (Current.Last - Current.First).TotalSeconds + IdleSeconds));
  }
}

sealed class DpsMeter : LayeredForm {
  const int W = 320, Pad = 10, HeadH = 30, RowH = 17, MaxTargets = 5;

  static readonly Color Back = Color.FromArgb(18, 22, 28), Edge = Color.FromArgb(52, 74, 80), TextColor = Color.FromArgb(235, 238, 242);
  static readonly Color Muted = Color.FromArgb(140, 152, 160), Accent = Color.FromArgb(232, 92, 72), Track = Color.FromArgb(38, 44, 52);
  static readonly Color Crit = Color.FromArgb(255, 196, 92), Taken = Color.FromArgb(255, 120, 110), Heal = Color.FromArgb(110, 220, 130);

  readonly Timer timer = new Timer { Interval = 250 };
  readonly string posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-dps.pos");
  readonly CombatTracker tracker = new CombatTracker();
  internal CombatTracker Tracker { get { return tracker; } } // shared with the Damage page in swgr-tools
  Font nameFont, bigFont, rowFont, smallFont;
  float scale = 1f;
  int designH;
  NotifyIcon tray;
  Point dragFrom;
  readonly bool hosted;
  bool sessionView;
  int look;          // 0 = Questlog (default), 1 = Classic; saved as the third field of swgr-dps.pos
  bool targetsView;  // Questlog style: rows are targets instead of party members
  Font qlBody, qlBold, qlSmall, qlTiny, qlTotal;
  DateTime? renderNow; // --render

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  public DpsMeter(bool hosted = false) {
    this.hosted = hosted;
    Text = "SWGR DPS meter";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Back;
    Location = LoadPos();
    Restyle();
    UiLook.Changed += OnLook;

    UseOpacity("dps");
    var menu = new ContextMenuStrip();
    var view = new ToolStripMenuItem("Show session totals") { CheckOnClick = true };
    view.CheckedChanged += (s, e) => { sessionView = view.Checked; Relayout(); };
    menu.Items.Add(view);
    var styleMenu = new ToolStripMenuItem("Style");
    foreach (var st in new[] { "Questlog", "Classic" }) { int idx = styleMenu.DropDownItems.Count; styleMenu.DropDownItems.Add(new ToolStripMenuItem(st, null, (o, e) => { look = idx; SavePos(); Relayout(); })); }
    styleMenu.DropDownOpening += (o, e) => { for (int i = 0; i < styleMenu.DropDownItems.Count; i++) ((ToolStripMenuItem)styleMenu.DropDownItems[i]).Checked = i == look; };
    menu.Items.Add(styleMenu);
    var tv = new ToolStripMenuItem("Show targets instead of party") { CheckOnClick = true };
    tv.CheckedChanged += (s, e) => { targetsView = tv.Checked; Relayout(); };
    menu.Items.Add(tv);
    menu.Items.Add("Reset", null, (s, e) => { tracker.Reset(); Relayout(); });
    menu.Opening += (s, e) => { view.Checked = sessionView; tv.Checked = targetsView; }; // the tools window may have changed them
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.AddRange(UiLook.MenuItems());
    menu.Items.AddRange(OpacityMenu());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    ContextMenuStrip = menu;
    if (!hosted) {
      var trayMenu = new ContextMenuStrip();
      trayMenu.Items.Add("Close", null, (s, e) => Close());
      tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR DPS meter", ContextMenuStrip = trayMenu, Visible = true };
    }
    timer.Tick += (s, e) => Tick();
    timer.Start();
  }

  void OnLook(bool sizePicked) { Restyle(); }

  void Restyle() {
    foreach (var f in new[] { nameFont, bigFont, rowFont, smallFont, qlBody, qlBold, qlSmall, qlTiny, qlTotal }) if (f != null) f.Dispose();
    // Questlog's sizes in CSS pixels: 13px body (names semibold), text-xs 12px, 10px column labels, text-xl total
    qlBody = UiLook.MakeFont("Encode Sans", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlBold = UiLook.MakeFont("Encode Sans SemiBold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlSmall = UiLook.MakeFont("Encode Sans", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlTiny = UiLook.MakeFont("Encode Sans", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlTotal = UiLook.MakeFont("Encode Sans SemiBold", 18f, FontStyle.Regular, GraphicsUnit.Pixel);
    nameFont = UiLook.PanelFont(UiLook.Strong, 10f);
    bigFont = UiLook.PanelFont(UiLook.Strong, 16f);
    rowFont = UiLook.PanelFont(UiLook.Regular, 8.5f);
    smallFont = UiLook.PanelFont(UiLook.Regular, 7.5f);
    scale = UiLook.Scale;
    Relayout();
  }

  Point LoadPos() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      if (p.Length > 2) look = Math.Max(0, Math.Min(1, int.Parse(p[2])));
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return new Point(wa.Right - W - 40, wa.Top + 300);
  }

  void SavePos() { try { File.WriteAllText(posFile, Left + "," + Top + "," + look); } catch { } }

  // For the swgr-tools main window
  public static readonly string[] Looks = { "Questlog", "Classic" };
  public int Look { get { return look; } set { look = value; SavePos(); Relayout(); } }
  public bool SessionView { get { return sessionView; } set { sessionView = value; Relayout(); } }
  public bool TargetsView { get { return targetsView; } set { targetsView = value; Relayout(); } }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

  DateTime Now { get { return renderNow ?? DateTime.Now; } }

  void Tick() {
    tracker.Poll();
    if (tray != null) tray.Text = tracker.LogFound ? "SWGR DPS meter" : "SWGR DPS meter: chat log not found";
    // Shown once there has been combat this session (the last fight stays up between fights)
    bool show = tracker.Current != null || tracker.LastFight != null || tracker.Session.Dealt > 0;
    if (show != Visible) Visible = show;
    if (!show) return;
    Relayout();
    KeepOnTop();
  }

  FightStats View { get { return sessionView ? tracker.Session : tracker.Current ?? tracker.LastFight ?? tracker.Session; } }

  const int MaxParty = 8;
  static readonly Color Ally = Color.FromArgb(80, 210, 200);

  List<FightStats.Member> PartyRows(FightStats f) { return f.Party(tracker.Me).Take(MaxParty).ToList(); }

  static string Cap(string s) { return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s; }

  List<KeyValuePair<string, long>> Targets(FightStats f) { return f.ByTarget.OrderByDescending(kv => kv.Value).Take(MaxTargets).ToList(); }

  void Relayout() {
    var f = View;
    BackColor = look == 0 ? QlBack : Back;
    if (look == 0) {
      designH = (int)Math.Ceiling(QlTop + Math.Max(1, QlRows(f).Count) * QlRowStep + 8);
      var qs = new Size((int)Math.Ceiling(QW * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
      if (ClientSize != qs) ClientSize = qs;
      Invalidate();
      return;
    }
    int rows = Math.Max(1, Targets(f).Count);
    int party = PartyRows(f).Count;
    designH = HeadH + 40 + 3 * RowH + 18 + rows * RowH + Pad + (party > 1 ? 18 + party * RowH : 0);
    var size = new Size((int)Math.Ceiling(W * scale * UiLook.Dpi), (int)Math.Ceiling(designH * scale * UiLook.Dpi));
    if (ClientSize != size) ClientSize = size;
    Invalidate();
  }

  static string Num(double v) {
    if (v >= 1e6) return (v / 1e6).ToString(v >= 1e7 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "M";
    if (v >= 1e5) return (v / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k";
    return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture);
  }

  static string Clock(double secs) { var t = TimeSpan.FromSeconds(Math.Round(secs)); return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds); }

  static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    if (look == 0) { PaintQuestlog(g); return; }
    using (var p = new Pen(Edge)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);
    using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, 4, designH);

    var f = View;
    bool live = !sessionView && tracker.Current != null;
    double secs = sessionView ? tracker.SessionSeconds(Now) : live ? tracker.LiveSeconds(Now) : f.Seconds;
    double dps = f.Dealt / Math.Max(1, secs);
    string title = sessionView ? "Damage: session" : live ? "Damage: in combat" : tracker.LastFight != null ? "Damage: last fight" : "Damage";
    DrawText(g, title, nameFont, new RectangleF(Pad, 4, W - 2 * Pad - 90, 22), live ? Accent : TextColor, StringAlignment.Near);
    DrawText(g, (sessionView ? "combat " : "") + Clock(secs), smallFont, new RectangleF(W - Pad - 90, 4, 90, 22), Muted, StringAlignment.Far);

    // DPS big on the left, total on the right
    int y = HeadH;
    DrawText(g, Num(dps), bigFont, new RectangleF(Pad, y, 140, 30), TextColor, StringAlignment.Near);
    float dpsW = g.MeasureString(Num(dps), bigFont).Width;
    DrawText(g, "dps", rowFont, new RectangleF(Pad + dpsW, y + 6, 40, 22), Muted, StringAlignment.Near);
    DrawText(g, Num(f.Dealt), nameFont, new RectangleF(W - Pad - 150, y, 150, 18), TextColor, StringAlignment.Far);
    DrawText(g, "damage dealt", smallFont, new RectangleF(W - Pad - 150, y + 16, 150, 14), Muted, StringAlignment.Far);
    y += 40;

    // hits / crits / blocked / missed, max and average, then taken and healing
    int swings = f.Hits + f.Misses;
    string critPct = f.Hits > 0 ? " (" + Math.Round(100.0 * f.Crits / f.Hits) + "%)" : "";
    DrawText(g, string.Format("Hits {0}   Crits {1}{2}   Blocked {3}   Missed {4}", f.Hits, f.Crits, critPct, f.Blocked, f.Misses), rowFont,
      new RectangleF(Pad, y, W - 2 * Pad, RowH), TextColor, StringAlignment.Near);
    y += RowH;
    DrawText(g, "Max hit " + Num(f.MaxHit) + "   Avg " + (f.Hits > 0 ? Num((double)f.Dealt / f.Hits) : "-") +
      (swings > 0 ? "   Accuracy " + Math.Round(100.0 * f.Hits / swings) + "%" : ""), rowFont, new RectangleF(Pad, y, W - 2 * Pad, RowH), Muted, StringAlignment.Near);
    y += RowH;
    DrawText(g, "Taken " + Num(f.Taken) + " (" + Num(f.Taken / Math.Max(1, secs)) + "/s)", rowFont, new RectangleF(Pad, y, 170, RowH), Taken, StringAlignment.Near);
    DrawText(g, "Healed " + Num(f.Healed), rowFont, new RectangleF(Pad + 170, y, W - 2 * Pad - 170, RowH), Heal, StringAlignment.Far);
    y += RowH + 4;

    // you against your group: dps and share of the group's damage (only with the "Group" combat filter)
    var party = PartyRows(f);
    if (party.Count > 1) {
      long all = party.Sum(m => m.Dealt), best = Math.Max(1, party[0].Dealt);
      DrawText(g, "Party", smallFont, new RectangleF(Pad, y, 150, 14), Muted, StringAlignment.Near);
      DrawText(g, "dps     share", smallFont, new RectangleF(W - Pad - 120, y, 120, 14), Muted, StringAlignment.Far);
      y += 14;
      foreach (var m in party) {
        var c = m.Me ? Accent : Ally;
        using (var b = new SolidBrush(Track)) g.FillRectangle(b, Pad, y + 2, W - 2 * Pad, RowH - 4);
        using (var b = new SolidBrush(Color.FromArgb(m.Me ? 140 : 90, c))) g.FillRectangle(b, Pad, y + 2, (float)((W - 2 * Pad) * m.Dealt / (double)best), RowH - 4);
        DrawText(g, Cap(m.Name), m.Me ? nameFont : rowFont, new RectangleF(Pad + 4, y, W - 2 * Pad - 130, RowH), TextColor, StringAlignment.Near);
        DrawText(g, Num(m.Dealt / Math.Max(1, secs)), rowFont, new RectangleF(W - Pad - 124, y, 70, RowH), TextColor, StringAlignment.Far);
        DrawText(g, Math.Round(100.0 * m.Dealt / Math.Max(1, all)) + "%", rowFont, new RectangleF(W - Pad - 50, y, 46, RowH), TextColor, StringAlignment.Far);
        y += RowH;
      }
      y += 4;
    }

    // damage per target, with a bar for the share
    DrawText(g, "Target", smallFont, new RectangleF(Pad, y, 150, 14), Muted, StringAlignment.Near);
    DrawText(g, "Damage", smallFont, new RectangleF(W - Pad - 100, y, 100, 14), Muted, StringAlignment.Far);
    y += 14;
    var targets = Targets(f);
    if (targets.Count == 0) DrawText(g, tracker.LogFound ? "No damage dealt yet" : "Chat log not found", rowFont, new RectangleF(Pad, y, W - 2 * Pad, RowH), Muted, StringAlignment.Near);
    long top = targets.Count > 0 ? targets[0].Value : 1;
    foreach (var kv in targets) {
      using (var b = new SolidBrush(Track)) g.FillRectangle(b, Pad, y + 2, W - 2 * Pad, RowH - 4);
      using (var b = new SolidBrush(Color.FromArgb(90, Accent))) g.FillRectangle(b, Pad, y + 2, (float)((W - 2 * Pad) * kv.Value / (double)top), RowH - 4);
      string name = kv.Key.Length > 0 ? char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1) : kv.Key;
      DrawText(g, name, rowFont, new RectangleF(Pad + 4, y, W - 2 * Pad - 110, RowH), TextColor, StringAlignment.Near);
      DrawText(g, Num(kv.Value) + "  " + Math.Round(100.0 * kv.Value / Math.Max(1, f.Dealt)) + "%", rowFont, new RectangleF(W - Pad - 104, y, 100, RowH), TextColor, StringAlignment.Far);
      y += RowH;
    }
  }

  // ---------- Questlog style ----------
  // After Questlog's Aion 2 damage meter (renderer/aion-2 dps-meter page and its CombatBar component): a 340px rounded
  // gray-800 panel with a gray-500 border, a header with the fight time and the total DPS in the accent colour, tiny
  // column labels, then 30px rows on a gray-700 track. The fill runs from 22% to 42% of the row colour mixed into
  // gray-700, ends in a 2px line of the full colour with a 10px glow, and the text has a 1px black shadow.
  const float QW = 340, QlTop = 52, QlRowH = 30, QlRowStep = 32, MaxQlRows = 8;
  static readonly Color QlBack = Color.FromArgb(18, 18, 18), QlBorder = Color.FromArgb(43, 43, 43), QlTrack = Color.FromArgb(24, 24, 24);
  static readonly Color QlText = Color.FromArgb(250, 250, 250), QlDark = Color.FromArgb(220, 220, 220), QlDarker = Color.FromArgb(175, 175, 175);
  static readonly Color QlAccent = Color.FromArgb(66, 153, 225);
  // you are always the accent blue; everyone else gets a stable colour from their name
  static readonly Color[] QlPalette = {
    Color.FromArgb(72, 187, 120), Color.FromArgb(245, 101, 101), Color.FromArgb(236, 201, 75), Color.FromArgb(159, 122, 234),
    Color.FromArgb(56, 178, 172), Color.FromArgb(237, 137, 54), Color.FromArgb(237, 100, 166),
  };

  protected override float CornerRadius { get { return look == 0 ? 12 * scale * UiLook.Dpi : 0; } }

  sealed class QlRow { public string Name; public long Value; public Color Color; public bool Self; }

  // first letter of the name, skipping an article ("a ronto lair" -> R)
  static string Initial(string name) {
    var n = System.Text.RegularExpressions.Regex.Replace(name, @"^(a|an|the)s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?";
  }

  static Color MemberColor(string name) {
    int h = 0; foreach (char c in name) h = h * 31 + c;
    return QlPalette[(h & 0x7fffffff) % QlPalette.Length];
  }

  List<QlRow> QlRows(FightStats f) {
    if (targetsView)
      return f.ByTarget.OrderByDescending(kv => kv.Value).Take((int)MaxQlRows)
        .Select(kv => new QlRow { Name = Cap(kv.Key), Value = kv.Value, Color = Color.FromArgb(245, 101, 101) }).ToList();
    return f.Party(tracker.Me).Take((int)MaxQlRows)
      .Select(m => new QlRow { Name = Cap(m.Name), Value = m.Dealt, Color = m.Me ? QlAccent : MemberColor(m.Name), Self = m.Me }).ToList();
  }

  // Questlog's formatCombatNumber: plain below 10K, then 12.3K / 1.23M
  static string QlNum(double v) {
    if (v >= 1e6) return (v / 1e6).ToString(v >= 1e7 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "M";
    if (v >= 1e4) return (v / 1e3).ToString(v >= 1e5 ? "0" : "0.0", CultureInfo.InvariantCulture) + "K";
    return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture);
  }

  static Color Mix(Color c, int pct, Color with) {
    return Color.FromArgb((c.R * pct + with.R * (100 - pct)) / 100, (c.G * pct + with.G * (100 - pct)) / 100, (c.B * pct + with.B * (100 - pct)) / 100);
  }

  // text with Questlog's 1px black shadow
  static void QlText2(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    DrawText(g, s, f, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), Color.FromArgb(220, 0, 0, 0), h);
    DrawText(g, s, f, r, c, h);
  }

  void PaintQuestlog(Graphics g) {
    float k = scale * UiLook.Dpi;
    using (var path = RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 12 * k))
    using (var p = new Pen(QlBorder)) g.DrawPath(p, path);
    g.ScaleTransform(k, k);

    var f = View;
    bool live = !sessionView && tracker.Current != null;
    double secs = sessionView ? tracker.SessionSeconds(Now) : live ? tracker.LiveSeconds(Now) : f.Seconds;
    var rows = QlRows(f);

    // header: title and time on the left, total dps on the right
    string title = targetsView ? "Targets" : "Damage";
    float tw = g.MeasureString(title, qlBold).Width;
    DrawText(g, title, qlBold, new RectangleF(12, 6, 120, 24), QlText, StringAlignment.Near);
    bool any = f.Dealt > 0 || live;
    string sub = !any ? "" : (sessionView ? "session " : "") + Clock(secs);
    DrawText(g, sub, qlSmall, new RectangleF(12 + tw, 7, 110, 24), live ? QlAccent : QlDarker, StringAlignment.Near);
    long total = targetsView ? f.Dealt : rows.Sum(r => r.Value);
    if (any) {
      string tot = QlNum(total / Math.Max(1, secs));
      float dw = g.MeasureString("DPS", qlSmall).Width;
      DrawText(g, "DPS", qlSmall, new RectangleF(QW - 12 - dw, 8, dw + 2, 24), QlDarker, StringAlignment.Far);
      DrawText(g, tot, qlTotal, new RectangleF(QW - 160 - dw, 4, 150, 26), QlAccent, StringAlignment.Far);
    }

    // column labels, then the rows
    const float colDmg = 52, colShare = 40, colDps = 58, right = QW - 8 - 10;
    DrawText(g, "Damage", qlTiny, new RectangleF(right - colDmg, 36, colDmg, 14), QlDarker, StringAlignment.Far);
    DrawText(g, "Share", qlTiny, new RectangleF(right - colDmg - colShare, 36, colShare, 14), QlDarker, StringAlignment.Far);
    DrawText(g, "DPS", qlTiny, new RectangleF(right - colDmg - colShare - colDps, 36, colDps, 14), QlDarker, StringAlignment.Far);

    if (rows.Count == 0) {
      DrawText(g, tracker.LogFound ? "Waiting for combat" : "Chat log not found", qlSmall, new RectangleF(16, QlTop, QW - 32, QlRowH), QlDarker, StringAlignment.Near);
      return;
    }
    long top = Math.Max(1, rows.Max(r => r.Value));
    long sum = Math.Max(1, targetsView ? f.Dealt : total);
    float x0 = 8, w = QW - 16;
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      float y = QlTop + i * QlRowStep;
      var rc = new RectangleF(x0, y, w, QlRowH);
      using (var path = RoundRect(rc, 6)) {
        using (var b = new SolidBrush(QlTrack)) g.FillPath(b, path);
        var clip = g.Clip;
        g.SetClip(path);
        float fw = (float)(w * Math.Min(1, (double)r.Value / top));
        if (fw >= 1) {
          var fill = new RectangleF(x0, y, fw, QlRowH);
          using (var lg = new LinearGradientBrush(new RectangleF(x0 - 1, y, fw + 2, QlRowH), Mix(r.Color, 22, QlTrack), Mix(r.Color, 42, QlTrack), LinearGradientMode.Horizontal))
            g.FillRectangle(lg, fill);
          float gx = Math.Max(x0, x0 + fw - 10);
          using (var glow = new LinearGradientBrush(new RectangleF(gx - 1, y, x0 + fw - gx + 2, QlRowH), Color.FromArgb(0, r.Color), Color.FromArgb(89, r.Color), LinearGradientMode.Horizontal))
            g.FillRectangle(glow, gx, y, x0 + fw - gx, QlRowH);
          using (var edge = new SolidBrush(r.Color)) g.FillRectangle(edge, x0 + fw - 2, y, 2, QlRowH);
        }
        g.Clip = clip;
      }

      // badge with the initial (Questlog shows the class icon here)
      var badge = new RectangleF(x0 + 8, y + 5, 20, 20);
      using (var b = new SolidBrush(Mix(r.Color, 70, QlTrack))) g.FillEllipse(b, badge);
      DrawText(g, Initial(r.Name), qlBold, new RectangleF(badge.X, badge.Y + 0.5f, badge.Width, badge.Height), QlText, StringAlignment.Center);

      float nx = x0 + 34;
      if (r.Self) { QlText2(g, "▸", qlSmall, new RectangleF(nx - 1, y, 12, QlRowH), QlText, StringAlignment.Near); nx += 10; }
      float valuesW = colDps + colShare + colDmg;
      QlText2(g, r.Name, qlBold, new RectangleF(nx, y, right - valuesW - nx - 4, QlRowH), QlText, StringAlignment.Near);
      QlText2(g, QlNum(r.Value / Math.Max(1, secs)), qlBold, new RectangleF(right - valuesW, y, colDps, QlRowH), QlText, StringAlignment.Far);
      QlText2(g, Math.Round(100.0 * r.Value / sum) + "%", qlSmall, new RectangleF(right - colDmg - colShare, y, colShare, QlRowH), QlDark, StringAlignment.Far);
      QlText2(g, QlNum(r.Value), qlSmall, new RectangleF(right - colDmg, y, colDmg, QlRowH), QlDark, StringAlignment.Far);
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
    base.OnFormClosed(e);
  }

  // A fight against a ronto lair with a group, built from brief combat lines (each written twice, as the log does)
  internal DateTime LoadSample() {
    var t0 = new DateTime(2026, 10, 31, 23, 26, 0);
    var sb = new StringBuilder();
    var rnd = new Random(7);
    for (int s = 0; s < 42; s++) {
      var ts = t0.AddSeconds(s).ToString("HH:mm:ss");
      Action<string> add = l => { sb.Append("[Combat]  " + ts + " " + l + "\r\n"); sb.Append("[Combat]  " + ts + " " + l + "\r\n"); };
      string tgt = s < 26 ? "a ronto lair" : s < 36 ? "a bull ronto" : "a ronto (scout)";
      int roll = rnd.Next(10);
      if (roll == 0) add("Kira'Dorne misses " + tgt + ".");
      else add("Kira'Dorne " + (roll == 1 ? "crits" : "hits") + (roll == 2 ? " (blocked) " : " ") + tgt + " " + (roll == 1 ? 1300 + rnd.Next(80) : 980 + rnd.Next(90)) + " pts");
      if (s % 4 == 2 && s >= 26) add("A bull ronto hits Kira'Dorne " + (180 + rnd.Next(60)) + " pts");
      if (s % 9 == 5) add("Teammate heals Kira'Dorne for " + (300 + rnd.Next(50)) + " points of damage.");
      if (s % 2 == 0) add("Rabbies hits " + tgt + " " + (1400 + rnd.Next(300)) + " pts");
      if (s % 3 == 1) add("Cynestra Veddar " + (rnd.Next(5) == 0 ? "crits" : "hits") + " " + tgt + " " + (700 + rnd.Next(200)) + " pts");
      if (s % 2 == 1) add("Bolo hits " + tgt + " " + (350 + rnd.Next(100)) + " pts");
      if (s % 5 == 3) add("A ronto hits Rabbies " + (200 + rnd.Next(80)) + " pts");
    }
    CombatTracker.GameName = () => "Kira'Dorne";
    tracker.Feed(sb.ToString(), t0.AddSeconds(50));
    return t0;
  }

  // --render: the sample fight, live, after it ended, and as session totals
  void RenderSample(string file) {
    var t0 = LoadSample();
    var shots = new List<Bitmap>();
    foreach (var v in new[] { 0, 1, 2, 3, 4 }) {
      look = v < 3 ? 0 : 1; targetsView = v == 2;
      // 0: mid-fight (live), 1: last fight after it ended, 2: session view
      if (v == 0) { renderNow = t0.AddSeconds(42); } else { renderNow = t0.AddSeconds(60); tracker.CloseIfIdle(renderNow.Value); }
      sessionView = v == 4;
      Relayout();
      var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
      using (var g = Graphics.FromImage(bmp)) {
        // the background as the layered window draws it (rounded for the Questlog style)
        g.Clear(Color.FromArgb(40, 30, 60)); g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(new RectangleF(0, 0, bmp.Width, bmp.Height), CornerRadius > 0 ? CornerRadius : 0.01f)) using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
        OnPaint(new PaintEventArgs(g, ClientRectangle));
      }
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

  // --parse: replays a whole chat log and writes each fight's summary (for checking the parser against real logs)
  static void ParseFile(string log, string outFile) {
    var tr = new CombatTracker();
    // timestamps carry no date: count days from the file's creation, moving on whenever the clock wraps past midnight
    var day = File.GetCreationTime(log).Date;
    var stamp = new Regex(@"^\[[^\]]*\]\s+(\d{1,2}):(\d\d):(\d\d)");
    TimeSpan prev = TimeSpan.Zero;
    DateTime at = day;
    foreach (var l in File.ReadLines(log)) {
      var m = stamp.Match(l);
      if (m.Success) {
        var t = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        if (t < prev - TimeSpan.FromHours(12)) day = day.AddDays(1);
        prev = t; at = day + t;
      }
      tr.Feed(l + "\n", at);
    }
    tr.CloseIfIdle(at.AddDays(1));
    var o = new StringBuilder();
    o.AppendLine("me: " + tr.Me + "   fights: " + tr.History.Count);
    foreach (var f in tr.History.Concat(new[] { tr.Session })) {
      o.AppendLine(string.Format("{0:HH:mm:ss}-{1:HH:mm:ss} {2,5:0}s  dealt {3,9:N0}  dps {4,7:N0}  hits {5} crits {6} blocked {7} missed {8} max {9}  taken {10:N0} healed {11:N0}  {12}",
        f.First, f.Last, f.Seconds, f.Dealt, f.Dps, f.Hits, f.Crits, f.Blocked, f.Misses, f.MaxHit, f.Taken, f.Healed,
        string.Join(", ", f.ByTarget.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + " " + kv.Value.ToString("N0")))));
      var party = f.Party(tr.Me);
      if (party.Count > 1) o.AppendLine("    party: " + string.Join(", ", party.Select(m => m.Name + " " + m.Dealt.ToString("N0"))));
    }
    File.WriteAllText(outFile, o.ToString());
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render") {
      using (var f = new DpsMeter()) { f.timer.Stop(); f.tray.Visible = false; f.RenderSample(args[1]); }
      return;
    }
    if (args.Length == 3 && args[0] == "--parse") { ParseFile(args[1], args[2]); return; }
    Application.Run(new DpsMeter());
  }
}
