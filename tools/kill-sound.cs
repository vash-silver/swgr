// Kill sound: plays a short sound when a mob you were fighting dies. "Fighting" = it was attacking you, or it was
// your target and damaged, within the last few seconds. Death = health 0 or posture Dead. Reads client memory
// through overlay.cs's GameReader, which opens the process with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only.
// Never add WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess or any other access right.
// The sounds are synthesised here (16-bit mono PCM), no audio files.
//
// build (standalone): cd tools && csc -nologo -target:winexe -platform:x64 -optimize -main:KillSound -out:../bin/swgr-killsound.exe overlay.cs ui.cs kill-sound.cs
// usage: swgr-killsound [--wav <dir>]   (--wav writes every sound as a .wav file and exits)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;

sealed class KillSound : IDisposable {
  public static readonly string[] Names = { "Chime", "Ping", "Coin", "Thud", "Click" };
  public static readonly int[] Volumes = { 25, 50, 75, 100 };
  const double EngagedSeconds = 6; // a mob still counts as yours this long after it stopped attacking you
  const float Range = 80;

  readonly GameReader reader = new GameReader();
  readonly Timer timer = new Timer { Interval = 200 };
  readonly string cfgFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-killsound.cfg");
  readonly Dictionary<ulong, DateTime> engaged = new Dictionary<ulong, DateTime>();
  readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>(); // read while alive
  readonly HashSet<ulong> dead = new HashSet<ulong>(); // already played, so a corpse doesn't ring twice
  SoundPlayer player;
  MemoryStream wav;
  int reconnectTick;
  DateTime lastPlay;

  public string Sound { get; private set; } // null = off
  public int Volume { get; private set; }
  public int Kills { get; private set; }

  // Rising pitch: each kill of a streak a semitone higher (up to an octave). PitchStep gives the streak step (0 = base).
  public Func<int> PitchStep;
  bool rising = true;
  public bool Rising { get { return rising; } set { rising = value; Set(Sound, Volume); } }
  readonly Dictionary<int, SoundPlayer> stepped = new Dictionary<int, SoundPlayer>();

  // Every kill, with the mob's name (the kill banner listens). Fires even with the sound off while Watch is on.
  public event Action<string> Killed;
  bool watch;
  public bool Watch { get { return watch; } set { watch = value; timer.Enabled = Sound != null || watch; } }

  public KillSound() {
    Sound = "Chime"; Volume = 50;
    try {
      foreach (var l in File.ReadAllLines(cfgFile)) {
        var kv = l.Split('=');
        if (kv.Length != 2) continue;
        if (kv[0] == "sound") Sound = Array.IndexOf(Names, kv[1].Trim()) >= 0 ? kv[1].Trim() : null;
        if (kv[0] == "rising") rising = kv[1].Trim() == "1";
        if (kv[0] == "volume") { int v; if (int.TryParse(kv[1], out v)) Volume = Math.Max(5, Math.Min(100, v)); }
      }
    } catch { }
    Load();
    timer.Tick += (s, e) => Tick();
    timer.Enabled = Sound != null;
  }

  public void Set(string sound, int volume) {
    Sound = sound; Volume = volume;
    try { File.WriteAllLines(cfgFile, new[] { "sound=" + (sound ?? "off"), "volume=" + volume, "rising=" + (rising ? 1 : 0) }); } catch { }
    Load();
    timer.Enabled = sound != null || watch;
  }

  public void Play() { if (player != null) try { player.Play(); } catch { } }

  // The sound n semitones up (resampled), built once per step
  void PlayStep(int n) {
    if (n <= 0 || Sound == null) { Play(); return; }
    SoundPlayer sp;
    if (!stepped.TryGetValue(n, out sp)) {
      sp = stepped[n] = new SoundPlayer(new MemoryStream(Wav(Resample(Synth(Sound, Volume / 100.0), Math.Pow(2, n / 12.0)))));
      try { sp.Load(); } catch { }
    }
    try { sp.Play(); } catch { }
  }

