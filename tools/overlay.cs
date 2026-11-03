// Target HP overlay for the SWG Restoration DX12 client. Read-only.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only, so it cannot write,
// inject, suspend or debug. This file must never call WriteProcessMemory, VirtualProtectEx,
// CreateRemoteThread, DebugActiveProcess or open the process with any other access right.
//
// build: csc -nologo -target:winexe -platform:x64 -optimize -out:../bin/swgr-overlay.exe overlay.cs ui.cs
// Offsets are documented in notes/memory.md and are specific to the current client build.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Offsets {
  public const ulong PlayerGlobal = 0x21f22c0;   // RVA of a global CreatureObject* (the player)
  public const ulong CreatureVtable = 0x1cf7648; // RVA of CreatureObject's vtable
  public const ulong Target = 0xd18;             // CreatureObject* of the current target
  public const ulong Name = 0x280;               // std::wstring display name
  public const ulong CurHam = 0xa00;             // int* current health/_/action/_/mind
  public const ulong MaxHam = 0xa70;             // int* max health/_/action/_/mind
  public const ulong Level = 0x1320;             // int16 combat level
  public const ulong PosX = 0x84, PosY = 0x94, PosZ = 0xa4; // floats: translation column of the object-to-parent transform
  public const ulong Buffs = 0x1508;             // std::map<uint32 crc, PackedBuff> head pointer (size at +8)
  public const ulong StringIdTable = 0x368, StringIdKey = 0x388; // std::string name StringId (table, key)
  public const ulong ContainedBy = 0xf8, ContainedByObj = 0x28;  // ContainedByProperty* → container Object* (the pilot's ship)
  public const ulong Template = 0x18, TemplatePath = 0x20;      // ObjectTemplate* → char* path ("object/creature/player/..." for players)
  // Module-global std::vector<Object*> (begin, end) of the creatures (and a few tangibles) within ~120 m of you.
  // A creature's Target (+0xd18) is its combat target, so a mob pointing at you is on you (notes/memory.md → "Aggro").
  public const ulong NearbyCreatures = 0x22d85d0;

  // ShipObject (space targets; notes/memory.md → "Ships"). AutoDelta floats hold their value at +0x18 of the variable.
  // Lairs and other tangibles (InstallationObject, e.g. turrets, derives from it): HP = max - damage
  public const ulong TangibleVtable = 0x1cf68c8, InstallationVtable = 0x1d40ca8;
  public const ulong DamageTaken = 0x600, MaxHitPoints = 0x630;

  public const ulong ShipVtable = 0x1cfc748;
  public const ulong ChassisCur = 0x9e8, ChassisMax = 0xa08;
  public const ulong ShieldFrontMax = 0x14c0, ShieldBackMax = 0x14e0, ShieldFrontCur = 0x1500, ShieldBackCur = 0x1520;
  // AutoDelta std::map<int slot, float> objects: head at +0x18, size at +0x20; node key at +0x1c, value at +0x20
  public const ulong ArmorMax = 0xbb0, ArmorCur = 0xc18, HpCur = 0xe80, HpMax = 0xee8;
  public const ulong MobileType = 0x1ac8, Difficulty = 0x1b00; // std::string keys: space/space_mobile_type, space/space_difficulty
  public const ulong SubTargetSlot = 0x1998; // int on the player's own ship: targeted component slot of the target, 115 = none
}