  static float[] Resample(float[] a, double factor) {
    var o = new float[(int)(a.Length / factor)];
    for (int i = 0; i < o.Length; i++) {
      double x = i * factor; int k = (int)x; double f = x - k;
      o[i] = (float)(a[k] * (1 - f) + (k + 1 < a.Length ? a[k + 1] : 0) * f);
    }
    return o;
  }

  void Load() {
    foreach (var sp in stepped.Values) sp.Dispose();
    stepped.Clear();
    if (player != null) player.Dispose();
    if (wav != null) wav.Dispose();
    player = null; wav = null;
    if (Sound == null) return;
    wav = new MemoryStream(Wav(Synth(Sound, Volume / 100.0)));
    player = new SoundPlayer(wav);
    try { player.Load(); } catch { }
  }

  void Tick() {
    if (!reader.Connected) { if (++reconnectTick % 10 == 1) reader.Connect(); return; }
    var now = DateTime.Now;
    var ids = reader.ReadEngaged(Range);
    if (ids == null) { engaged.Clear(); return; }
    foreach (var id in ids) if (!dead.Contains(id)) {
      engaged[id] = now;
      if (!names.ContainsKey(id)) { var n = reader.NameOf(id); if (n.Length > 0) names[id] = n; }
    }
    int kills = 0;
    var drop = new List<ulong>();
    foreach (var kv in engaged) {
      int a = reader.CreatureAlive(kv.Key);
      if (a == 0) {
        kills++; dead.Add(kv.Key); drop.Add(kv.Key);
        string n; if (!names.TryGetValue(kv.Key, out n)) n = reader.NameOf(kv.Key);
        if (Killed != null) Killed(n.Length > 0 ? n : "Target");
      }
      else if (a < 0 || (now - kv.Value).TotalSeconds > EngagedSeconds) drop.Add(kv.Key);
    }
    foreach (var id in drop) { engaged.Remove(id); names.Remove(id); }
    if (dead.Count > 500) dead.Clear();
    if (kills > 0) {
      Kills += kills;
      if (Sound != null && (now - lastPlay).TotalMilliseconds > 120) { lastPlay = now; PlayStep(rising && PitchStep != null ? Math.Min(12, PitchStep()) : 0); } // one sound for kills in the same tick
    }
  }

  // Tray / menu items: Off, each sound (picking one plays it), Volume
  public ToolStripMenuItem Menu(string title) {
    var root = new ToolStripMenuItem(title);
    var off = new ToolStripMenuItem("Off", null, (s, e) => Set(null, Volume));
    root.DropDownItems.Add(off);
    var soundItems = new List<ToolStripMenuItem>();
    foreach (var n in Names) {
      string name = n;
      var it = new ToolStripMenuItem(name, null, (s, e) => { Set(name, Volume); Play(); });
      soundItems.Add(it);
      root.DropDownItems.Add(it);
    }
    root.DropDownItems.Add(new ToolStripSeparator());
    var vol = new ToolStripMenuItem("Volume");
    var volItems = new List<ToolStripMenuItem>();
    foreach (var v in Volumes) {
      int val = v;
      var it = new ToolStripMenuItem(val + " %", null, (s, e) => { Set(Sound ?? "Chime", val); Play(); });
      volItems.Add(it);
      vol.DropDownItems.Add(it);
    }
    root.DropDownItems.Add(vol);
    root.DropDownOpening += (s, e) => {
      off.Checked = Sound == null;
      for (int i = 0; i < Names.Length; i++) soundItems[i].Checked = Names[i] == Sound;
      for (int i = 0; i < Volumes.Length; i++) volItems[i].Checked = Volumes[i] == Volume;
    };
    return root;
  }

  public void Dispose() { timer.Dispose(); if (player != null) player.Dispose(); if (wav != null) wav.Dispose(); reader.Dispose(); }

  // --- synthesis ---
  const int Rate = 44100;

  // Each sound is a list of notes: start (s), frequency (Hz), length (s), decay (1/s), and a waveform
  static float[] Synth(string name, double gain) {
    switch (name) {
      case "Chime": return Mix(gain * 0.55, Note(0, 1318.5, 0.35, 9, 0.25), Note(0.075, 1975.5, 0.5, 7, 0.2));
      case "Ping":  return Mix(gain * 0.6, Note(0, 1760, 0.45, 8, 0.15));
      case "Coin":  return Mix(gain * 0.4, Note(0, 987.8, 0.08, 4, 0.9), Note(0.07, 1318.5, 0.3, 10, 0.9));
      case "Thud":  return Mix(gain * 0.9, Drop(0, 160, 70, 0.18, 22));
      default:      return Mix(gain * 0.5, Note(0, 2400, 0.03, 120, 0.1));
    }
  }

  // A sine note with some harmonic (0 = pure sine, 1 = buzzy), a 4 ms attack and exponential decay
  static float[] Note(double start, double hz, double len, double decay, double harm) {
    int off = (int)(start * Rate), n = (int)(len * Rate);
    var b = new float[off + n];
    for (int i = 0; i < n; i++) {
      double t = (double)i / Rate, p = 2 * Math.PI * hz * t;
      double w = Math.Sin(p) + harm * 0.5 * Math.Sin(2 * p) + harm * 0.25 * Math.Sin(3 * p);
      double env = Math.Min(1, t / 0.004) * Math.Exp(-decay * t) * Math.Min(1, (len - t) / 0.01);
      b[off + i] = (float)(w * env / (1 + harm * 0.75));
    }
    return b;
  }

  // A sine whose pitch slides from hz0 to hz1 (a soft low thump)
  static float[] Drop(double start, double hz0, double hz1, double len, double decay) {
    int off = (int)(start * Rate), n = (int)(len * Rate);
    var b = new float[off + n];
    double phase = 0;
    for (int i = 0; i < n; i++) {
      double t = (double)i / Rate, hz = hz1 + (hz0 - hz1) * Math.Exp(-t * 30);
      phase += 2 * Math.PI * hz / Rate;
      double env = Math.Min(1, t / 0.003) * Math.Exp(-decay * t) * Math.Min(1, (len - t) / 0.01);
      b[off + i] = (float)(Math.Sin(phase) * env);
    }
    return b;
  }

  static float[] Mix(double gain, params float[][] parts) {
    int n = 0;
    foreach (var p in parts) n = Math.Max(n, p.Length);
    var o = new float[n];
    foreach (var p in parts) for (int i = 0; i < p.Length; i++) o[i] += p[i];
    for (int i = 0; i < n; i++) o[i] = (float)Math.Max(-1, Math.Min(1, o[i] * gain));
    return o;
  }

  static byte[] Wav(float[] s) {
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    int bytes = s.Length * 2;
    w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
    w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
    foreach (var v in s) w.Write((short)(v * 32767));
    w.Flush();
    return ms.ToArray();
  }

  // --- standalone ---
  [STAThread]
  static void Main(string[] args) {
    if (args.Length == 2 && args[0] == "--wav") {
      Directory.CreateDirectory(args[1]);
      foreach (var n in Names) File.WriteAllBytes(Path.Combine(args[1], "kill-" + n.ToLowerInvariant() + ".wav"), Wav(Synth(n, 0.75)));
      return;
    }
    Application.EnableVisualStyles();
    var ks = new KillSound();
    var menu = new ContextMenuStrip();
    menu.Items.Add(ks.Menu("Kill sound"));
    menu.Items.Add("Close", null, (s, e) => Application.Exit());
    var tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SWGR kill sound", ContextMenuStrip = menu, Visible = true };
    Application.Run();
    tray.Visible = false;
    ks.Dispose();
  }
}