sealed class GameReader : IDisposable {
  const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;

  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte[] buf, IntPtr size, out IntPtr read);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int needed, uint filter);

  IntPtr h;
  ulong baseAddr;
  int failures;

  public bool Connected { get { return h != IntPtr.Zero; } }

  public bool Connect() {
    if (Connected) return true;
    var procs = Process.GetProcessesByName("SwgClient_r");
    if (procs.Length == 0) return false;
    h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, procs[0].Id);
    if (h == IntPtr.Zero) return false;
    var mods = new IntPtr[1];
    int needed;
    if (!EnumProcessModulesEx(h, mods, IntPtr.Size, out needed, 0x03)) { Dispose(); return false; }
    baseAddr = (ulong)mods[0].ToInt64();
    failures = 0;
    return true;
  }

  public void Dispose() {
    if (h != IntPtr.Zero) CloseHandle(h);
    h = IntPtr.Zero;
  }

  byte[] Read(ulong addr, int len) {
    if (addr < 0x10000 || addr > 0x7fffffffffff) return null;
    var b = new byte[len];
    IntPtr n;
    return ReadProcessMemory(h, addr, b, (IntPtr)len, out n) && (int)n == len ? b : null;
  }

  ulong Ptr(ulong addr) { var b = Read(addr, 8); return b == null ? 0 : BitConverter.ToUInt64(b, 0); }

  bool IsCreature(ulong obj) { return obj != 0 && Ptr(obj) == baseAddr + Offsets.CreatureVtable; }

  string WStr(ulong addr) {
    var b = Read(addr, 128);
    if (b == null) return null;
    var s = Encoding.Unicode.GetString(b);
    int z = s.IndexOf('\0');
    return (z >= 0 ? s.Substring(0, z) : s).Trim();
  }

  // MSVC std::string: 16-byte buffer or pointer, size at +0x10, capacity at +0x18
  string StdStr(byte[] b, int off) {
    long len = BitConverter.ToInt64(b, off + 0x10), cap = BitConverter.ToInt64(b, off + 0x18);
    if (len <= 0 || len > 256 || cap < len) return "";
    var raw = cap < 16 ? b : Read(BitConverter.ToUInt64(b, off), (int)len);
    return raw == null ? "" : Encoding.ASCII.GetString(raw, cap < 16 ? off : 0, (int)len);
  }

  // MSVC std::wstring: 8-wchar buffer or pointer, size at +0x10, capacity at +0x18
  string StdWStr(byte[] b, int off) {
    long len = BitConverter.ToInt64(b, off + 0x10), cap = BitConverter.ToInt64(b, off + 0x18);
    if (len <= 0 || len > 256 || cap < len) return "";
    var raw = cap < 8 ? b : Read(BitConverter.ToUInt64(b, off), (int)len * 2);
    return raw == null ? "" : Encoding.Unicode.GetString(raw, cap < 8 ? off : 0, (int)len * 2);
  }

  // Per-buff effects chosen at cast time (build-a-buff, city buffs): vector at node +0x48/+0x50 of 0x90-byte entries,
  // each { string name, string table (+0x20), string text key (+0x40), ..., float value (+0x68) (%DI/%DF),
  // wstring text (+0x70) (%TT, e.g. the city name) }
  List<string> DynamicEffects(byte[] node) {
    var lines = new List<string>();
    ulong beg = BitConverter.ToUInt64(node, 0x48), end = BitConverter.ToUInt64(node, 0x50);
    if (beg == 0 || end <= beg || (end - beg) % 0x90 != 0 || end - beg > 0x90 * 16) return lines;
    var v = Read(beg, (int)(end - beg));
    if (v == null) return lines;
    for (int off = 0; off < v.Length; off += 0x90) {
      string text = BuffBook.Text(StdStr(v, off + 0x20), StdStr(v, off + 0x40));
      if (text != null) lines.Add(BuffBook.Fill(text, BitConverter.ToSingle(v, off + 0x68)).Replace("%TT", StdWStr(v, off + 0x70)));
    }
    return lines;
  }

  // Buffs of a CreatureObject: in-order walk of the std::map at +0x1508. Node: key crc at +0x20, then PackedBuff:
  // end time (game seconds, 0xffffffff = permanent) +0x28, duration +0x30, stack count +0x40, dynamic effects +0x48.
  List<ActiveBuff> ReadBuffs(ulong obj) {
    var list = new List<ActiveBuff>();
    ulong head = Ptr(obj + Offsets.Buffs);
    var hb = Read(head, 0x20);
    if (hb == null || hb[0x19] != 1) return list;
    var stack = new Stack<ulong>();
    ulong n = BitConverter.ToUInt64(hb, 8);
    int guard = 0;
    while (guard++ < 1000) {
      if (n != head && n != 0) {
        var b = Read(n, 0x20);
        if (b == null || b[0x19] > 1) return list;
        if (b[0x19] == 0) { stack.Push(n); n = BitConverter.ToUInt64(b, 0); continue; }
      }
      if (stack.Count == 0) break;
      n = stack.Pop();
      var nb = Read(n, 0x58);
      if (nb == null) break;
      list.Add(new ActiveBuff { Crc = BitConverter.ToUInt32(nb, 0x20), End = BitConverter.ToUInt32(nb, 0x28), Duration = BitConverter.ToUInt32(nb, 0x30), Stacks = BitConverter.ToInt32(nb, 0x40), Dynamic = DynamicEffects(nb) });
      n = BitConverter.ToUInt64(nb, 0x10);
    }
    return list;
  }

  // Game clock. End times are in server game seconds, and the client's clock isn't stored anywhere readable, so it is
  // calibrated: when a buff appears on an object watched in the previous tick, its end - duration is the game time now.
  // offset = unix seconds - game seconds; saved in swgr-clock.cfg and dropped if an active buff contradicts it.
  static readonly string clockFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-clock.cfg");
  // Second line of the file: the player's timed buffs as crc:end, from the session the offset belongs to.
  // Third line: unix time a character was last seen loaded. The game clock can pause while you're logged out (buff
  // end times stay put and their time left is kept), so after a gap the offset is retried shifted by the gap.
  static readonly Dictionary<uint, uint> lastEnd = new Dictionary<uint, uint>();
  static long lastSeen, lastSeenSaved, resumeGap, loadedAt;
  static long clockOffset = LoadClock();
  readonly Dictionary<ulong, HashSet<ulong>> seen = new Dictionary<ulong, HashSet<ulong>>();

  static long LoadClock() {
    try {
      var lines = File.ReadAllLines(clockFile);
      if (lines.Length > 1)
        foreach (var kv in lines[1].Split(',')) {
          var p = kv.Split(':');
          if (p.Length == 2) lastEnd[Convert.ToUInt32(p[0], 16)] = uint.Parse(p[1]);
        }
      if (lines.Length > 2) lastSeen = lastSeenSaved = long.Parse(lines[2].Trim());
      return long.Parse(lines[0].Trim());
    } catch { return 0; }
  }
  static void SaveClock() {
    var parts = new List<string>();
    foreach (var kv in lastEnd) parts.Add(kv.Key.ToString("x8") + ":" + kv.Value);
    lastSeenSaved = lastSeen;
    try { File.WriteAllLines(clockFile, new[] { clockOffset.ToString(), string.Join(",", parts), lastSeen.ToString() }); } catch { }
  }
  static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

  // Called every tick a character is loaded: notes a gap since the last one (logout, or the tools weren't running)
  static void Seen() {
    long now = UnixNow();
    if (lastSeen == 0 || now - lastSeen > 20) loadedAt = now; // first tick of a session or after a logout/loading screen
    if (lastSeen != 0 && now - lastSeen > 20) resumeGap = now - lastSeen;
    lastSeen = now;
    if (clockOffset != 0 && now - lastSeenSaved >= 15) SaveClock();
  }

  // The server re-bases buff end times when the character logs back in (the time left is kept while offline), so
  // the game clock jumps against the wall clock. A persisting buff whose end moved, without looking freshly cast,
  // shows the jump: shift the offset by the same amount (the time left stays the same). When several buffs moved,
  // the delta most of them agree on wins (a recast one disagrees).
  // Only right after a (re)load: during play a moved end time is a recast, and with a slightly-off offset a recast
  // doesn't look fresh, so treating it as a re-base once drifted the clock by hours.
  void TrackRebase(List<ActiveBuff> own) {
    if (own.Count == 0) return; // transient empty read: keep the snapshot
    bool justLoaded = UnixNow() - loadedAt <= 60;
    var deltas = new List<long>();
    var now = new Dictionary<uint, uint>();
    foreach (var b in own) {
      if (b.End == 0xffffffff || b.Duration == 0) continue;
      now[b.Crc] = b.End;
      uint old;
      if (!lastEnd.TryGetValue(b.Crc, out old) || old == b.End) continue;
      bool freshCast = clockOffset != 0 && Math.Abs(((long)b.End - b.Duration) - (UnixNow() - clockOffset)) <= 10;
      if (!freshCast) deltas.Add((long)b.End - old);
    }
    bool changed = now.Count != lastEnd.Count;
    foreach (var kv in now) { uint o; if (!lastEnd.TryGetValue(kv.Key, out o) || o != kv.Value) changed = true; }
    if (clockOffset != 0 && justLoaded && deltas.Count > 0) {
      long best = 0; int bestVotes = 0, tie = 0;
      foreach (var d in deltas) {
        int votes = 0;
        foreach (var e in deltas) if (Math.Abs(d - e) <= 2) votes++;
        if (votes > bestVotes) { best = d; bestVotes = votes; tie = 0; } else if (votes == bestVotes && Math.Abs(d - best) > 2) tie++;
      }
      if (tie == 0 && Fits(clockOffset - best, own)) clockOffset -= best;
    }
    if (changed) { lastEnd.Clear(); foreach (var kv in now) lastEnd[kv.Key] = kv.Value; }
    if (changed && clockOffset != 0) SaveClock();
  }

  // Remaining seconds, or -1 when permanent or the clock isn't known yet
  public static long Remaining(ActiveBuff b) {
    if (b.End == 0xffffffff || b.Duration == 0 || clockOffset == 0) return -1;
    long rem = (long)b.End - (UnixNow() - clockOffset);
    if (rem > b.Duration + 5) return -1; // not on this clock (e.g. a target's buff): no timer rather than a wrong one
    return Math.Max(0, rem);
  }

  // True when every timed buff's remaining time under this offset lies within [0, duration]
  static bool Fits(long offset, List<ActiveBuff> buffs) {
    foreach (var b in buffs) {
      if (b.End == 0xffffffff || b.Duration == 0) continue;
      long rem = (long)b.End - (UnixNow() - offset);
      if (rem > b.Duration + 5 || rem < -30) return false;
    }
    return true;
  }

  // Buffs restored in bulk (zoning, relogging, re-targeting, a transient empty read) also look "new", and treating one
  // of those as freshly cast is what once put a 5.5 h duration on a buff with 2.5 h left. So a sample only counts when
  // the object was already watched with buffs, at most two appear while at least one stays, and the result fits every
  // active buff. A known offset is only replaced once an active buff contradicts it (e.g. after a server restart).
  void Calibrate(ulong obj, List<ActiveBuff> buffs) {
    HashSet<ulong> before;
    bool watched = seen.TryGetValue(obj, out before);
    var now = new HashSet<ulong>();
    var fresh = new List<ActiveBuff>();
    foreach (var b in buffs) {
      ulong key = ((ulong)b.Crc << 32) | b.End;
      now.Add(key);
      if (watched && !before.Contains(key) && b.End != 0xffffffff && b.Duration > 0) fresh.Add(b);
    }
    seen[obj] = now;
    bool timed = buffs.Exists(b => b.End != 0xffffffff && b.Duration > 0);
    if (clockOffset != 0 && !Fits(clockOffset, buffs)) {
      // The clock paused for the gap (logged out): shift by it if that fits. Otherwise the clock really moved.
      if (resumeGap > 0 && Fits(clockOffset + resumeGap, buffs)) { clockOffset += resumeGap; SaveClock(); }
      else clockOffset = 0;
      resumeGap = 0;
    } else if (clockOffset != 0 && timed) resumeGap = 0; // the offset still fits: the clock kept running
    if (fresh.Count == 0 || fresh.Count > 2 || before.Count == 0 || !now.Overlaps(before)) return;
    long offset = UnixNow() - ((long)fresh[0].End - fresh[0].Duration);
    if ((clockOffset != 0 && Math.Abs(offset - clockOffset) > 10) || !Fits(offset, buffs)) return; // small refinements only
    clockOffset = offset;
    SaveClock();
  }

  readonly Dictionary<ulong, bool> playerChar = new Dictionary<ulong, bool>();

  // A player character (not an NPC or a pet): its template is under object/creature/player/. Cached per object.
  bool IsPlayerChar(ulong obj) {
    bool v;
    if (playerChar.TryGetValue(obj, out v)) return v;
    var b = Read(Ptr(Ptr(obj + Offsets.Template) + Offsets.TemplatePath), 23);
    v = b != null && Encoding.ASCII.GetString(b) == "object/creature/player/";
    if (playerChar.Count > 2000) playerChar.Clear();
    return playerChar[obj] = v;
  }

  public string NameOf(ulong t) { return Connected && IsCreature(t) ? CreatureName(t) : ""; }

  string CreatureName(ulong t) {
    var nb = Read(t + Offsets.Name, 0x20);
    var name = nb == null ? "" : StdWStr(nb, 0); // short names (< 8 chars) sit inline, longer ones behind a pointer
    if (name.Length == 0) name = OverlayForm.StripColors(BuffBook.Text(Std(t + Offsets.StringIdTable), Std(t + Offsets.StringIdKey))); // generic mobs
    return name;
  }

  // Mobs within range that are attacking a player character (you or anyone else), or null when not in game.
  // There's no group list yet, so "anyone else" includes strangers: the range keeps their fights out.
  public List<Threat> ReadThreats(float range) {
    if (!Connected) return null;
    ulong player = Ptr(baseAddr + Offsets.PlayerGlobal);
    if (!IsCreature(player)) return null;
    ulong begin = Ptr(baseAddr + Offsets.NearbyCreatures), end = Ptr(baseAddr + Offsets.NearbyCreatures + 8);
    var list = new List<Threat>();
    if (end <= begin || end - begin > 8 * 4000) return list;
    var arr = Read(begin, (int)(end - begin));
    if (arr == null) return list;
    for (int i = 0; i + 8 <= arr.Length; i += 8) {
      ulong o = BitConverter.ToUInt64(arr, i);
      if (o == player || !IsCreature(o)) continue;
      ulong t = Ptr(o + Offsets.Target);
      if (t == 0 || t == o) continue;
      bool onYou = t == player;
      if (!onYou && !(IsCreature(t) && IsPlayerChar(t))) continue;
      if (IsPlayerChar(o)) continue; // players fighting players (duels, PvP) aren't threat
      float d = Distance(player, o);
      if (d < 0 || d > range) continue;
      var cur = Read(Ptr(o + Offsets.CurHam), 4); var max = Read(Ptr(o + Offsets.MaxHam), 4);
      if (cur == null || max == null || BitConverter.ToInt32(cur, 0) <= 0) continue; // dead
      list.Add(new Threat { Id = o, Name = CreatureName(o), OnYou = onYou, Target = onYou ? null : CreatureName(t), Distance = d,
        Health = BitConverter.ToInt32(cur, 0), HealthMax = BitConverter.ToInt32(max, 0) });
    }
    // Loose ones first (they need a taunt), then the closest
    list.Sort((a, b) => a.OnYou != b.OnYou ? (a.OnYou ? 1 : -1) : a.Distance.CompareTo(b.Distance));
    return list;
  }

  // NPCs you're fighting (kill-sound.cs): every live mob within range attacking you, plus your target if it's a
  // damaged NPC. Null when not in game.
  public List<ulong> ReadEngaged(float range) {
    if (!Connected) return null;
    ulong player = Ptr(baseAddr + Offsets.PlayerGlobal);
    if (!IsCreature(player)) return null;
    var ids = new List<ulong>();
    ulong tgt = Ptr(player + Offsets.Target);
    if (tgt != player && IsCreature(tgt) && !IsPlayerChar(tgt)) {
      var cur = Read(Ptr(tgt + Offsets.CurHam), 4); var max = Read(Ptr(tgt + Offsets.MaxHam), 4);
      if (cur != null && max != null) { int c = BitConverter.ToInt32(cur, 0); if (c > 0 && c < BitConverter.ToInt32(max, 0)) ids.Add(tgt); }
    }
    var th = ReadThreats(range);
    if (th != null) foreach (var x in th) if (x.OnYou && !ids.Contains(x.Id)) ids.Add(x.Id);
    return ids;
  }

  // Every live NPC/creature within range (mobs-panel.cs): name, level, health, distance, and whom it's attacking.
  // Players are left out. Null when not in game.
  public List<NearbyMob> ReadNearby(float range) {
    if (!Connected) return null;
    ulong player = Ptr(baseAddr + Offsets.PlayerGlobal);
    if (!IsCreature(player)) return null;
    ulong mine = Ptr(player + Offsets.Target);
    ulong begin = Ptr(baseAddr + Offsets.NearbyCreatures), end = Ptr(baseAddr + Offsets.NearbyCreatures + 8);
    var list = new List<NearbyMob>();
    if (end <= begin || end - begin > 8 * 4000) return list;
    var arr = Read(begin, (int)(end - begin));
    if (arr == null) return list;
    for (int i = 0; i + 8 <= arr.Length; i += 8) {
      ulong o = BitConverter.ToUInt64(arr, i);
      if (o == player || !IsCreature(o) || IsPlayerChar(o)) continue;
      float d = Distance(player, o);
      if (d < 0 || d > range) continue;
      var cur = Read(Ptr(o + Offsets.CurHam), 4); var max = Read(Ptr(o + Offsets.MaxHam), 4);
      if (cur == null || max == null) continue;
      int hc = BitConverter.ToInt32(cur, 0), hm = BitConverter.ToInt32(max, 0);
      if (hc <= 0 || hm <= 0) continue; // dead
      ulong t = Ptr(o + Offsets.Target);
      bool onYou = t == player, onOther = !onYou && t != 0 && t != o && IsCreature(t) && IsPlayerChar(t);
      var lvl = Read(o + Offsets.Level, 2);
      list.Add(new NearbyMob { Id = o, Name = CreatureName(o), Level = lvl == null ? 0 : BitConverter.ToInt16(lvl, 0), Health = hc, HealthMax = hm, Distance = d,
        OnYou = onYou, OnOther = onOther ? CreatureName(t) : null, IsTarget = o == mine });
    }
    return list;
  }

  // 1 alive, 0 dead (health 0 or posture 14 Dead at +0x988, notes/memory.md), -1 gone or not a creature any more
  public int CreatureAlive(ulong o) {
    if (!Connected || !IsCreature(o)) return -1;
    var cur = Read(Ptr(o + Offsets.CurHam), 4);
    var pos = Read(o + 0x988, 1);
    if (cur == null) return -1;
    return BitConverter.ToInt32(cur, 0) <= 0 || (pos != null && pos[0] == 14) ? 0 : 1;
  }

  // Returns null when there is no (valid) target. Drops the handle if the game has gone away.
  // self: read your own character instead of your target (the self overlay)
  public TargetInfo ReadTarget(bool self = false) {
    if (!Connected) return null;
    ulong player = Ptr(baseAddr + Offsets.PlayerGlobal);
    if (!IsCreature(player)) {
      if (Read(baseAddr, 2) == null && ++failures > 20) Dispose(); // module unreadable: process exited
      return null;
    }
    failures = 0;
    Seen();
    if (seen.Count > 8) seen.Clear();
    var own = ReadBuffs(player);
    TrackRebase(own);
    Calibrate(player, own);
    ulong t = self ? player : Ptr(player + Offsets.Target);
    if (IsShip(t)) return ReadShip(player, t);
    if (IsTangible(t)) return ReadTangible(player, t);
    if (!IsCreature(t)) return null;
    var buffs = own;
    if (t != player) buffs = ReadBuffs(t); // calibrate from your own buffs only: a target's once gave an offset hours off
    var cur = Read(Ptr(t + Offsets.CurHam), 20);
    var max = Read(Ptr(t + Offsets.MaxHam), 20);
    if (cur == null || max == null) return null;
    var name = CreatureName(t);
    var lvl = Read(t + Offsets.Level, 2);
    // Aggro: whom your target is attacking (NPC targets only), or for the self overlay how many mobs are on you
    bool onYou = false; string onOther = null; int onYouCount = 0;
    if (self) { var th = ReadThreats(100); if (th != null) foreach (var x in th) if (x.OnYou) onYouCount++; }
    else if (!IsPlayerChar(t)) {
      ulong tt = Ptr(t + Offsets.Target);
      onYou = tt == player;
      if (!onYou && tt != t && IsCreature(tt) && IsPlayerChar(tt)) onOther = CreatureName(tt);
    }
    return new TargetInfo {
      OnYou = onYou, OnOther = onOther, OnYouCount = onYouCount,
      Id = t,
      Name = string.IsNullOrEmpty(name) ? "Target" : name,
      Health = BitConverter.ToInt32(cur, 0), HealthMax = BitConverter.ToInt32(max, 0),
      Action = BitConverter.ToInt32(cur, 8), ActionMax = BitConverter.ToInt32(max, 8),
      Mind = BitConverter.ToInt32(cur, 16), MindMax = BitConverter.ToInt32(max, 16),
      Level = lvl == null ? 0 : BitConverter.ToInt16(lvl, 0),
      Distance = self ? -1 : Distance(player, t),
      Buffs = buffs,
    };
  }

  bool IsTangible(ulong obj) { if (obj == 0) return false; ulong vt = Ptr(obj); return vt == baseAddr + Offsets.TangibleVtable || vt == baseAddr + Offsets.InstallationVtable; }

  // A lair (or turret, or other tangible): one hitpoint bar, no Action/Mind, level or buffs
  TargetInfo ReadTangible(ulong player, ulong t) {
    var nb = Read(t + Offsets.Name, 0x20);
    string name = nb == null ? "" : StdWStr(nb, 0);
    if (name.Length == 0) name = OverlayForm.StripColors(BuffBook.Text(Std(t + Offsets.StringIdTable), Std(t + Offsets.StringIdKey)));
    var d = Read(t + Offsets.DamageTaken, 4); var m = Read(t + Offsets.MaxHitPoints, 4);
    if (d == null || m == null) return null;
    int max = BitConverter.ToInt32(m, 0), dmg = BitConverter.ToInt32(d, 0);
    if (max <= 0 || max > 100000000 || dmg < 0) return null; // not something with hitpoints
    return new TargetInfo { Id = t, Name = name.Length > 0 ? name : "Target", Health = Math.Max(0, max - dmg), HealthMax = max, Distance = Distance(player, t) };
  }

  bool IsShip(ulong obj) { return obj != 0 && Ptr(obj) == baseAddr + Offsets.ShipVtable; }

  float F(ulong addr) { var b = Read(addr, 4); return b == null ? 0 : BitConverter.ToSingle(b, 0); }

  string Std(ulong addr) { var b = Read(addr, 0x20); return b == null ? "" : StdStr(b, 0); }

  // An AutoDelta std::map<int, float> (e.g. per-slot armor): in-order walk, node key at +0x1c and value at +0x20
  Dictionary<int, float> SlotMap(ulong mapObj) {
    var map = new Dictionary<int, float>();
    var mb = Read(mapObj + 0x18, 0x10);
    if (mb == null) return map;
    ulong head = BitConverter.ToUInt64(mb, 0);
    long size = BitConverter.ToInt64(mb, 8);
    var hb = Read(head, 0x20);
    if (size <= 0 || size > 64 || hb == null || hb[0x19] != 1) return map;
    var stack = new Stack<ulong>();
    ulong n = BitConverter.ToUInt64(hb, 8);
    int guard = 0;
    while (guard++ < 300) {
      if (n != head && n != 0) {
        var b = Read(n, 0x28);
        if (b == null || b[0x19] > 1) return map;
        if (b[0x19] == 0) { stack.Push(n); n = BitConverter.ToUInt64(b, 0); continue; }
      }
      if (stack.Count == 0) break;
      n = stack.Pop();
      var nb = Read(n, 0x28);
      if (nb == null) break;
      map[BitConverter.ToInt32(nb, 0x1c)] = BitConverter.ToSingle(nb, 0x20);
      n = BitConverter.ToUInt64(nb, 0x10);
    }
    return map;
  }

  // A ship target: the same four numbers as the game's space target box, plus every component for the tooltip
  TargetInfo ReadShip(ulong player, ulong t) {
    var nb = Read(t + Offsets.Name, 0x20);
    string name = nb == null ? "" : StdWStr(nb, 0);
    if (name.Length == 0) name = OverlayForm.StripColors(BuffBook.Text(Std(t + Offsets.StringIdTable), Std(t + Offsets.StringIdKey)));
    string type = Std(t + Offsets.MobileType), tier = Std(t + Offsets.Difficulty);
    var s = new ShipInfo {
      Type = OverlayForm.StripColors(BuffBook.Text("space/space_mobile_type", type)),
      Tier = tier.Length == 0 ? "" : BuffBook.Text("space/space_difficulty", tier) ?? tier,
      Chassis = F(t + Offsets.ChassisCur), ChassisMax = F(t + Offsets.ChassisMax),
      ShieldFront = F(t + Offsets.ShieldFrontCur), ShieldFrontMax = F(t + Offsets.ShieldFrontMax),
      ShieldBack = F(t + Offsets.ShieldBackCur), ShieldBackMax = F(t + Offsets.ShieldBackMax),
    };
    var armorMax = SlotMap(t + Offsets.ArmorMax); var armorCur = SlotMap(t + Offsets.ArmorCur);
    var hpMax = SlotMap(t + Offsets.HpMax); var hpCur = SlotMap(t + Offsets.HpCur);
    foreach (var kv in hpMax) {
      float a, am, h;
      armorCur.TryGetValue(kv.Key, out a); armorMax.TryGetValue(kv.Key, out am); hpCur.TryGetValue(kv.Key, out h);
      s.Parts.Add(new ShipPart { Slot = kv.Key, Armor = a, ArmorMax = am, Hp = h, HpMax = kv.Value });
    }
    s.Parts.Sort((x, y) => x.Slot.CompareTo(y.Slot));
    // You sit inside your ship at ship-relative coordinates, so measure from the ship instead
    ulong own = OwnShip(player);
    // The targeted-component slot is only kept for a fighter pilot (you sit directly in the ship). In a multi-crew
    // ship's seat it stays 0, which would wrongly show the reactor, so leave the row out there.
    if (own != 0 && own == ContainerOf(player)) {
      var sb = Read(own + Offsets.SubTargetSlot, 4);
      int slot = sb == null ? -1 : BitConverter.ToInt32(sb, 0);
      s.Sub = s.Parts.Find(p => p.Slot == slot); // null for 115 (none) or a slot the target doesn't have
    }
    return new TargetInfo { Id = t, Name = name.Length > 0 ? name : "Ship", Distance = own != 0 ? Distance(own, t) : -1, Ship = s };
  }

  // The ship you're in: a fighter's pilot sits directly in the ship, but in a multi-crew ship you sit in a seat or
  // turret inside a cell inside the ship (player → seat → cell → ship), so walk up the containers. 0 if none.
  ulong ContainerOf(ulong o) {
    ulong cb = Ptr(o + Offsets.ContainedBy);
    return cb == 0 ? 0 : Ptr(cb + Offsets.ContainedByObj);
  }

  ulong OwnShip(ulong player) {
    ulong o = player;
    for (int i = 0; i < 6; i++) {
      o = ContainerOf(o);
      if (o == 0) return 0;
      if (IsShip(o)) return o;
    }
    return 0;
  }

  float[] Pos(ulong obj) {
    var x = Read(obj + Offsets.PosX, 4); var y = Read(obj + Offsets.PosY, 4); var z = Read(obj + Offsets.PosZ, 4);
    return x == null || y == null || z == null ? null : new[] { BitConverter.ToSingle(x, 0), BitConverter.ToSingle(y, 0), BitConverter.ToSingle(z, 0) };
  }

  // Straight-line distance in metres. Positions are parent-relative, so this is wrong when only one of the
  // two is inside a building cell. Returns -1 when unknown.
  float Distance(ulong a, ulong b) {
    var p = Pos(a); var q = Pos(b);
    if (p == null || q == null) return -1;
    double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2];
    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
    return double.IsNaN(d) || d > 100000 ? -1 : (float)d;
  }
}

sealed class TargetInfo {
  public string Name;
  public int Health, HealthMax, Action, ActionMax, Mind, MindMax, Level;
  public float Distance;
  public List<ActiveBuff> Buffs = new List<ActiveBuff>();
  public ShipInfo Ship; // set for space targets (no HAM, level or buffs then)
  public ulong Id;      // object address: a new target resets the damage trails
  public bool HasPools { get { return ActionMax > 0 || MindMax > 0; } } // false for lairs and other tangibles: health only
  public bool OnYou;      // the target is attacking you
  public string OnOther;  // ... or this other player
  public int OnYouCount;  // self overlay: mobs attacking you
  public bool HasAggro { get { return OnYou || OnOther != null || OnYouCount > 0; } }
}

// A live NPC/creature near you (GameReader.ReadNearby)
sealed class NearbyMob {
  public ulong Id;
  public string Name, OnOther; // OnOther: the player it's attacking, when that isn't you
  public int Level, Health, HealthMax;
  public float Distance;
  public bool OnYou, IsTarget;
  public bool InCombat { get { return OnYou || OnOther != null; } }
}

// One mob attacking a player near you (GameReader.ReadThreats)
sealed class Threat {
  public ulong Id;
  public string Name, Target; // Target: the other player's name, null when it's on you
  public bool OnYou;
  public int Health, HealthMax;
  public float Distance;
}

// Space target, matching the game's target box: Shields = both shield arcs; Armor = the armor of the two armor-plating
// slots; Systems = the hitpoints of every other component; Chassis = hull. Percentages round down, as the game's do.
sealed class ShipInfo {
  public string Type = "", Tier = "";
  public float Chassis, ChassisMax, ShieldFront, ShieldFrontMax, ShieldBack, ShieldBackMax;
  public List<ShipPart> Parts = new List<ShipPart>();
  public ShipPart Sub; // the component you have targeted on it (game: the green "Engine 63%" line), or null

  public static bool IsArmorSlot(int slot) { return slot == 4 || slot == 5; }

  static double Frac(double cur, double max) { return max > 0 ? Math.Max(0, Math.Min(1, cur / max)) : 0; }
  public double Shields { get { return Frac(ShieldFront + ShieldBack, ShieldFrontMax + ShieldBackMax); } }
  public double Hull { get { return Frac(Chassis, ChassisMax); } }
  public double Armor { get { double c = 0, m = 0; foreach (var p in Parts) if (IsArmorSlot(p.Slot)) { c += p.Armor; m += p.ArmorMax; } return Frac(c, m); } }
  public double Systems { get { double c = 0, m = 0; foreach (var p in Parts) if (!IsArmorSlot(p.Slot)) { c += p.Hp; m += p.HpMax; } return Frac(c, m); } }

  public static int Pct(double frac) { return (int)Math.Floor(frac * 100 + 1e-6); }

  // Chassis slot enum (ShipChassisSlotType); weapons start at 15
  static readonly string[] SlotNames = { "Reactor", "Engine", "Shield", "Shield 2", "Armor 1", "Armor 2", "Capacitor", "Booster",
    "Droid interface", "Bridge", "Hangar", "Targeting station", "Cargo hold", "Modification 1", "Modification 2" };
  public static string SlotName(int slot) { return slot >= 0 && slot < SlotNames.Length ? SlotNames[slot] : "Weapon " + (slot - 14); }

  // Hover text: the shield arcs, the hull and every component's armor and hitpoints
  public string Tooltip() {
    var sb = new StringBuilder();
    sb.AppendLine(string.Format("Shields: front {0:N0} / {1:N0}, back {2:N0} / {3:N0}", ShieldFront, ShieldFrontMax, ShieldBack, ShieldBackMax));
    sb.AppendLine(string.Format("Chassis: {0:N0} / {1:N0}", Chassis, ChassisMax));
    foreach (var p in Parts)
      sb.AppendLine(string.Format("{0}: armor {1:N0} / {2:N0}, hp {3:N0} / {4:N0}{5}", SlotName(p.Slot), p.Armor, p.ArmorMax, p.Hp, p.HpMax, p.Hp <= 0 && p.HpMax > 0 ? "  (disabled)" : ""));
    return sb.ToString().TrimEnd();
  }
}

sealed class ShipPart { public int Slot; public float Armor, ArmorMax, Hp, HpMax; }

sealed class ActiveBuff {
  public uint Crc, End, Duration;
  public int Stacks;
  public List<string> Dynamic = new List<string>();
}

// bin/buff-icons/buffs.tsv (from tools/buff-icons.js): crc, name, icon file, debuff, visible, max stacks, display name
static class BuffBook {
  public sealed class Entry { public string Name, Display, Icon, Description; public string[] Effects; public bool Debuff, Visible; }
  // bin/data/buff-icons (the data pack) or bin/buff-icons (tools/buff-icons.js output)
  static readonly string dir = Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data", "buff-icons"))
    ? Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "data", "buff-icons") : Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "buff-icons");
  static Dictionary<uint, Entry> entries;
  static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();

  public static Entry Get(uint crc) {
    if (entries == null) {
      entries = new Dictionary<uint, Entry>();
      try {
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "buffs.tsv"))) {
          var f = line.Split('\t');
          uint c;
          if (f.Length < 7 || !uint.TryParse(f[0], NumberStyles.HexNumber, null, out c)) continue;
          entries[c] = new Entry {
            Name = f[1], Icon = f[2], Debuff = f[3] == "1", Visible = f[4] != "0", Display = f[6].Length > 0 ? f[6] : f[1],
            Description = f.Length > 7 ? f[7].Replace("\\n", "\n") : "",
            Effects = f.Length > 8 && f[8].Length > 0 ? f[8].Split(new[] { "\\n" }, StringSplitOptions.None) : new string[0],
          };
        }
      } catch { }
    }
    Entry e;
    return entries.TryGetValue(crc, out e) ? e : null;
  }

  public static Image Icon(Entry e) {
    string file = e.Icon.Length > 0 ? e.Icon : (e.Debuff ? "buffs.genericDebuff.png" : "buffs.genericBuff.png");
    Image img;
    if (!icons.TryGetValue(file, out img)) {
      try { using (var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(dir, file)))) img = new Bitmap(ms); } catch { img = null; } // no file lock
      icons[file] = img;
    }
    return img;
  }

  // String tables for dynamic effect text (<table>.txt under bin/data/string/en or ../beautified/string/en, see GameData), loaded on first use
  static readonly Dictionary<string, Dictionary<string, string>> tables = new Dictionary<string, Dictionary<string, string>>();

  public static string Text(string table, string key) {
    // Tables may sit in subfolders (space/ship_names), but nothing may climb out of the string folder
    if (table.Length == 0 || key.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(table, @"^[A-Za-z0-9_]+(/[A-Za-z0-9_]+)*$")) return null;
    Dictionary<string, string> t;
    if (!tables.TryGetValue(table, out t)) {
      t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      try {
        foreach (var line in File.ReadAllLines(GameData.File("string", "en", table.Replace('/', Path.DirectorySeparatorChar) + ".txt"))) {
          int i = line.IndexOf(" = ");
          if (i > 0) t[line.Substring(0, i)] = line.Substring(i + 3).Replace("\\n", "\n");
        }
      } catch { }
      tables[table] = t;
    }
    string v;
    return t.TryGetValue(key, out v) ? v : null;
  }

  // %DI = whole number, %DF = number with up to two decimals (as in the game's tooltips)
  public static string Fill(string text, double v) {
    return text.Replace("%DI", Math.Round(v).ToString(CultureInfo.InvariantCulture)).Replace("%DF", Math.Round(v, 2).ToString(CultureInfo.InvariantCulture));
  }

  // The game's buff tooltip: time remaining, name, description, then the effect lines
  public static string Tooltip(ActiveBuff b, Entry e) {
    var sb = new StringBuilder();
    long rem = GameReader.Remaining(b);
    if (rem >= 0) sb.AppendLine(string.Format("Time Remaining:  {0:00}:{1:00}:{2:00}", rem / 3600, rem / 60 % 60, rem % 60));
    sb.AppendLine(b.Stacks > 1 ? e.Display + " (" + b.Stacks + ")" : e.Display);
    if (e.Description.Length > 0) sb.AppendLine(e.Description);
    foreach (var l in b.Dynamic) sb.AppendLine(l);
    foreach (var l in e.Effects) sb.AppendLine(l);
    return sb.ToString().TrimEnd();
  }

  // Sample buffs for previews: a couple of debuffs and a few buffs that have their own icons
  public static List<ActiveBuff> Samples() {
    Get(0);
    var list = new List<ActiveBuff>();
    int deb = 0, buf = 0;
    foreach (var kv in entries) {
      var e = kv.Value;
      if (!e.Visible || e.Icon.Length == 0 || e.Icon.StartsWith("buffs.generic")) continue;
      if (e.Debuff ? deb++ >= 2 : buf++ >= 5) continue;
      list.Add(new ActiveBuff { Crc = kv.Key, End = 0xffffffff, Stacks = list.Count == 1 ? 3 : 1 });
    }
    return list;
  }
}

sealed class OverlayForm : LayeredForm {
  // Layout is designed at this size and drawn scaled; resizing changes the scale, keeping the aspect ratio
  const int BaseW = 320, BaseH = 70, MinW = 200, MaxW = 1280, Grip = 14;
  const int IconSize = 24, IconGap = 3, BuffRowH = 42, BuffPad = 6; // buff row(s) under the panel (design units)
  const int StripH = 18; // the aggro strip between the panel and the buffs, while there's aggro to show

  static readonly string[] StyleNames = { "Classic", "Holo", "Minimal", "Terminal", "Radial", "Questlog" };
  static readonly Color[] StyleBacks = {
    Color.FromArgb(18, 22, 28), Color.FromArgb(5, 14, 20), Color.FromArgb(14, 15, 18), Color.FromArgb(12, 8, 2), Color.FromArgb(16, 18, 24), Color.FromArgb(18, 18, 18),
  };

  readonly GameReader reader = new GameReader();
  readonly Timer timer = new Timer { Interval = 100 };
  readonly Timer animTimer = new Timer { Interval = 16 }; // ~60 fps, only repaints while a damage trail moves
  ulong trailTarget;
  readonly string posFile;
  readonly bool self; // the self overlay: your own health, action, mind and buffs
  readonly Font termFont = UiLook.PanelFont("Consolas", 8.5f, FontStyle.Bold); // Terminal stays monospace for its block bars
  Font nameFont, hpFont, barFont, holoFont, holoSmall, minBig, minName, minSmall, minTiny, radName, radHp, radSmall, radLevel;
  TargetInfo info;
  string status = "Waiting for game";
  float scale = 1f;
  int style;
  DateTime previewUntil;
  Point dragFrom;
  bool resizing;
  int reconnectTick;
  NotifyIcon tray;
  ContextMenuStrip menu, trayMenu;
  readonly bool hosted;

  // Hosted inside swgr-tools: no tray icon of its own; the host shows HostMenu and toggles Active
  public ContextMenuStrip HostMenu { get { return trayMenu; } }

  public bool Active {
    get { return timer.Enabled; }
    set { timer.Enabled = value; if (!value) Visible = false; }
  }

  // For the swgr-tools main window
  public static string[] Styles { get { return StyleNames; } }
  public int Style { get { return style; } set { SetStyle(value); } }
  public void Preview() { previewShip = false; previewUntil = DateTime.Now.AddSeconds(5); }
  public void PreviewShip() { previewShip = true; previewUntil = DateTime.Now.AddSeconds(5); }
  public void ResetSize() { scale = 1f; ApplyScale(); SaveSettings(); }

  public OverlayForm(bool hosted = false, bool self = false) {
    this.hosted = hosted;
    this.self = self;
    posFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), self ? "swgr-self.pos" : "swgr-overlay.pos");
    UseOpacity(self ? "self" : "target");
    Text = self ? "SWGR self" : "SWGR target";
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    Location = LoadSettings();
    ApplyScale();
    BackColor = StyleBacks[style];

    // The window hides when there is no target, so the tray icon is the way to reach the menu then
    MakeFonts();
    UiLook.Changed += OnLook;
    menu = BuildMenu();
    trayMenu = BuildMenu();
    ContextMenuStrip = menu;
    if (!hosted) tray = new NotifyIcon { Icon = SystemIcons.Application, Text = self ? "SWGR self overlay" : "SWGR target overlay", ContextMenuStrip = trayMenu, Visible = true };

    timer.Tick += (s, e) => Tick();
    animTimer.Tick += (s, e) => { if (animating && Visible) Invalidate(); };
    animTimer.Start();
    timer.Start();
  }

  // Each style's own fonts, or the font picked under Font (tray / right-click) once one has been chosen
  void MakeFonts() {
    foreach (var f in new[] { nameFont, hpFont, barFont, holoFont, holoSmall, minBig, minName, minSmall, minTiny, radName, radHp, radSmall, radLevel, qlName, qlNum, qlSmall, qlTiny }) if (f != null) f.Dispose();
    Func<string, string> strong = fam => UiLook.Chosen ? UiLook.Strong : fam;
    Func<string, string> regular = fam => UiLook.Chosen ? UiLook.Regular : fam;
    nameFont = UiLook.PanelFont(strong("Segoe UI Semibold"), 10f);
    hpFont = UiLook.PanelFont(strong("Segoe UI Semibold"), 9f);
    holoFont = UiLook.PanelFont(strong("Bahnschrift SemiBold"), 9.5f);
    holoSmall = UiLook.PanelFont(regular("Bahnschrift"), 7.5f);
    minBig = UiLook.PanelFont(regular("Segoe UI Light"), 25f);
    minName = UiLook.PanelFont(strong("Segoe UI Semibold"), 10.5f);
    minSmall = UiLook.PanelFont(regular("Segoe UI"), 8f);
    radName = UiLook.PanelFont(strong("Segoe UI Semibold"), 11f);
    radHp = UiLook.PanelFont(strong("Segoe UI Semibold"), 10f);
    radSmall = UiLook.PanelFont(regular("Segoe UI"), 8f);
    radLevel = UiLook.PanelFont(strong("Segoe UI Semibold"), 11f);
    barFont = UiLook.PanelFont(strong("Segoe UI Semibold"), 7.5f);
    minTiny = UiLook.PanelFont(regular("Segoe UI"), 7f);
    // Questlog: Encode Sans like the DPS meter's Questlog style (bundled in bin/fonts)
    qlName = UiLook.MakeFont(strong("Encode Sans SemiBold"), 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlNum = UiLook.MakeFont(strong("Encode Sans SemiBold"), 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlSmall = UiLook.MakeFont(regular("Encode Sans"), 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    qlTiny = UiLook.MakeFont(regular("Encode Sans"), 10f, FontStyle.Regular, GraphicsUnit.Pixel);
  }

  // Text size from the shared menu sets this window's scale (dragging the corner still fine-tunes it)
  void OnLook(bool sizePicked) {
    MakeFonts();
    if (sizePicked) { scale = Math.Max((float)MinW / BaseW, Math.Min((float)MaxW / BaseW, UiLook.Scale)); }
    ApplyScale();
    SaveSettings();
    previewUntil = DateTime.Now.AddSeconds(5);
    Invalidate();
  }

  ContextMenuStrip BuildMenu() {
    var m = new ContextMenuStrip();
    var styles = new ToolStripMenuItem("Style");
    for (int i = 0; i < StyleNames.Length; i++) {
      int idx = i;
      styles.DropDownItems.Add(new ToolStripMenuItem((i + 1) + ". " + StyleNames[i], null, (s, e) => SetStyle(idx)) { Checked = i == style });
    }
    m.Items.Add(styles);
    m.Items.Add("Preview (5 s)", null, (s, e) => { previewShip = false; previewUntil = DateTime.Now.AddSeconds(5); });
    m.Items.Add("Preview ship (5 s)", null, (s, e) => { previewShip = true; previewUntil = DateTime.Now.AddSeconds(5); });
    m.Items.Add("Reset size", null, (s, e) => { scale = 1f; ApplyScale(); SaveSettings(); });
    m.Items.AddRange(UiLook.MenuItems());
    m.Items.AddRange(OpacityMenu());
    m.Items.Add(new ToolStripSeparator());
    m.Items.Add(hosted ? "Turn off" : "Close", null, (s, e) => { if (hosted) Active = false; else Close(); });
    return m;
  }

  void SetStyle(int s) {
    style = s;
    BackColor = StyleBacks[s];
    foreach (var m in new[] { menu, trayMenu }) {
      var items = ((ToolStripMenuItem)m.Items[0]).DropDownItems;
      for (int i = 0; i < items.Count; i++) ((ToolStripMenuItem)items[i]).Checked = i == s;
    }
    previewUntil = DateTime.Now.AddSeconds(5);
    SaveSettings();
    Invalidate();
  }

  int buffRows; // rows of buff icons currently shown under the panel (0-2)

  bool shipMode; // a space target: the taller ship panel, no buff rows
  int shipH;

  bool strip; // the aggro strip is showing
  int StripY { get { return strip ? StripH : 0; } }

  void ApplyScale() { ClientSize = new Size((int)Math.Round(BaseW * scale * UiLook.Dpi), (int)Math.Round((shipMode ? shipH : BaseH + StripY + buffRows * BuffRowH) * scale * UiLook.Dpi)); }

  // Visible buffs with a known entry, debuffs first, then by time left (permanent last)
  static List<Tuple<ActiveBuff, BuffBook.Entry>> ShownBuffs(TargetInfo t) {
    var list = new List<Tuple<ActiveBuff, BuffBook.Entry>>();
    foreach (var b in t.Buffs) {
      var e = BuffBook.Get(b.Crc);
      if (e != null && e.Visible) list.Add(Tuple.Create(b, e));
    }
    list.Sort((a, b) => {
      if (a.Item2.Debuff != b.Item2.Debuff) return a.Item2.Debuff ? -1 : 1;
      long ra = GameReader.Remaining(a.Item1), rb = GameReader.Remaining(b.Item1);
      return (ra < 0 ? long.MaxValue : ra).CompareTo(rb < 0 ? long.MaxValue : rb);
    });
    return list;
  }

  static int PerRow { get { return (BaseW - 2 * BuffPad + IconGap) / (IconSize + IconGap); } }

  static string Short(long secs) {
    if (secs < 0) return "";
    if (secs < 60) return secs + "s";
    if (secs < 3600) return (secs / 60) + "m";
    return (secs / 3600) + "h";
  }

  // Hit boxes (design units) and names for the hover tooltip
  readonly List<Tuple<RectangleF, ActiveBuff, BuffBook.Entry>> buffHits = new List<Tuple<RectangleF, ActiveBuff, BuffBook.Entry>>();

  // Icon row(s) in the design space starting at y: red frame for debuffs, stack count top-right, time left below
  void PaintBuffs(Graphics g, TargetInfo t, float y, bool hits) {
    var shown = ShownBuffs(t);
    if (hits) buffHits.Clear();
    int per = PerRow;
    using (var small = UiLook.PanelFont(UiLook.Chosen ? UiLook.Strong : "Segoe UI Semibold", 7f))
    using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
      for (int i = 0; i < shown.Count && i < 2 * per; i++) {
        var b = shown[i].Item1; var e = shown[i].Item2;
        float x = BuffPad + (i % per) * (IconSize + IconGap), iy = y + 3 + (i / per) * BuffRowH;
        var r = new RectangleF(x, iy, IconSize, IconSize);
        var img = BuffBook.Icon(e);
        if (img != null) {
          var mode = g.InterpolationMode;
          g.InterpolationMode = InterpolationMode.HighQualityBicubic;
          g.DrawImage(img, r);
          g.InterpolationMode = mode;
        } else using (var br = new SolidBrush(Color.FromArgb(60, 70, 80))) g.FillRectangle(br, r);
        using (var p = new Pen(e.Debuff ? Color.FromArgb(230, 70, 60) : Color.FromArgb(90, 110, 130), e.Debuff ? 1.5f : 1f)) g.DrawRectangle(p, x, iy, IconSize, IconSize);
        if (b.Stacks > 1) DrawText(g, b.Stacks.ToString(), small, new RectangleF(x + IconSize - 14, iy - 1, 14, 11), Color.White, StringAlignment.Far, true);
        long rem = GameReader.Remaining(b);
        using (var br = new SolidBrush(rem >= 0 && rem < 10 ? Color.FromArgb(255, 120, 100) : Color.FromArgb(200, 208, 216)))
          g.DrawString(Short(rem), small, br, new RectangleF(x - 4, iy + IconSize + 1, IconSize + 8, 14), fmt);
        if (hits) buffHits.Add(Tuple.Create(r, b, e));
      }
  }

  // swgr-overlay.pos holds "x,y[,scale[,style]]"
  Point LoadSettings() {
    try {
      var p = File.ReadAllText(posFile).Split(',');
      if (p.Length > 2) scale = Math.Max((float)MinW / BaseW, Math.Min((float)MaxW / BaseW, float.Parse(p[2], CultureInfo.InvariantCulture)));
      if (p.Length > 3) style = Math.Max(0, Math.Min(StyleNames.Length - 1, int.Parse(p[3])));
      var pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
      foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(pt)) return pt;
    } catch { }
    var wa = Screen.PrimaryScreen.WorkingArea;
    return self ? new Point(wa.Left + 40, wa.Top + 120) : new Point(wa.Left + wa.Width / 2 - BaseW / 2, wa.Top + 120);
  }

  void SaveSettings() {
    try { File.WriteAllText(posFile, Left + "," + Top + "," + scale.ToString(CultureInfo.InvariantCulture) + "," + style); } catch { }
  }

  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

  static readonly TargetInfo Sample = new TargetInfo {
    Name = "a vicious Rill", Health = 214, HealthMax = 316, Action = 820, ActionMax = 1000, Mind = 1000, MindMax = 1000, Level = 3, Distance = 9,
    Buffs = BuffBook.Samples(),
  };

  // Values from a real fight (Mercenary Cruiser, game box showed 50 / 50 / 31 / 69%), with the engine targeted
  // as in a later screenshot ("Engine 63%")
  static readonly TargetInfo ShipSample = new TargetInfo {
    Name = "Mercenary Cruiser", Distance = 86,
    Ship = new ShipInfo {
      Type = "HC-556 Light Cruiser", Tier = "Tier 3", Chassis = 8288, ChassisMax = 12000,
      ShieldFront = 0, ShieldFrontMax = 43200, ShieldBack = 43200, ShieldBackMax = 43200,
      Parts = new List<ShipPart> {
        new ShipPart { Slot = 0, Armor = 0, ArmorMax = 10800, Hp = 2171, HpMax = 10800 },
        new ShipPart { Slot = 1, Armor = 2808, ArmorMax = 10800, Hp = 10800, HpMax = 10800 },
        new ShipPart { Slot = 2, Armor = 0, ArmorMax = 10800, Hp = 0, HpMax = 10800 },
        new ShipPart { Slot = 4, Armor = 23040, ArmorMax = 23040, Hp = 23040, HpMax = 23040 },
        new ShipPart { Slot = 5, Armor = 0, ArmorMax = 23040, Hp = 23040, HpMax = 23040 },
        new ShipPart { Slot = 6, Armor = 0, ArmorMax = 10800, Hp = 1885, HpMax = 10800 },
        new ShipPart { Slot = 15, Armor = 0, ArmorMax = 10800, Hp = 7920, HpMax = 10800 },
      },
    },
  };
  bool previewShip;
  static OverlayForm() { ShipSample.Ship.Sub = ShipSample.Ship.Parts[1]; }

  void Tick() {
    if (!reader.Connected) {
      info = null;
      status = "Waiting for game";
      if (++reconnectTick % 20 == 1) reader.Connect(); // try every 2 s
    } else {
      info = reader.ReadTarget(self);
      status = reader.Connected ? (self ? "Not in game" : "No target") : "Waiting for game";
    }
    string tip = (self ? "SWGR self overlay: " : "SWGR target overlay: ") + (info != null ? info.Name : status);
    if (tray != null) tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // NotifyIcon throws above 63 chars
    if (info == null && DateTime.Now < previewUntil) info = previewShip ? ShipSample : Sample;
    ulong tid = info == null ? 0 : info.Id;
    if (tid != trailTarget) { trails.Clear(); trailTarget = tid; }
    bool ship = info != null && info.Ship != null;
    int sh = ship ? ShipHeight(info.Ship, style) : 0;
    if (ship != shipMode || sh != shipH) { shipMode = ship; shipH = sh; ApplyScale(); }

    // Only shown while there is a target (or while previewing/resizing); ShowWithoutActivation keeps focus on the game
    bool show = info != null || resizing;
    if (!show) HideTip(); // e.g. a loading screen: the tooltip is its own window and would stay behind
    if (show != Visible) Visible = show;
    if (!show) return;
    if (lastTip != null) {
      var at = PointToClient(Cursor.Position);
      if (!ClientRectangle.Contains(at)) HideTip(); // left without a MouseLeave (focus is on the game)
      else if (DateTime.Now.Millisecond < 100) { lastTip = null; OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, at.X, at.Y, 0)); } // refresh the countdown ~1/s
    }
    int rows = Math.Min(2, (ShownBuffs(info ?? Sample).Count + PerRow - 1) / PerRow);
    if (info == null || shipMode) rows = 0;
    bool st = info != null && !shipMode && info.HasAggro;
    if (rows != buffRows || st != strip) { buffRows = rows; strip = st; ApplyScale(); }
    // A focused borderless DX12 game can end up above other topmost windows; put this (own) window back on top
    KeepOnTop();
    Invalidate();
  }

  // ---------- shared drawing helpers (all in the 320x70 design space) ----------

  static readonly Color TextColor = Color.FromArgb(235, 238, 242);

  static double Frac(int cur, int max) { return max > 0 ? Math.Max(0, Math.Min(1, (double)cur / max)) : 0; }

  // GDI+ text so it follows the scale transform (TextRenderer ignores transforms)
  static void DrawText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h, bool shadow) {
    using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter }) {
      if (shadow) using (var b = new SolidBrush(Color.Black)) g.DrawString(s, f, b, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), fmt);
      using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, fmt);
    }
  }

  static string Dist(TargetInfo t) { return t.Distance >= 0 ? string.Format("{0:0}m", t.Distance) : ""; }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    animating = false; // set again by Tr while a trail is still above its bar
    if (info != null) {
      var state = g.Save();
      g.ScaleTransform(scale * UiLook.Dpi, scale * UiLook.Dpi);
      if (info.Ship != null) PaintShip(g, style, info);
      else PaintStyle(g, style, info);
      if (strip && info.Ship == null) PaintAggro(g, info, BaseH);
      if (buffRows > 0 && info.Ship == null) PaintBuffs(g, info, BaseH + StripY, true);
      g.Restore(state);
    }
    DrawGrip(g);
  }

  // ---------- aggro: a frame round the panel and a strip under it ----------
  // Green: on you (or, on the self overlay, how many are). Red: on another player, so it needs a taunt.

  static readonly Color AggroYou = Color.FromArgb(80, 210, 120), AggroLoose = Color.FromArgb(255, 84, 76);

  void PaintAggro(Graphics g, TargetInfo t, float y) {
    var c = t.OnOther != null ? AggroLoose : AggroYou;
    using (var frame = new Pen(c, 2f)) g.DrawRectangle(frame, 1, 1, BaseW - 2, y - 2);
    using (var band = new SolidBrush(Color.FromArgb(60, c))) g.FillRectangle(band, 0, y, BaseW, StripH);
    using (var bar = new SolidBrush(c)) g.FillRectangle(bar, 0, y, 4, StripH);
    string head = t.OnOther != null ? "ON " + t.OnOther : t.OnYouCount > 0 ? t.OnYouCount + (t.OnYouCount == 1 ? " MOB ON YOU" : " MOBS ON YOU") : "ON YOU";
    using (var f = UiLook.PanelFont(UiLook.Chosen ? UiLook.Strong : "Segoe UI Semibold", 8.5f)) {
      DrawText(g, head, f, new RectangleF(10, y, BaseW - 90, StripH), c, StringAlignment.Near, true);
      if (t.OnOther != null) DrawText(g, "taunt", f, new RectangleF(BaseW - 80, y, 72, StripH), Color.FromArgb(200, c), StringAlignment.Far, true);
    }
  }

  // swgr-overlay.exe --render-aggro <out.png>: each style on you, loose (on another player), and the self count
  void RenderAggro(string outPath) {
    const int k = 2, gap = 12;
    var cases = new[] {
      new TargetInfo { Name = Sample.Name, Health = 214, HealthMax = 316, Action = 820, ActionMax = 1000, Mind = 1000, MindMax = 1000, Level = 3, Distance = 9, OnYou = true, Buffs = Sample.Buffs },
      new TargetInfo { Name = Sample.Name, Health = 214, HealthMax = 316, Action = 820, ActionMax = 1000, Mind = 1000, MindMax = 1000, Level = 3, Distance = 9, OnOther = "Bealdwine", Buffs = Sample.Buffs },
      new TargetInfo { Name = "Kira'Dorne", Health = 3116, HealthMax = 3427, Action = 1016, ActionMax = 1016, Mind = 1016, MindMax = 1016, Level = 80, Distance = -1, OnYouCount = 3, Buffs = Sample.Buffs },
    };
    int h = BaseH + StripH + BuffRowH;
    using (var bmp = new Bitmap(cases.Length * (BaseW * k + gap) + gap, StyleNames.Length * (h * k + gap) + gap))
    using (var g = Graphics.FromImage(bmp)) {
      g.Clear(Color.FromArgb(60, 66, 74));
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      for (int i = 0; i < StyleNames.Length; i++)
        for (int c = 0; c < cases.Length; c++) {
          var state = g.Save();
          g.TranslateTransform(gap + c * (BaseW * k + gap), gap + i * (h * k + gap));
          g.ScaleTransform(k, k);
          using (var b = new SolidBrush(StyleBacks[i])) g.FillRectangle(b, 0, 0, BaseW, h);
          trails.Clear();
          PaintStyle(g, i, cases[c]);
          PaintAggro(g, cases[c], BaseH);
          PaintBuffs(g, cases[c], BaseH + StripH, false);
          g.Restore(state);
        }
      bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }
  }

  void RenderQl(string outPath) {
    const int k = 2, gap = 14, label = 18;
    int sh = ShipHeight(ShipSample.Ship, 5), rowH = Math.Max(BaseH + BuffRowH, sh);
    using (var bmp = new Bitmap(2 * BaseW * k + 3 * gap, 3 * (rowH * k + gap + label + 4) + gap))
    using (var g = Graphics.FromImage(bmp)) {
      g.Clear(Color.FromArgb(120, 104, 82));
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      int keepLevel = qlLevel, keepPalette = qlPalette;
      var shown = new[] { Tuple.Create(0, 0, "Before: dim Questlog"), Tuple.Create(5, keepPalette, "Middle (new default)"), Tuple.Create(4, keepPalette, "Bright (last build)") };
      for (int lv = 0; lv < shown.Length; lv++) {
        qlLevel = shown[lv].Item1; qlPalette = shown[lv].Item2;
        float y0 = gap + lv * (rowH * k + gap + label + 4) + 4;
        using (var f = new Font("Segoe UI Semibold", 11f)) g.DrawString(shown[lv].Item3, f, Brushes.White, gap, y0 - 3);
        for (int c = 0; c < 2; c++) {
          var state = g.Save();
          g.TranslateTransform(gap + c * (BaseW * k + gap), y0 + label);
          g.ScaleTransform(k, k);
          int h = c == 0 ? BaseH : sh;
          var bg = Color.FromArgb(Math.Max(18, QL[2] - 6), Math.Max(18, QL[2] - 6), Math.Max(18, QL[2] - 6));
          using (var path = LayeredForm.RoundRect(new RectangleF(0, 0, BaseW, h), 10)) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
          trails.Clear();
          if (c == 0) { PaintQuestlog(g, Sample); PaintBuffs(g, Sample, BaseH, false); } else PaintShipQuestlog(g, ShipSample);
          g.Restore(state);
        }
      }
      qlLevel = keepLevel; qlPalette = keepPalette;
      bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }
  }

  void PaintStyle(Graphics g, int s, TargetInfo t) {
    switch (s) {
      case 1: PaintHolo(g, t); break;
      case 2: PaintMinimal(g, t); break;
      case 3: PaintTerminal(g, t); break;
      case 4: PaintRadial(g, t); break;
      case 5: PaintQuestlog(g, t); break;
      default: PaintClassic(g, t); break;
    }
  }

  // swgr-overlay.exe --render <out.png>: draws every style with sample data at 2x, creature on the left and ship on
  // the right (for previews, no game needed)
  void RenderAll(string outPath) {
    const int k = 2, gap = 12;
    int h = Math.Max(BaseH + BuffRowH, ShipHeight(ShipSample.Ship, 2));
    using (var bmp = new Bitmap(2 * BaseW * k + 3 * gap, StyleNames.Length * (h * k + gap) + gap))
    using (var g = Graphics.FromImage(bmp)) {
      g.Clear(Color.FromArgb(60, 66, 74));
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      for (int i = 0; i < StyleNames.Length; i++) {
        var state = g.Save();
        g.TranslateTransform(gap, gap + i * (h * k + gap));
        g.ScaleTransform(k, k);
        using (var b = new SolidBrush(StyleBacks[i])) g.FillRectangle(b, 0, 0, BaseW, h);
        PaintStyle(g, i, Sample);
        PaintBuffs(g, Sample, BaseH, false);
        g.Restore(state);
        state = g.Save();
        g.TranslateTransform(2 * gap + BaseW * k, gap + i * (h * k + gap));
        g.ScaleTransform(k, k);
        using (var b = new SolidBrush(StyleBacks[i])) g.FillRectangle(b, 0, 0, BaseW, ShipHeight(ShipSample.Ship, i));
        PaintShip(g, i, ShipSample);
        g.Restore(state);
      }
      bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }
  }

  // swgr-overlay.exe --render-trail <out.png>: each style (rows) at moments around one hit (columns), creature
  // then ship. Uses a fake clock, so the frames are exact.
  void RenderTrail(string outPath) {
    double[] times = { -0.1, 0.05, 0.3, 0.5, 0.65, 0.85, 1.2 };
    const float k = 1.25f; const int gap = 8;
    int ch = BaseH, sh = ShipHeight(ShipSample.Ship, 2), cellH = Math.Max(ch, sh);
    int cw = (int)(BaseW * k) + gap;
    using (var bmp = new Bitmap(times.Length * cw + gap, 2 * StyleNames.Length * ((int)(cellH * k) + gap) + gap))
    using (var g = Graphics.FromImage(bmp)) {
      g.Clear(Color.FromArgb(60, 66, 74));
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      for (int row = 0; row < 2 * StyleNames.Length; row++) {
        int st = row % StyleNames.Length; bool ship = row >= StyleNames.Length;
        for (int c = 0; c < times.Length; c++) {
          trails.Clear();
          // the frame before the hit seeds every trail at the full values, then the hit lands at t = 0
          var before = ship ? ShipFrame(false) : HitFrame(false);
          var after = ship ? ShipFrame(true) : HitFrame(true);
          using (var scratch = new Bitmap(8, 8)) using (var sg = Graphics.FromImage(scratch)) {
            fakeNow = -1; if (ship) PaintShip(sg, st, before); else PaintStyle(sg, st, before);
            if (times[c] >= 0) { fakeNow = 0; if (ship) PaintShip(sg, st, after); else PaintStyle(sg, st, after); }
          }
          fakeNow = times[c];
          var state = g.Save();
          g.TranslateTransform(gap + c * cw, gap + row * ((int)(cellH * k) + gap));
          g.ScaleTransform(k, k);
          var t = times[c] < 0 ? before : after;
          using (var b = new SolidBrush(StyleBacks[st])) g.FillRectangle(b, 0, 0, BaseW, ship ? ShipHeight(t.Ship, st) : BaseH);
          if (ship) PaintShip(g, st, t); else PaintStyle(g, st, t);
          g.Restore(state);
        }
      }
      fakeNow = null;
      bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
    }
  }

  static TargetInfo HitFrame(bool hit) {
    return new TargetInfo { Name = Sample.Name, Level = Sample.Level, Distance = Sample.Distance,
      Health = hit ? 214 : 300, HealthMax = 316, Action = hit ? 820 : 1000, ActionMax = 1000, Mind = 1000, MindMax = 1000 };
  }

  static TargetInfo ShipFrame(bool hit) {
    var s = ShipSample.Ship;
    var parts = new List<ShipPart>();
    foreach (var p in s.Parts) parts.Add(new ShipPart { Slot = p.Slot, Armor = p.Armor, ArmorMax = p.ArmorMax, Hp = p.Hp, HpMax = p.HpMax });
    var ship = new ShipInfo { Type = s.Type, Tier = s.Tier, Chassis = hit ? 8288 : 11000, ChassisMax = s.ChassisMax,
      ShieldFront = 0, ShieldFrontMax = s.ShieldFrontMax, ShieldBack = hit ? 30000 : 43200, ShieldBackMax = s.ShieldBackMax, Parts = parts };
    ship.Sub = parts[1];
    return new TargetInfo { Name = ShipSample.Name, Distance = ShipSample.Distance, Ship = ship };
  }

  // Three short diagonal lines in the bottom-right corner (drawn unscaled)
  void DrawGrip(Graphics g) {
    using (var p = new Pen(Color.FromArgb(90, 110, 130)))
      for (int i = 1; i <= 3; i++) g.DrawLine(p, Width - 3 - 3 * i, Height - 3, Width - 3, Height - 3 - 3 * i);
  }

  // ---------- damage trail ----------
  // When a bar drops, the fill moves at once and the lost part stays as a light band: it holds for TrailHold
  // seconds (each new hit restarts the hold, so a quick combo reads as one chunk), then eases down to the new value.
  // Heals just move the fill. Keyed per bar; cleared when the target changes.

  sealed class TrailState { public double Trail, Last, HoldUntil, LastT; }
  readonly Dictionary<string, TrailState> trails = new Dictionary<string, TrailState>();
  const double TrailHold = 0.4, TrailTau = 0.15, TrailFloor = 0.05; // s, s (ease-out time constant), fraction/s
  static readonly Stopwatch trailClock = Stopwatch.StartNew();
  static double? fakeNow; // --render-trail
  static double Now() { return fakeNow ?? trailClock.Elapsed.TotalSeconds; }
  bool animating;

  // The trail value to draw behind a bar now showing frac
  double Tr(string key, double frac) {
    double now = Now();
    TrailState st;
    if (!trails.TryGetValue(key, out st)) { trails[key] = new TrailState { Trail = frac, Last = frac, LastT = now }; return frac; }
    if (frac < st.Last - 1e-6) st.HoldUntil = now + TrailHold;
    st.Last = frac;
    if (frac >= st.Trail) st.Trail = frac;
    else if (now >= st.HoldUntil) {
      double dt = now - Math.Max(st.LastT, st.HoldUntil); // only the time since the hold ended
      st.Trail -= (st.Trail - frac) * (1 - Math.Exp(-dt / TrailTau)) + dt * TrailFloor;
      if (st.Trail < frac + 0.001) st.Trail = frac;
    }
    st.LastT = now;
    if (st.Trail > frac) animating = true;
    return st.Trail;
  }

  // The band's colour: the bar's colour lifted towards white (red HP gives a light red)
  static Color TrailColor(Color c) { return Color.FromArgb(230, c.R + (255 - c.R) * 55 / 100, c.G + (255 - c.G) * 55 / 100, c.B + (255 - c.B) * 55 / 100); }

  // ---------- 1. Classic: mirrors the game's own target window ----------

  void PaintClassic(Graphics g, TargetInfo t) {
    using (var border = new Pen(Color.FromArgb(70, 90, 110))) g.DrawRectangle(border, 0, 0, BaseW - 1, BaseH - 1);
    const int badge = 34, pad = 8;
    int barsRight = BaseW - pad - badge - 6;

    string dist = Dist(t);
    float distW = dist.Length > 0 ? g.MeasureString(dist, nameFont).Width : 0;
    DrawText(g, dist, nameFont, new RectangleF(pad, 2, barsRight - pad, 20), TextColor, StringAlignment.Far, false);
    DrawText(g, t.Name, nameFont, new RectangleF(pad, 2, barsRight - pad - distW - 4, 20), TextColor, StringAlignment.Near, false);

    int w = barsRight - pad;
    ClassicBar(g, new Rectangle(pad, 23, w, 17), t.Health, t.HealthMax, Color.FromArgb(214, 52, 46), Color.FromArgb(150, 28, 26), Color.FromArgb(60, 18, 18), hpFont, Tr("hp", Frac(t.Health, t.HealthMax)));
    if (t.HasPools) ClassicBar(g, new Rectangle(pad, 43, w, 12), t.Action, t.ActionMax, Color.FromArgb(62, 178, 72), Color.FromArgb(30, 112, 40), Color.FromArgb(16, 48, 20), barFont, Tr("ac", Frac(t.Action, t.ActionMax)));
    if (t.HasPools) ClassicBar(g, new Rectangle(pad, 57, w, 12), t.Mind, t.MindMax, Color.FromArgb(66, 112, 222), Color.FromArgb(32, 60, 150), Color.FromArgb(16, 24, 60), barFont, Tr("md", Frac(t.Mind, t.MindMax)));

    if (t.Level > 0) {
      float cx = BaseW - pad - badge / 2f, cy = 24 + 40 / 2f, r = badge / 2f;
      var hex = new PointF[6];
      for (int i = 0; i < 6; i++) { double a = Math.PI / 3 * i; hex[i] = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a)); }
      using (var fill = new SolidBrush(Color.FromArgb(48, 54, 62))) g.FillPolygon(fill, hex);
      using (var edge = new Pen(Color.FromArgb(140, 150, 160), 1.5f)) g.DrawPolygon(edge, hex);
      DrawText(g, t.Level.ToString(), nameFont, new RectangleF(cx - r, cy - r, badge, badge), Color.White, StringAlignment.Center, false);
    }
  }

  // Fills a bar for cur/max; with a font, also writes "cur / max  (pct%)" centred on it with a 1px shadow
  static void ClassicBar(Graphics g, Rectangle bar, int cur, int max, Color top, Color bottom, Color back, Font font, double trail = -1) {
    double frac = Frac(cur, max);
    using (var b = new SolidBrush(back)) g.FillRectangle(b, bar);
    if (trail > frac) using (var tb = new SolidBrush(TrailColor(top))) g.FillRectangle(tb, bar.X, bar.Y, (float)(bar.Width * trail), bar.Height);
    if (frac > 0)
      using (var fill = new LinearGradientBrush(bar, top, bottom, LinearGradientMode.Vertical))
        g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * frac), bar.Height);
    using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(edge, bar);
    if (font == null) return;
    // the thin Action/Mind bars get just the numbers
    string text = bar.Height < 14 ? string.Format("{0:N0} / {1:N0}", cur, max) : string.Format("{0:N0} / {1:N0}  ({2:0}%)", cur, max, frac * 100);
    DrawText(g, text, font, bar, Color.White, StringAlignment.Center, true);
  }

  // ---------- 6. Questlog: the DPS meter's Questlog look (dps-meter.cs) ----------
  // Rounded gray panel, rows on a dark track filled with a gradient of the bar colour (22% to 42% into the track),
  // ending in a 2px line of the full colour with a soft glow; Encode Sans with a 1px black text shadow.

  // Brightness levels (fill gradient from/to % of the bar colour, track grey, border grey); level 0 is the DPS meter's own
  static readonly int[][] QlLevels = { new[] { 22, 42, 24, 43 }, new[] { 40, 65, 32, 58 }, new[] { 55, 82, 38, 70 }, new[] { 70, 95, 44, 84 }, new[] { 80, 100, 36, 64 }, new[] { 50, 72, 30, 54 } };
  static int qlLevel = 5; // halfway between the DPS meter's dim fill (0) and full colour (4)
  static int[] QL { get { return QlLevels[qlLevel]; } }
  internal static Color QlTrack { get { return Color.FromArgb(QL[2], QL[2], QL[2]); } }
  internal static Color QlBorder { get { return Color.FromArgb(QL[3], QL[3], QL[3]); } }
  internal static readonly Color QlText = Color.FromArgb(250, 250, 250), QlDark = Color.FromArgb(220, 220, 220), QlDarker = Color.FromArgb(175, 175, 175);
  internal static readonly Color QlAccent = Color.FromArgb(66, 153, 225);
  // Bar colours (health, action, mind) per palette; 0 is the DPS meter's own soft Questlog colours
  static readonly string[] QlPaletteNames = { "Questlog", "Vivid", "Neon", "Game" };
  static readonly Color[][] QlPalettes = {
    new[] { Color.FromArgb(245, 101, 101), Color.FromArgb(72, 187, 120), Color.FromArgb(66, 153, 225) },
    new[] { Color.FromArgb(255, 64, 64), Color.FromArgb(46, 220, 90), Color.FromArgb(48, 140, 255) },
    new[] { Color.FromArgb(255, 40, 80), Color.FromArgb(40, 255, 120), Color.FromArgb(40, 190, 255) },
    new[] { Color.FromArgb(235, 45, 38), Color.FromArgb(70, 205, 70), Color.FromArgb(70, 110, 255) },
  };
  static int qlPalette = LoadQlPalette();
  static readonly string qlFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-ql.cfg");

  // The Questlog style's bar colours, shared by the target and self overlays; saved in swgr-ql.cfg ("Vivid" by default)
  public static string[] QlColorNames { get { return QlPaletteNames; } }
  public static int QlColors {
    get { return qlPalette; }
    set { qlPalette = value; try { File.WriteAllText(qlFile, QlPaletteNames[value]); } catch { } if (QlColorsChanged != null) QlColorsChanged(); }
  }
  public static event Action QlColorsChanged;
  static int LoadQlPalette() {
    try { int i = Array.IndexOf(QlPaletteNames, File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-ql.cfg")).Trim()); if (i >= 0) return i; } catch { }
    return 1;
  }
  internal static Color QlHealth { get { return QlPalettes[qlPalette][0]; } }
  internal static Color QlAction { get { return QlPalettes[qlPalette][1]; } }
  internal static Color QlMind { get { return QlPalettes[qlPalette][2]; } }
  Font qlName, qlNum, qlSmall, qlTiny;

  protected override float CornerRadius { get { return style == 5 ? 10 * scale * UiLook.Dpi : 0; } }

  internal static Color QlMix(Color c, int pct, Color with) {
    return Color.FromArgb((c.R * pct + with.R * (100 - pct)) / 100, (c.G * pct + with.G * (100 - pct)) / 100, (c.B * pct + with.B * (100 - pct)) / 100);
  }

  internal static void QlShadowText(Graphics g, string s, Font f, RectangleF r, Color c, StringAlignment h) {
    DrawText(g, s, f, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), Color.FromArgb(220, 0, 0, 0), h, false);
    DrawText(g, s, f, r, c, h, false);
  }

  internal static string QlInitial(string name) {
    var n = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"^(a|an|the)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?";
  }

  // One Questlog row: track, damage trail, gradient fill, glow and the 2px edge
  internal static void QlBar(Graphics g, RectangleF rc, double frac, double trail, Color c, float radius) {
    using (var path = LayeredForm.RoundRect(rc, radius)) {
      using (var b = new SolidBrush(QlTrack)) g.FillPath(b, path);
      var clip = g.Clip;
      g.SetClip(path, CombineMode.Intersect);
      if (trail > frac) using (var tb = new SolidBrush(QlMix(c, Math.Min(100, QL[1] + 15), QlTrack))) g.FillRectangle(tb, rc.X, rc.Y, (float)(rc.Width * trail), rc.Height);
      float fw = (float)(rc.Width * frac);
      if (fw >= 1) {
        using (var lg = new LinearGradientBrush(new RectangleF(rc.X - 1, rc.Y, fw + 2, rc.Height), QlMix(c, QL[0], QlTrack), QlMix(c, QL[1], QlTrack), LinearGradientMode.Horizontal))
          g.FillRectangle(lg, rc.X, rc.Y, fw, rc.Height);
        float gx = Math.Max(rc.X, rc.X + fw - 10);
        using (var glow = new LinearGradientBrush(new RectangleF(gx - 1, rc.Y, rc.X + fw - gx + 2, rc.Height), Color.FromArgb(0, c), Color.FromArgb(89, c), LinearGradientMode.Horizontal))
          g.FillRectangle(glow, gx, rc.Y, rc.X + fw - gx, rc.Height);
        using (var edge = new SolidBrush(c)) g.FillRectangle(edge, rc.X + fw - 2, rc.Y, 2, rc.Height);
      }
      g.Clip = clip;
    }
  }

  void QlFrame(Graphics g, float h) {
    using (var path = LayeredForm.RoundRect(new RectangleF(0.5f, 0.5f, BaseW - 1, h - 1), 10))
    using (var p = new Pen(QlBorder)) g.DrawPath(p, path);
  }

  // Header: initial badge, name, then level and distance on the right
  void QlHeader(Graphics g, TargetInfo t, Color badgeColor) {
    var badge = new RectangleF(9, 5, 18, 18);
    using (var b = new SolidBrush(QlMix(badgeColor, 70, QlTrack))) g.FillEllipse(b, badge);
    DrawText(g, QlInitial(t.Name), qlTiny, new RectangleF(badge.X, badge.Y + 0.5f, badge.Width, badge.Height), QlText, StringAlignment.Center, false);
    string dist = Dist(t), lv = t.Level > 0 ? "Lv " + t.Level : "";
    float dw = dist.Length > 0 ? g.MeasureString(dist, qlNum).Width : 0, lw = lv.Length > 0 ? g.MeasureString(lv, qlSmall).Width : 0;
    float right = BaseW - 10;
    if (dw > 0) DrawText(g, dist, qlNum, new RectangleF(right - dw, 4, dw + 2, 20), QlAccent, StringAlignment.Far, false);
    if (lw > 0) DrawText(g, lv, qlSmall, new RectangleF(right - dw - 8 - lw, 5, lw + 2, 20), QlDarker, StringAlignment.Far, false);
    QlShadowText(g, t.Name, qlName, new RectangleF(32, 4, right - dw - lw - 46, 20), QlText, StringAlignment.Near);
  }

  void PaintQuestlog(Graphics g, TargetInfo t) {
    QlFrame(g, BaseH);
    QlHeader(g, t, self ? QlAccent : QlHealth);
    double hp = Frac(t.Health, t.HealthMax);
    bool pools = t.HasPools;
    var hr = new RectangleF(8, 27, BaseW - 16, pools ? 21 : 35);
    QlBar(g, hr, hp, Tr("hp", hp), QlHealth, 6);
    QlShadowText(g, string.Format("{0:N0} / {1:N0}", t.Health, t.HealthMax), qlNum, new RectangleF(hr.X + 8, hr.Y, hr.Width - 60, hr.Height), QlText, StringAlignment.Near);
    QlShadowText(g, string.Format("{0:0}%", hp * 100), qlSmall, new RectangleF(hr.Right - 58, hr.Y, 50, hr.Height), QlDark, StringAlignment.Far);
    if (!pools) return;
    float half = (BaseW - 16 - 6) / 2f;
    QlPool(g, new RectangleF(8, 51, half, 14), t.Action, t.ActionMax, QlAction, "ac");
    QlPool(g, new RectangleF(8 + half + 6, 51, half, 14), t.Mind, t.MindMax, QlMind, "md");
  }

  void QlPool(Graphics g, RectangleF rc, int cur, int max, Color c, string key) {
    double f = Frac(cur, max);
    QlBar(g, rc, f, Tr(key, f), c, 4);
    QlShadowText(g, string.Format("{0:N0} / {1:N0}", cur, max), qlTiny, new RectangleF(rc.X + 6, rc.Y - 0.5f, rc.Width - 40, rc.Height), QlDark, StringAlignment.Near);
    QlShadowText(g, string.Format("{0:0}%", f * 100), qlTiny, new RectangleF(rc.Right - 40, rc.Y - 0.5f, 34, rc.Height), QlDarker, StringAlignment.Far);
  }

  void PaintShipQuestlog(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 5);
    QlFrame(g, h);
    QlHeader(g, t, QlHealth);
    DrawText(g, ShipSub(t, "  ·  "), qlSmall, new RectangleF(32, 21, BaseW - 42, 16), QlDarker, StringAlignment.Near, false);
    var rows = ShipRows(t.Ship);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      var rc = new RectangleF(8, ShipTop + i * ShipRowH + 0.5f, BaseW - 16, ShipRowH - 2);
      QlBar(g, rc, r.Frac, Tr("s:" + r.Label, r.Frac), r.Sub ? SubColor : r.Color, 4);
      QlShadowText(g, r.Label, qlTiny, new RectangleF(rc.X + 6, rc.Y - 0.5f, 120, rc.Height), r.Sub ? SubColor : QlText, StringAlignment.Near);
      QlShadowText(g, Nums(r), qlTiny, new RectangleF(rc.Right - 150, rc.Y - 0.5f, 104, rc.Height), QlDark, StringAlignment.Far);
      QlShadowText(g, Pct(r), qlTiny, new RectangleF(rc.Right - 42, rc.Y - 0.5f, 36, rc.Height), r.Sub ? SubColor : QlText, StringAlignment.Far);
    }
  }

  // ---------- 2. Holo: cyan hologram with chamfered frame, scanlines and segmented bars ----------

  static readonly Color Holo = Color.FromArgb(90, 225, 255);

  static GraphicsPath Chamfer(RectangleF r, float c) {
    var p = new GraphicsPath();
    p.AddPolygon(new[] {
      new PointF(r.Left + c, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom - c),
      new PointF(r.Right - c, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Top + c),
    });
    return p;
  }

  void PaintHolo(Graphics g, TargetInfo t) {
    // Scanlines and a soft inner glow
    using (var line = new Pen(Color.FromArgb(14, Holo))) for (int y = 1; y < BaseH; y += 3) g.DrawLine(line, 0, y, BaseW, y);
    using (var frame = Chamfer(new RectangleF(1.5f, 1.5f, BaseW - 3, BaseH - 3), 10)) {
      using (var glow = new Pen(Color.FromArgb(45, Holo), 4f)) g.DrawPath(glow, frame);
      using (var edge = new Pen(Color.FromArgb(200, Holo), 1.2f)) g.DrawPath(edge, frame);
    }

    // Header: name in caps, level and distance as tags
    DrawText(g, t.Name.ToUpperInvariant(), holoFont, new RectangleF(14, 3, 200, 17), Holo, StringAlignment.Near, false);
    string tag = (t.Level > 0 ? "LV " + t.Level : "") + (t.Distance >= 0 ? "   " + Math.Round(t.Distance) + " M" : "");
    DrawText(g, tag, holoSmall, new RectangleF(200, 4, BaseW - 214, 16), Color.FromArgb(170, Holo), StringAlignment.Far, false);
    using (var sep = new Pen(Color.FromArgb(80, Holo))) g.DrawLine(sep, 12, 21, BaseW - 12, 21);

    HoloRow(g, 25, "HP", t.Health, t.HealthMax, Color.FromArgb(255, 120, 110), 13, true, Tr("hp", Frac(t.Health, t.HealthMax)));
    if (t.HasPools) HoloRow(g, 41, "AC", t.Action, t.ActionMax, Color.FromArgb(120, 255, 170), 9, true, Tr("ac", Frac(t.Action, t.ActionMax)));
    if (t.HasPools) HoloRow(g, 53, "MD", t.Mind, t.MindMax, Color.FromArgb(150, 170, 255), 9, true, Tr("md", Frac(t.Mind, t.MindMax)));
  }

  void HoloRow(Graphics g, float y, string label, int cur, int max, Color tint, float h, bool numbers, double trail = -1) {
    DrawText(g, label, holoSmall, new RectangleF(12, y - 2, 22, h + 4), Color.FromArgb(170, Holo), StringAlignment.Near, false);
    const int segs = 24;
    float x0 = 34, x1 = numbers ? 238 : BaseW - 14, segW = (x1 - x0) / segs;
    int lit = (int)Math.Ceiling(Frac(cur, max) * segs - 0.001), trailLit = (int)Math.Ceiling(trail * segs - 0.001);
    for (int i = 0; i < segs; i++) {
      var r = new RectangleF(x0 + i * segW + 1, y, segW - 2, h);
      bool on = i < lit;
      using (var b = new SolidBrush(on ? Color.FromArgb(220, tint) : i < trailLit ? TrailColor(tint) : Color.FromArgb(28, Holo))) g.FillRectangle(b, r);
      if (on) using (var glow = new Pen(Color.FromArgb(60, tint), 2f)) g.DrawRectangle(glow, r.X, r.Y, r.Width, r.Height);
    }
    if (numbers)
      DrawText(g, cur.ToString("N0") + " / " + max.ToString("N0"), holoSmall, new RectangleF(x1 + 4, y - 2, BaseW - x1 - 16, h + 4), Holo, StringAlignment.Far, false);
  }

  // ---------- 3. Minimal: big percentage, quiet typography, hairline bars ----------

  void PaintMinimal(Graphics g, TargetInfo t) {
    double hp = Frac(t.Health, t.HealthMax);
    Color accent = hp > 0.5 ? Color.FromArgb(240, 242, 245) : hp > 0.25 ? Color.FromArgb(255, 196, 92) : Color.FromArgb(255, 92, 92);
    DrawText(g, Math.Round(hp * 100) + "%", minBig, new RectangleF(4, 2, 100, 46), accent, StringAlignment.Center, false);
    DrawText(g, t.Health.ToString("N0") + " / " + t.HealthMax.ToString("N0"), minSmall, new RectangleF(4, 46, 100, 18), Color.FromArgb(130, 136, 146), StringAlignment.Center, false);
    using (var div = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(div, 110, 10, 110, BaseH - 10);

    DrawText(g, t.Name, minName, new RectangleF(120, 4, BaseW - 128, 20), Color.FromArgb(240, 242, 245), StringAlignment.Near, false);
    var meta = (t.Level > 0 ? "Level " + t.Level : "") + (t.Distance >= 0 ? "  ·  " + Math.Round(t.Distance) + " m" : "");
    DrawText(g, meta, minSmall, new RectangleF(120, 22, BaseW - 128, 14), Color.FromArgb(130, 136, 146), StringAlignment.Near, false);

    // hairlines close together, then Action and Mind as numbers (Health's are under the big percentage)
    Hairline(g, 120, 41, BaseW - 132, hp, Color.FromArgb(235, 84, 76), Tr("hp", hp));
    if (t.HasPools) Hairline(g, 120, 48, BaseW - 132, Frac(t.Action, t.ActionMax), Color.FromArgb(92, 200, 120), Tr("ac", Frac(t.Action, t.ActionMax)));
    if (t.HasPools) Hairline(g, 120, 55, BaseW - 132, Frac(t.Mind, t.MindMax), Color.FromArgb(104, 150, 255), Tr("md", Frac(t.Mind, t.MindMax)));
    if (t.HasPools) DrawText(g, "AC " + t.Action.ToString("N0") + " / " + t.ActionMax.ToString("N0") + "    MD " + t.Mind.ToString("N0") + " / " + t.MindMax.ToString("N0"),
      minSmall, new RectangleF(120, 57, BaseW - 128, 13), Color.FromArgb(130, 136, 146), StringAlignment.Near, false);
  }

  static void Hairline(Graphics g, float x, float y, float w, double frac, Color c, double trail = -1) {
    using (var track = new Pen(Color.FromArgb(38, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(track, x, y, x + w, y);
    if (trail > frac) using (var tp = new Pen(TrailColor(c), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(tp, x, y, x + (float)(w * trail), y);
    if (frac > 0)
      using (var fill = new Pen(c, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(fill, x, y, x + (float)(w * frac), y);
  }

  // ---------- 4. Terminal: amber CRT readout ----------

  static readonly Color Amber = Color.FromArgb(255, 176, 0);

  void PaintTerminal(Graphics g, TargetInfo t) {
    using (var line = new Pen(Color.FromArgb(18, Amber))) for (int y = 0; y < BaseH; y += 2) g.DrawLine(line, 0, y, BaseW, y);
    using (var edge = new Pen(Color.FromArgb(90, Amber))) g.DrawRectangle(edge, 0.5f, 0.5f, BaseW - 1, BaseH - 1);

    string head = "> " + t.Name;
    string tail = (t.Level > 0 ? "LV" + t.Level.ToString("00") : "") + (t.Distance >= 0 ? " " + Math.Round(t.Distance).ToString("000") + "M" : "");
    TermText(g, head, new RectangleF(6, 2, 230, 16), StringAlignment.Near);
    TermText(g, tail, new RectangleF(200, 2, BaseW - 206, 16), StringAlignment.Far);
    TermText(g, "HP " + TermBar(t.Health, t.HealthMax, Tr("hp", Frac(t.Health, t.HealthMax))) + " " + t.Health + "/" + t.HealthMax, new RectangleF(6, 18, BaseW - 12, 16), StringAlignment.Near);
    if (t.HasPools) TermText(g, "AC " + TermBar(t.Action, t.ActionMax, Tr("ac", Frac(t.Action, t.ActionMax))) + " " + t.Action + "/" + t.ActionMax, new RectangleF(6, 34, BaseW - 12, 16), StringAlignment.Near);
    if (t.HasPools) TermText(g, "MD " + TermBar(t.Mind, t.MindMax, Tr("md", Frac(t.Mind, t.MindMax))) + " " + t.Mind + "/" + t.MindMax, new RectangleF(6, 50, BaseW - 12, 16), StringAlignment.Near);
  }

  static string TermBar(int cur, int max, double trail = -1) { return TermBarF(Frac(cur, max), trail, 20); }

  // '#' filled, '=' the damage trail, '.' empty
  static string TermBarF(double frac, double trail, int n) {
    int lit = (int)Math.Round(frac * n), tl = Math.Max(lit, Math.Min(n, (int)Math.Round(trail * n)));
    return "[" + new string('#', lit) + new string('=', tl - lit) + new string('.', n - tl) + "]";
  }

  void TermText(Graphics g, string s, RectangleF r, StringAlignment h) {
    // A faint offset copy reads as phosphor bloom
    DrawText(g, s, termFont, new RectangleF(r.X + 0.6f, r.Y + 0.6f, r.Width, r.Height), Color.FromArgb(70, Amber), h, false);
    DrawText(g, s, termFont, r, Amber, h, false);
  }

  // ---------- 5. Radial: concentric ring gauges around the level ----------

  void PaintRadial(Graphics g, TargetInfo t) {
    const float cx = 36, cy = 35;
    Ring(g, cx, cy, 30, 6, Frac(t.Health, t.HealthMax), Color.FromArgb(235, 70, 60), Tr("hp", Frac(t.Health, t.HealthMax)));
    if (t.HasPools) Ring(g, cx, cy, 22, 4, Frac(t.Action, t.ActionMax), Color.FromArgb(80, 200, 100), Tr("ac", Frac(t.Action, t.ActionMax)));
    if (t.HasPools) Ring(g, cx, cy, 16, 4, Frac(t.Mind, t.MindMax), Color.FromArgb(90, 140, 255), Tr("md", Frac(t.Mind, t.MindMax)));
    DrawText(g, t.Level > 0 ? t.Level.ToString() : "", radLevel, new RectangleF(cx - 14, cy - 12, 28, 24), Color.White, StringAlignment.Center, false);

    DrawText(g, t.Name, radName, new RectangleF(76, 3, BaseW - 84, 22), TextColor, StringAlignment.Near, false);
    DrawText(g, t.Health.ToString("N0") + " / " + t.HealthMax.ToString("N0") + " HP", radHp, new RectangleF(76, 24, BaseW - 84, 20), Color.FromArgb(255, 120, 110), StringAlignment.Near, false);
    string sub = (t.HasPools ? "Action " + t.Action.ToString("N0") + " / " + t.ActionMax.ToString("N0") + "   Mind " + t.Mind.ToString("N0") + " / " + t.MindMax.ToString("N0") : "") +
      (t.Distance >= 0 ? "   " + Math.Round(t.Distance) + " m" : "");
    DrawText(g, sub, radSmall, new RectangleF(76, 45, BaseW - 84, 18), Color.FromArgb(140, 148, 160), StringAlignment.Near, false);
  }

  static void Ring(Graphics g, float cx, float cy, float r, float w, double frac, Color c, double trail = -1) {
    var rect = new RectangleF(cx - r + w / 2, cy - r + w / 2, 2 * r - w, 2 * r - w);
    using (var track = new Pen(Color.FromArgb(40, c), w)) g.DrawEllipse(track, rect);
    if (trail > frac) using (var ta = new Pen(TrailColor(c), w) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(ta, rect, -90, (float)(360 * trail));
    if (frac > 0)
      using (var arc = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, rect, -90, (float)(360 * frac));
  }

  // ---------- ship targets: component / Shields / Armor / Systems / Chassis in each style (320 x ShipHeight) ----------

  sealed class ShipRow { public string Label, Tag; public double Cur, Max; public Color Color; public bool Sub; public double Frac { get { return Max > 0 ? Math.Max(0, Math.Min(1, Cur / Max)) : 0; } } }

  static readonly Color SubColor = Color.FromArgb(160, 230, 60); // the game's green component line

  // The game's order: the targeted component first (when there is one), then the four totals
  static List<ShipRow> ShipRows(ShipInfo s) {
    var rows = new List<ShipRow>();
    if (s.Sub != null)
      rows.Add(new ShipRow { Label = ShipInfo.SlotName(s.Sub.Slot), Tag = "TG", Cur = s.Sub.Armor + s.Sub.Hp, Max = s.Sub.ArmorMax + s.Sub.HpMax, Color = SubColor, Sub = true });
    double ac = 0, am = 0, hc = 0, hm = 0;
    foreach (var p in s.Parts) if (ShipInfo.IsArmorSlot(p.Slot)) { ac += p.Armor; am += p.ArmorMax; } else { hc += p.Hp; hm += p.HpMax; }
    rows.Add(new ShipRow { Label = "Shields", Tag = "SH", Cur = s.ShieldFront + s.ShieldBack, Max = s.ShieldFrontMax + s.ShieldBackMax, Color = Color.FromArgb(80, 170, 255) });
    rows.Add(new ShipRow { Label = "Armor", Tag = "AR", Cur = ac, Max = am, Color = Color.FromArgb(230, 170, 60) });
    rows.Add(new ShipRow { Label = "Systems", Tag = "SY", Cur = hc, Max = hm, Color = Color.FromArgb(90, 200, 110) });
    rows.Add(new ShipRow { Label = "Chassis", Tag = "CH", Cur = s.Chassis, Max = s.ChassisMax, Color = Color.FromArgb(225, 70, 60) });
    return rows;
  }

  // Minimal puts each hairline under its text, so its rows are taller
  const float ShipTop = 40, ShipRowH = 14.5f, MinRowH = 18f;
  static int ShipHeight(ShipInfo s, int style) { return (int)Math.Ceiling(ShipTop + (s.Sub != null ? 5 : 4) * (style == 2 ? MinRowH : ShipRowH) + 4); }

  static string Nums(ShipRow r) { return r.Cur.ToString("N0") + " / " + r.Max.ToString("N0"); }
  static string Pct(ShipRow r) { return ShipInfo.Pct(r.Frac) + "%"; }

  static string ShipSub(TargetInfo t, string sep) {
    var parts = new List<string>();
    if (t.Ship.Type.Length > 0) parts.Add(t.Ship.Type);
    if (t.Ship.Tier.Length > 0) parts.Add(t.Ship.Tier);
    return string.Join(sep, parts);
  }

  static Color Scale(Color c, double k) { return Color.FromArgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k)); }

  void PaintShip(Graphics g, int s, TargetInfo t) {
    switch (s) {
      case 1: PaintShipHolo(g, t); break;
      case 2: PaintShipMinimal(g, t); break;
      case 3: PaintShipTerminal(g, t); break;
      case 4: PaintShipRadial(g, t); break;
      case 5: PaintShipQuestlog(g, t); break;
      default: PaintShipClassic(g, t); break;
    }
  }

  // Label | bar | current / max | percent
  void PaintShipClassic(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 0);
    using (var border = new Pen(Color.FromArgb(70, 90, 110))) g.DrawRectangle(border, 0, 0, BaseW - 1, h - 1);
    const int pad = 8;
    string dist = Dist(t);
    float distW = dist.Length > 0 ? g.MeasureString(dist, nameFont).Width : 0;
    DrawText(g, dist, nameFont, new RectangleF(pad, 2, BaseW - 2 * pad, 20), TextColor, StringAlignment.Far, false);
    DrawText(g, t.Name, nameFont, new RectangleF(pad, 2, BaseW - 2 * pad - distW - 4, 20), TextColor, StringAlignment.Near, false);
    DrawText(g, ShipSub(t, "  ·  "), hpFont, new RectangleF(pad, 20, BaseW - 2 * pad, 16), Color.FromArgb(150, 160, 172), StringAlignment.Near, false);
    var rows = ShipRows(t.Ship);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      float y = ShipTop + i * ShipRowH;
      DrawText(g, r.Label, hpFont, new RectangleF(pad, y - 1, 66, 14), r.Sub ? SubColor : TextColor, StringAlignment.Near, false);
      var bar = new Rectangle(76, (int)y + 2, 98, 9);
      using (var b = new SolidBrush(Scale(r.Color, 0.22))) g.FillRectangle(b, bar);
      double tr = Tr("s:" + r.Label, r.Frac);
      if (tr > r.Frac) using (var tb = new SolidBrush(TrailColor(r.Color))) g.FillRectangle(tb, bar.X, bar.Y, (float)(bar.Width * tr), bar.Height);
      if (r.Frac > 0)
        using (var fill = new LinearGradientBrush(bar, r.Color, Scale(r.Color, 0.65), LinearGradientMode.Vertical))
          g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * r.Frac), bar.Height);
      using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawRectangle(edge, bar);
      DrawText(g, Nums(r), radSmall, new RectangleF(176, y - 1, 98, 14), Color.FromArgb(170, 178, 188), StringAlignment.Far, false);
      DrawText(g, Pct(r), hpFont, new RectangleF(BaseW - pad - 40, y - 1, 40, 14), r.Sub ? SubColor : TextColor, StringAlignment.Far, false);
    }
  }

  void PaintShipHolo(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 1);
    using (var line = new Pen(Color.FromArgb(14, Holo))) for (int y = 1; y < h; y += 3) g.DrawLine(line, 0, y, BaseW, y);
    using (var frame = Chamfer(new RectangleF(1.5f, 1.5f, BaseW - 3, h - 3), 10)) {
      using (var glow = new Pen(Color.FromArgb(45, Holo), 4f)) g.DrawPath(glow, frame);
      using (var edge = new Pen(Color.FromArgb(200, Holo), 1.2f)) g.DrawPath(edge, frame);
    }
    DrawText(g, t.Name.ToUpperInvariant(), holoFont, new RectangleF(14, 3, 220, 17), Holo, StringAlignment.Near, false);
    DrawText(g, t.Distance >= 0 ? Math.Round(t.Distance) + " M" : "", holoSmall, new RectangleF(220, 4, BaseW - 234, 16), Color.FromArgb(170, Holo), StringAlignment.Far, false);
    DrawText(g, ShipSub(t, "   ").ToUpperInvariant(), holoSmall, new RectangleF(14, 19, BaseW - 28, 14), Color.FromArgb(170, Holo), StringAlignment.Near, false);
    using (var sep = new Pen(Color.FromArgb(80, Holo))) g.DrawLine(sep, 12, 35, BaseW - 12, 35);
    var rows = ShipRows(t.Ship);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      float y = ShipTop + i * ShipRowH;
      Color tint = r.Sub ? SubColor : Color.FromArgb(Math.Min(255, r.Color.R + 40), Math.Min(255, r.Color.G + 40), Math.Min(255, r.Color.B + 40));
      DrawText(g, r.Sub ? r.Label.ToUpperInvariant() : r.Tag, holoSmall, new RectangleF(12, y - 2, r.Sub ? 80 : 22, 14), r.Sub ? SubColor : Color.FromArgb(170, Holo), StringAlignment.Near, false);
      const int segs = 16;
      float x0 = r.Sub ? 94 : 34, x1 = 166, segW = (x1 - x0) / segs;
      int lit = (int)Math.Ceiling(r.Frac * segs - 0.001), trailLit = (int)Math.Ceiling(Tr("s:" + r.Label, r.Frac) * segs - 0.001);
      for (int k = 0; k < segs; k++) {
        var rr = new RectangleF(x0 + k * segW + 1, y, segW - 2, 10);
        bool on = k < lit;
        using (var b = new SolidBrush(on ? Color.FromArgb(220, tint) : k < trailLit ? TrailColor(tint) : Color.FromArgb(28, Holo))) g.FillRectangle(b, rr);
        if (on) using (var glow = new Pen(Color.FromArgb(60, tint), 2f)) g.DrawRectangle(glow, rr.X, rr.Y, rr.Width, rr.Height);
      }
      DrawText(g, Nums(r), holoSmall, new RectangleF(168, y - 2, 100, 14), Color.FromArgb(170, Holo), StringAlignment.Far, false);
      DrawText(g, Pct(r), holoSmall, new RectangleF(268, y - 2, BaseW - 280, 14), r.Sub ? SubColor : Holo, StringAlignment.Far, false);
    }
  }

  void PaintShipMinimal(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 2);
    double hull = t.Ship.Hull;
    Color accent = hull > 0.5 ? Color.FromArgb(240, 242, 245) : hull > 0.25 ? Color.FromArgb(255, 196, 92) : Color.FromArgb(255, 92, 92);
    DrawText(g, ShipInfo.Pct(hull) + "%", minBig, new RectangleF(4, h / 2 - 32, 100, 46), accent, StringAlignment.Center, false);
    DrawText(g, "chassis", minSmall, new RectangleF(4, h / 2 + 12, 100, 18), Color.FromArgb(130, 136, 146), StringAlignment.Center, false);
    using (var div = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(div, 110, 10, 110, h - 10);

    DrawText(g, t.Name, minName, new RectangleF(120, 4, BaseW - 128, 20), Color.FromArgb(240, 242, 245), StringAlignment.Near, false);
    var meta = ShipSub(t, "  ·  ") + (t.Distance >= 0 ? "  ·  " + Math.Round(t.Distance) + " m" : "");
    DrawText(g, meta, minSmall, new RectangleF(120, 22, BaseW - 128, 14), Color.FromArgb(130, 136, 146), StringAlignment.Near, false);
    var rows = ShipRows(t.Ship);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      float y = ShipTop + 2 + i * MinRowH;
      // label and numbers on one line, the hairline right under them
      DrawText(g, r.Label, minSmall, new RectangleF(120, y - 7, 80, 12), r.Sub ? SubColor : Color.FromArgb(160, 166, 176), StringAlignment.Near, false);
      DrawText(g, Nums(r) + "   " + Pct(r), minSmall, new RectangleF(180, y - 7, BaseW - 188, 12), r.Sub ? SubColor : Color.FromArgb(200, 204, 210), StringAlignment.Far, false);
      Hairline(g, 121, y + 7, BaseW - 130, r.Frac, r.Color, Tr("s:" + r.Label, r.Frac));
    }
  }

  void PaintShipTerminal(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 3);
    using (var line = new Pen(Color.FromArgb(18, Amber))) for (int y = 0; y < h; y += 2) g.DrawLine(line, 0, y, BaseW, y);
    using (var edge = new Pen(Color.FromArgb(90, Amber))) g.DrawRectangle(edge, 0.5f, 0.5f, BaseW - 1, h - 1);
    TermText(g, "> " + t.Name, new RectangleF(6, 2, 240, 16), StringAlignment.Near);
    TermText(g, t.Distance >= 0 ? Math.Round(t.Distance).ToString("000") + "M" : "", new RectangleF(240, 2, BaseW - 246, 16), StringAlignment.Far);
    TermText(g, "  " + ShipSub(t, " / ").ToUpperInvariant(), new RectangleF(6, 18, BaseW - 12, 16), StringAlignment.Near);
    var rows = ShipRows(t.Ship);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      // every tag is 3 chars wide so the bars line up; ">" marks the targeted component
      string tag = r.Sub ? ">" + r.Label.Substring(0, Math.Min(2, r.Label.Length)).ToUpperInvariant() : " " + r.Tag;
      string bar = TermBarF(r.Frac, Tr("s:" + r.Label, r.Frac), 12);
      float y = ShipTop - 6 + i * ShipRowH;
      TermText(g, tag + " " + bar + " " + Pct(r).PadLeft(4), new RectangleF(6, y, BaseW - 12, 16), StringAlignment.Near);
      TermText(g, r.Cur.ToString("0") + "/" + r.Max.ToString("0"), new RectangleF(6, y, BaseW - 12, 16), StringAlignment.Far);
    }
  }

  void PaintShipRadial(Graphics g, TargetInfo t) {
    int h = ShipHeight(t.Ship, 4);
    float cx = 50, cy = h / 2f;
    var rows = ShipRows(t.Ship);
    var totals = rows.FindAll(r => !r.Sub);
    // Outside in, the order damage goes through: shields, armor, systems, chassis
    for (int i = 0; i < totals.Count; i++) Ring(g, cx, cy, 44 - i * 8, 5, totals[i].Frac, totals[i].Color, Tr("s:" + totals[i].Label, totals[i].Frac));
    DrawText(g, Pct(totals[3]), radSmall, new RectangleF(cx - 16, cy - 10, 32, 20), Color.White, StringAlignment.Center, false);

    string dist = t.Distance >= 0 ? Math.Round(t.Distance) + " m" : "";
    DrawText(g, dist, radSmall, new RectangleF(104, 6, BaseW - 112, 18), Color.FromArgb(140, 148, 160), StringAlignment.Far, false);
    DrawText(g, t.Name, radName, new RectangleF(104, 4, BaseW - 150, 22), TextColor, StringAlignment.Near, false);
    DrawText(g, ShipSub(t, "  ·  "), radSmall, new RectangleF(104, 23, BaseW - 112, 16), Color.FromArgb(140, 148, 160), StringAlignment.Near, false);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      float y = ShipTop + i * ShipRowH;
      using (var dot = new SolidBrush(r.Color)) g.FillEllipse(dot, 104, y + 3, 7, 7);
      DrawText(g, r.Label, radSmall, new RectangleF(115, y - 2, 80, 16), r.Sub ? SubColor : TextColor, StringAlignment.Near, false);
      DrawText(g, Nums(r), radSmall, new RectangleF(170, y - 2, 100, 16), Color.FromArgb(150, 158, 170), StringAlignment.Far, false);
      DrawText(g, Pct(r), radSmall, new RectangleF(270, y - 2, BaseW - 278, 16), r.Sub ? SubColor : TextColor, StringAlignment.Far, false);
    }
  }

  // ---------- window behaviour ----------

  readonly ToolTip tooltip = MakeTooltip();
  string lastTip;

  void HideTip() {
    if (lastTip == null) return;
    lastTip = null;
    tooltip.Hide(this);
  }

  protected override void OnMouseLeave(EventArgs e) {
    HideTip();
    base.OnMouseLeave(e);
  }

  // Dark tooltip like the game's buff tooltip; the time line is refreshed every tick while it's open
  static ToolTip MakeTooltip() {
    var t = new ToolTip { OwnerDraw = true, UseAnimation = false, UseFading = false };
    var font = new Font("Segoe UI", 9f);
    t.Popup += (s, e) => { var sz = TextRenderer.MeasureText(StripColors(t.GetToolTip(e.AssociatedControl) ?? ""), font); e.ToolTipSize = new Size(sz.Width + 16, sz.Height + 10); };
    t.Draw += (s, e) => {
      using (var bg = new SolidBrush(Color.FromArgb(24, 40, 44))) e.Graphics.FillRectangle(bg, e.Bounds);
      using (var edge = new Pen(Color.FromArgb(70, 140, 140))) e.Graphics.DrawRectangle(edge, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
      DrawColored(e.Graphics, e.ToolTipText, font, new Point(8, 5), TipText);
    };
    return t;
  }

  static readonly Color TipText = Color.FromArgb(225, 232, 235);
  static readonly System.Text.RegularExpressions.Regex ColorCode = new System.Text.RegularExpressions.Regex(@"\\#([0-9A-Fa-f]{6})");

  // Game strings change colour mid-line with \#RRGGBB (e.g. "\#FFFFFFRelentless \#00FF66[*****]")
  internal static string StripColors(string s) { return ColorCode.Replace(s ?? "", ""); }

  // Draws text, switching colour at each \#RRGGBB code; white (the game's "normal") stays the tooltip's text colour
  internal static void DrawColored(Graphics g, string text, Font font, Point at, Color normal) {
    const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    int lineH = TextRenderer.MeasureText(g, "Ag", font, Size.Empty, TextFormatFlags.NoPrefix).Height;
    Color c = normal;
    int y = at.Y;
    foreach (var line in (text ?? "").Replace("\r", "").Split('\n')) {
      int x = at.X, pos = 0;
      // Leading padding as in a plain DrawText, so colored and plain lines line up
      x += TextRenderer.MeasureText(g, "A", font, Size.Empty, TextFormatFlags.NoPrefix).Width - TextRenderer.MeasureText(g, "A", font, Size.Empty, F).Width >> 1;
      foreach (System.Text.RegularExpressions.Match m in ColorCode.Matches(line)) {
        x += Seg(g, line.Substring(pos, m.Index - pos), font, x, y, c, F);
        int rgb = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
        c = rgb == 0xFFFFFF ? normal : Color.FromArgb(255, Color.FromArgb(rgb));
        pos = m.Index + m.Length;
      }
      Seg(g, line.Substring(pos), font, x, y, c, F);
      y += lineH;
    }
  }

  static int Seg(Graphics g, string s, Font font, int x, int y, Color c, TextFormatFlags f) {
    if (s.Length == 0) return 0;
    TextRenderer.DrawText(g, s, font, new Point(x, y), c, f);
    return TextRenderer.MeasureText(g, s, font, new Size(int.MaxValue, int.MaxValue), f).Width;
  }

  bool InGrip(Point p) { return p.X >= Width - Grip && p.Y >= Height - Grip; }

  protected override void OnMouseDown(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left) { resizing = InGrip(e.Location); dragFrom = e.Location; }
    base.OnMouseDown(e);
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left && resizing) {
      scale = Math.Max(MinW, Math.Min(MaxW, (e.X + Grip / 2) / UiLook.Dpi)) / BaseW;
      ApplyScale();
      Invalidate();
    } else if (e.Button == MouseButtons.Left) {
      Location = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
    } else {
      Cursor = InGrip(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
      // Buff name under the mouse (hit boxes are in design units)
      float k = scale * UiLook.Dpi;
      string tip = null;
      if (info != null && info.Ship != null) tip = info.Ship.Tooltip(); // per-component breakdown anywhere on the ship panel
      else foreach (var hb in buffHits) if (hb.Item1.Contains(e.X / k, e.Y / k)) { tip = BuffBook.Tooltip(hb.Item2, hb.Item3); break; }
      if (tip != lastTip) { lastTip = tip; if (tip == null) tooltip.Hide(this); else tooltip.Show(tip, this, e.X + 12, Height + 4); } // below the window: it re-asserts topmost every tick and would cover the tooltip
    }
    base.OnMouseMove(e);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left) { resizing = false; SaveSettings(); }
    base.OnMouseUp(e);
  }

  // Never take focus from the game when shown
  protected override bool ShowWithoutActivation { get { return true; } }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
      return cp;
    }
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    timer.Stop();
    animTimer.Stop();
    if (tray != null) { tray.Visible = false; tray.Dispose(); }
    reader.Dispose();
    UiLook.Changed -= OnLook;
    base.OnFormClosed(e);
  }

  [STAThread]
  internal static void Main(string[] args) {
    UiLook.EnableDpi();
    Application.EnableVisualStyles();
    if (args.Length == 2 && args[0] == "--render-trail") {
      using (var f = new OverlayForm()) { f.timer.Stop(); f.animTimer.Stop(); f.tray.Visible = false; f.RenderTrail(args[1]); }
      return;
    }
    if (args.Length == 2 && args[0] == "--render-aggro") {
      using (var f = new OverlayForm()) { f.timer.Stop(); f.tray.Visible = false; f.RenderAggro(args[1]); }
      return;
    }
    if (args.Length == 2 && args[0] == "--render-ql") { // the Questlog style at each brightness level
      using (var f = new OverlayForm()) { f.timer.Stop(); f.tray.Visible = false; f.RenderQl(args[1]); }
      return;
    }
    if (args.Length == 2 && args[0] == "--render") {
      using (var f = new OverlayForm()) { f.timer.Stop(); f.tray.Visible = false; f.RenderAll(args[1]); }
      return;
    }
    Application.Run(new OverlayForm());
  }
}
