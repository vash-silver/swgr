// Read-only ship component scanner for the running SWG client.
// Walks the player's containers (inventory, backpacks, anything slotted on the player), looks every item up in
// the client's examine-attribute cache, and rates ship components against the 1M+ credit value sheet
// (same table and verdict rules as the user's "SWGR Part Checker" page and the swgr-ship-part-evaluator skill).
//
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only, so it cannot write,
// inject, suspend or debug. This file must never call WriteProcessMemory, VirtualProtectEx,
// CreateRemoteThread, DebugActiveProcess or open the process with any other access right.
//
// build: cd tools && csc -nologo -platform:x64 -optimize -out:../bin/swgr-parts.exe parts.cs
// usage: swgr-parts [-v] [--all] [--selected] [--vendor]   (-v every stat; --all also cached components outside your containers;
//                                             --selected only the item shown in the inventory info panel)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static partial class Parts {
  const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;

  // Offsets for the current DX12 client build (see notes/memory.md)
  const ulong RVA_PLAYER = 0x21f22c0;        // CreatureObject* of the player
  const ulong RVA_ATTR_MAP = 0x2229418;      // ObjectAttributeManager: std::map<NetworkId, {vector<pair<string, wstring>>, int revision}> head, size at +8
  const ulong RVA_VENDOR_DETAILS = 0x222d4a8; // vendor/bazaar "Item Details": std::map<NetworkId, details> head, size at +8 (notes/memory.md)
  const ulong VT_CREATURE = 0x1cf7648, VT_SLOTTED = 0x1d34488, VT_VOLUME = 0x1d34578;
  const int OBJ_ID = 0x30, OBJ_CONTAINER = 0xb8, OBJ_NAME = 0x280;
  const int CONT_BEGIN = 0x20, CONT_END = 0x28, CONT_ENTRY = 0x18, ENTRY_OBJ = 0x10;

  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte[] buf, IntPtr size, out IntPtr read);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int needed, uint filter);

  static IntPtr h;
  static ulong baseAddr;

  static byte[] Read(ulong addr, int len) {
    if (addr < 0x10000 || addr > 0x7fffffffffff) return null;
    var b = new byte[len];
    IntPtr n;
    return ReadProcessMemory(h, addr, b, (IntPtr)len, out n) && (int)n == len ? b : null;
  }

  static ulong Ptr(ulong addr) { var b = Read(addr, 8); return b == null ? 0 : BitConverter.ToUInt64(b, 0); }

  static bool IsA(ulong obj, ulong vtRva) { return obj != 0 && Ptr(obj) == baseAddr + vtRva; }

  static string WStr(ulong addr) {
    var b = Read(addr, 256);
    if (b == null) return null;
    var s = Encoding.Unicode.GetString(b);
    int z = s.IndexOf((char)0);
    return z >= 0 ? s.Substring(0, z) : s;
  }

  // MSVC x64 std::basic_string: 16-byte buffer-or-pointer, size at +0x10, capacity at +0x18
  static string StdStr(byte[] b, int at, bool wide) {
    long size = BitConverter.ToInt64(b, at + 16), cap = BitConverter.ToInt64(b, at + 24);
    if (size < 0 || size > 4096 || cap < size) return null;
    int bytes = (int)size * (wide ? 2 : 1);
    byte[] data; int from = 0;
    if (cap < (wide ? 8 : 16)) { data = b; from = at; } else data = Read(BitConverter.ToUInt64(b, at), bytes);
    if (data == null) return null;
    return wide ? Encoding.Unicode.GetString(data, from, bytes) : Encoding.ASCII.GetString(data, from, bytes);
  }

  // ---------- attribute cache ----------

  static Dictionary<ulong, Dictionary<string, string>> ReadAttrCache() {
    var result = new Dictionary<ulong, Dictionary<string, string>>();
    ulong head = Ptr(baseAddr + RVA_ATTR_MAP);
    long size = (long)Ptr(baseAddr + RVA_ATTR_MAP + 8);
    if (head == 0 || size <= 0 || size > 100000) return result;
    // In-order walk of the red-black tree. Node: left, parent, right, color, isnil(+0x19), key(+0x20), value(+0x28)
    var stack = new Stack<ulong>();
    ulong node = Ptr(head + 8); // root
    int guard = 0;
    while ((stack.Count > 0 || !IsNil(node)) && guard++ < 4 * size + 16) {
      if (!IsNil(node)) { stack.Push(node); node = Ptr(node); continue; }
      node = stack.Pop();
      var nb = Read(node + 0x20, 0x18);
      if (nb != null) {
        ulong id = BitConverter.ToUInt64(nb, 0), beg = BitConverter.ToUInt64(nb, 8), end = BitConverter.ToUInt64(nb, 16);
        if (end > beg && (end - beg) % 64 == 0 && end - beg <= 64 * 512) {
          var vb = Read(beg, (int)(end - beg));
          if (vb != null) {
            var attrs = new Dictionary<string, string>();
            for (int i = 0; i < vb.Length; i += 64) {
              var k = StdStr(vb, i, false);
              if (k != null && !attrs.ContainsKey(k)) attrs[k] = StdStr(vb, i + 32, true) ?? "";
            }
            result[id] = attrs;
          }
        }
      }
      node = Ptr(node + 0x10);
    }
    return result;
  }

  static bool IsNil(ulong node) { var b = Read(node + 0x19, 1); return b == null || b[0] != 0; }

  // ---------- containers ----------

  struct Item { public ulong Id; public string Name, Where; }

  // The inventory's id is always the player's id + 1. Its name at OBJ_NAME is often empty (since the 2026-10-30 patch it
  // isn't filled until later), so it is labelled by id rather than by name.
  static ulong inventoryId;

  static void Walk(ulong obj, string where, int depth, List<Item> items, HashSet<ulong> seen) {
    if (depth > 5 || !seen.Add(obj)) return;
    ulong cont = Ptr(obj + OBJ_CONTAINER);
    if (!IsA(cont, VT_SLOTTED) && !IsA(cont, VT_VOLUME)) return;
    ulong beg = Ptr(cont + CONT_BEGIN), end = Ptr(cont + CONT_END);
    if (end <= beg || (end - beg) % CONT_ENTRY != 0 || end - beg > CONT_ENTRY * 4096) return;
    var b = Read(beg, (int)(end - beg));
    if (b == null) return;
    for (int i = 0; i < b.Length; i += CONT_ENTRY) {
      ulong child = BitConverter.ToUInt64(b, i + ENTRY_OBJ);
      if (child == 0) continue;
      string name = ObjectName(child);
      ulong id = BitConverter.ToUInt64(b, i);
      if (depth == 0 && id == inventoryId) name = "Inventory";
      items.Add(new Item { Id = id, Name = name, Where = where });
      Walk(child, where.Length == 0 ? (name.Length > 0 ? name : "slot") : where + " > " + (name.Length > 0 ? name : "?"), depth + 1, items, seen);
    }
  }

  // ---------- value sheet (1 Million Credits+), copied from the SWGR Part Checker page ----------
  // ">" means at least, "<" means at most

  // The sheet in use: the built-in defaults below, or the user's edited copy from swgr-values.cfg next to the exe
  // (SWGR tools → Value sheet). Lines are "type<TAB>level<TAB>stat<TAB>value"; the comparison comes from OPS.
  static readonly string valuesFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swgr-values.cfg");

  // Comparison per type and stat: '>' at least, '<' at most (fixed, so the editor only takes numbers)
  internal static readonly Dictionary<string, Dictionary<string, char>> OPS = new Dictionary<string, Dictionary<string, char>> {
    { "reactor",   new Dictionary<string, char> { { "mass", '<' }, { "gen", '>' } } },
    { "engine",    new Dictionary<string, char> { { "red", '<' }, { "mass", '<' }, { "pitch", '>' }, { "yaw", '>' }, { "roll", '>' }, { "ts", '>' } } },
    { "shield",    new Dictionary<string, char> { { "red", '<' }, { "mass", '<' }, { "front", '>' }, { "back", '>' }, { "rr", '>' } } },
    { "armor",     new Dictionary<string, char> { { "armor", '>' }, { "mass", '<' } } },
    { "capacitor", new Dictionary<string, char> { { "red", '<' }, { "mass", '<' }, { "energy", '>' }, { "rr", '>' } } },
    { "booster",   new Dictionary<string, char> { { "red", '<' }, { "mass", '<' }, { "energy", '>' }, { "rr", '>' }, { "consumption", '<' }, { "accel", '>' }, { "speed", '>' } } },
    { "weapon",    new Dictionary<string, char> { { "red", '<' }, { "mass", '<' }, { "min", '>' }, { "max", '>' }, { "vss", '>' }, { "vsa", '>' }, { "eps", '<' }, { "rr", '<' } } },
  };

  // After OPS: static fields initialise in textual order, and LoadSheet needs OPS
  static Dictionary<string, Dictionary<int, Dictionary<string, Tuple<char, double>>>> SHEET = LoadSheet();

  internal static bool Customised { get { return System.IO.File.Exists(valuesFile); } }

  // Threshold for one cell, or null when that stat isn't required (or the level isn't on the sheet)
  internal static double? Threshold(string type, int level, string stat) {
    Dictionary<string, Tuple<char, double>> row;
    Tuple<char, double> t;
    return SHEET[type].TryGetValue(level, out row) && row.TryGetValue(stat, out t) ? t.Item2 : (double?)null;
  }

  // Set or clear one cell and save the whole sheet. A level with no thresholds left drops off the sheet.
  internal static void SetThreshold(string type, int level, string stat, double? value) {
    Dictionary<string, Tuple<char, double>> row;
    if (!SHEET[type].TryGetValue(level, out row)) SHEET[type][level] = row = new Dictionary<string, Tuple<char, double>>();
    if (value.HasValue) row[stat] = T(OPS[type][stat], value.Value); else row.Remove(stat);
    if (row.Count == 0) SHEET[type].Remove(level);
    var lines = new List<string> { "# SWGR tools value sheet (edited copy). Delete this file, or use Reset to defaults, to go back to the built-in sheet." };
    foreach (var t in SHEET) foreach (var l in t.Value) foreach (var s in l.Value)
      lines.Add(string.Join("\t", t.Key, l.Key.ToString(), s.Key, s.Value.Item2.ToString("R", CultureInfo.InvariantCulture)));
    try { System.IO.File.WriteAllLines(valuesFile, lines); } catch { }
  }

  internal static void ResetSheet() {
    try { System.IO.File.Delete(valuesFile); } catch { }
    SHEET = BuildSheet();
  }

  // The edited copy if there is a readable one, else the defaults
  static Dictionary<string, Dictionary<int, Dictionary<string, Tuple<char, double>>>> LoadSheet() {
    string file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swgr-values.cfg");
    if (!System.IO.File.Exists(file)) return BuildSheet();
    try {
      var s = OPS.Keys.ToDictionary(k => k, k => new Dictionary<int, Dictionary<string, Tuple<char, double>>>());
      foreach (var line in System.IO.File.ReadAllLines(file)) {
        var f = line.Split('\t');
        if (f.Length != 4 || line.StartsWith("#") || !OPS.ContainsKey(f[0]) || !OPS[f[0]].ContainsKey(f[2])) continue;
        int lvl = int.Parse(f[1]);
        if (!s[f[0]].ContainsKey(lvl)) s[f[0]][lvl] = new Dictionary<string, Tuple<char, double>>();
        s[f[0]][lvl][f[2]] = T(OPS[f[0]][f[2]], double.Parse(f[3], CultureInfo.InvariantCulture));
      }
      return s;
    } catch { return BuildSheet(); }
  }

  static Tuple<char, double> T(char op, double v) { return Tuple.Create(op, v); }

  static Dictionary<int, Dictionary<string, Tuple<char, double>>> Rows(params object[] rows) {
    var d = new Dictionary<int, Dictionary<string, Tuple<char, double>>>();
    foreach (object[] r in rows) {
      var m = new Dictionary<string, Tuple<char, double>>();
      for (int i = 1; i < r.Length; i += 3) m[(string)r[i]] = T((char)r[i + 1], Convert.ToDouble(r[i + 2]));
      d[(int)r[0]] = m;
    }
    return d;
  }

  static object[] R(params object[] a) { return a; }

  static Dictionary<string, Dictionary<int, Dictionary<string, Tuple<char, double>>>> BuildSheet() {
    var s = new Dictionary<string, Dictionary<int, Dictionary<string, Tuple<char, double>>>>();
    s["reactor"] = Rows(
      R(1, "mass", '<', 1000, "gen", '>', 11500), R(2, "mass", '<', 1200, "gen", '>', 13500),
      R(3, "mass", '<', 2000, "gen", '>', 14700), R(4, "mass", '<', 3000, "gen", '>', 17000),
      R(5, "mass", '<', 4300, "gen", '>', 20000), R(6, "mass", '<', 7500, "gen", '>', 26000),
      R(7, "mass", '<', 11000, "gen", '>', 31000), R(8, "mass", '<', 17000, "gen", '>', 38000));
    s["engine"] = Rows(
      R(4, "red", '<', 1200, "mass", '<', 3000, "pitch", '>', 67.3, "yaw", '>', 67.3, "roll", '>', 67.3, "ts", '>', 72.1),
      R(6, "red", '<', 1300, "mass", '<', 8500, "pitch", '>', 71, "yaw", '>', 71, "roll", '>', 71, "ts", '>', 95),
      R(8, "red", '<', 1350, "mass", '<', 22000, "pitch", '>', 73.6, "yaw", '>', 73.6, "roll", '>', 73.6, "ts", '>', 103.8),
      R(10, "red", '<', 1300, "mass", '<', 39500, "pitch", '>', 80.4, "yaw", '>', 80.4, "roll", '>', 80.4, "ts", '>', 106.5));
    s["shield"] = Rows(
      R(5, "red", '<', 1400, "mass", '<', 5300, "front", '>', 1300, "back", '>', 1300, "rr", '>', 9),
      R(6, "red", '<', 1500, "mass", '<', 9500, "front", '>', 1700, "back", '>', 1700, "rr", '>', 11.5),
      R(7, "red", '<', 1500, "mass", '<', 14000, "front", '>', 2200, "back", '>', 2200, "rr", '>', 16),
      R(8, "red", '<', 1600, "mass", '<', 25000, "front", '>', 2435, "back", '>', 2435, "rr", '>', 17),
      R(10, "red", '<', 1650, "mass", '<', 43000, "front", '>', 3100, "back", '>', 3100, "rr", '>', 19));
    s["armor"] = Rows(
      R(1, "armor", '>', 250, "mass", '<', 600), R(2, "armor", '>', 325, "mass", '<', 850),
      R(3, "armor", '>', 600, "mass", '<', 1000), R(4, "armor", '>', 750, "mass", '<', 1800),
      R(5, "armor", '>', 1100, "mass", '<', 2200), R(6, "armor", '>', 1400, "mass", '<', 3700),
      R(7, "armor", '>', 1600, "mass", '<', 4500), R(8, "armor", '>', 2000, "mass", '<', 6500),
      R(9, "armor", '>', 2400, "mass", '<', 7500), R(10, "armor", '>', 2800, "mass", '<', 10000));
    s["capacitor"] = Rows(
      R(2, "energy", '>', 1150, "rr", '>', 40.5),
      R(4, "red", '<', 575, "mass", '<', 2800, "energy", '>', 1250, "rr", '>', 45),
      R(6, "red", '<', 600, "mass", '<', 7000, "energy", '>', 1425, "rr", '>', 50),
      R(8, "red", '<', 700, "mass", '<', 23000, "energy", '>', 1450, "rr", '>', 55),
      R(10, "red", '<', 675, "mass", '<', 36000, "energy", '>', 1650, "rr", '>', 60));
    s["booster"] = Rows();
    s["weapon"] = Rows(
      R(5, "red", '<', 1500, "mass", '<', 4800, "min", '>', 1300, "max", '>', 1925, "vss", '>', .683, "vsa", '>', .683, "eps", '<', 18, "rr", '<', .31),
      R(6, "red", '<', 1550, "mass", '<', 8500, "min", '>', 1925, "max", '>', 2860, "vss", '>', .672, "vsa", '>', .672, "eps", '<', 22, "rr", '<', .38),
      R(7, "red", '<', 1650, "mass", '<', 12700, "min", '>', 2100, "max", '>', 3100, "vss", '>', .634, "vsa", '>', .634, "eps", '<', 20, "rr", '<', .31),
      R(8, "red", '<', 1700, "mass", '<', 23500, "min", '>', 2400, "max", '>', 3725, "vss", '>', .666, "vsa", '>', .666, "eps", '<', 14.1, "rr", '<', .388),
      R(10, "red", '<', 1700, "mass", '<', 40000, "min", '>', 3000, "max", '>', 5000, "vss", '>', .80, "vsa", '>', .80, "eps", '<', 27.5, "rr", '<', .38));
    return s;
  }

  internal static readonly Dictionary<string, string[][]> FIELDS = new Dictionary<string, string[][]> {
    { "reactor",   new[] { new[] { "mass", "Mass" }, new[] { "gen", "Generation" } } },
    { "engine",    new[] { new[] { "red", "Energy Drain" }, new[] { "mass", "Mass" }, new[] { "pitch", "Pitch" }, new[] { "yaw", "Yaw" }, new[] { "roll", "Roll" }, new[] { "ts", "Top Speed" } } },
    { "shield",    new[] { new[] { "red", "Energy Drain" }, new[] { "mass", "Mass" }, new[] { "front", "Front HP" }, new[] { "back", "Back HP" }, new[] { "rr", "Recharge Rate" } } },
    { "armor",     new[] { new[] { "armor", "Armor" }, new[] { "mass", "Mass" } } },
    { "capacitor", new[] { new[] { "red", "Energy Drain" }, new[] { "mass", "Mass" }, new[] { "energy", "Energy" }, new[] { "rr", "Recharge Rate" } } },
    { "booster",   new[] { new[] { "red", "Energy Drain" }, new[] { "mass", "Mass" }, new[] { "energy", "Energy" }, new[] { "rr", "Recharge Rate" }, new[] { "consumption", "Consumption" }, new[] { "accel", "Acceleration" }, new[] { "speed", "Speed" } } },
    { "weapon",    new[] { new[] { "red", "Energy Drain" }, new[] { "mass", "Mass" }, new[] { "min", "Min Damage" }, new[] { "max", "Max Damage" }, new[] { "vss", "Vs. Shields" }, new[] { "vsa", "Vs. Armor" }, new[] { "eps", "Energy/Shot" }, new[] { "rr", "Refire Rate" } } },
  };

  // ---------- attribute parsing ----------

  const string SC = "ship_component.ship_component_";

  static string TypeOf(Dictionary<string, string> a) {
    Func<string, bool> has = k => a.Keys.Any(x => x.StartsWith(SC + k));
    if (has("reactor_")) return "reactor";
    if (has("engine_")) return "engine";
    if (has("shield_")) return "shield";
    if (has("capacitor_")) return "capacitor";
    if (has("booster_")) return "booster";
    if (has("weapon_")) return "weapon";
    if (has("droidinterface") || has("cargo_hold")) return "other";
    if (a.ContainsKey(SC + "armor")) return "armor";
    return null;
  }

  // Strip colour codes like \#00ff00 and thousands separators
  static string Clean(string v) { return Regex.Replace(v ?? "", @"\\#[0-9a-fA-F]{6}", "").Replace(",", "").Trim(); }

  static double? Num(string v) {
    double d;
    return double.TryParse(Clean(v), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
  }

  // "cur/max" -> max
  static double? Max(string v) {
    if (v == null) return null;
    var c = Clean(v);
    int s = c.LastIndexOf('/');
    return Num(s >= 0 ? c.Substring(s + 1) : c);
  }

  static int? LevelOf(Dictionary<string, string> a) {
    string v;
    if (a.TryGetValue("reverseEngineeringLevel", out v) && Num(v).HasValue) return (int)Num(v).Value;
    foreach (var k in a.Keys) {
      var m = Regex.Match(k, @"cert_ordnance_level(\d+|ten)$");
      if (m.Success) return m.Groups[1].Value == "ten" ? 10 : int.Parse(m.Groups[1].Value);
    }
    return null;
  }

  static Dictionary<string, double> Stats(string type, Dictionary<string, string> a) {
    var s = new Dictionary<string, double>();
    Action<string, string, Func<string, double?>> put = (key, attr, f) => {
      string v;
      if (a.TryGetValue(SC + attr, out v)) { var d = f(v); if (d.HasValue) s[key] = d.Value; }
    };
    put("mass", "mass", Num);
    put("red", "energy_required", Num);
    switch (type) {
      case "reactor": put("gen", "reactor_generation_rate", Num); break;
      case "engine":
        put("pitch", "engine_pitch_rate_maximum", Num); put("yaw", "engine_yaw_rate_maximum", Num);
        put("roll", "engine_roll_rate_maximum", Num); put("ts", "engine_speed_maximum", Num); break;
      case "shield":
        put("front", "shield_hitpoints_front", Max); put("back", "shield_hitpoints_back", Max);
        put("rr", "shield_recharge_rate", Num); break;
      case "armor": put("armor", "armor", Max); break;
      case "capacitor":
        put("energy", "capacitor_energy", Max);
        put("rr", "capacitor_energy_recharge_rate", Num); put("rr", "capacitor_recharge_rate", Num); break;
      case "booster":
        put("energy", "booster_energy", Max); put("rr", "booster_energy_recharge_rate", Num);
        put("consumption", "booster_energy_consumption_rate", Num); put("accel", "booster_acceleration", Num);
        put("speed", "booster_speed_maximum", Num); break;
      case "weapon":
        string dmg;
        if (a.TryGetValue(SC + "weapon_damage", out dmg)) {
          var m = Regex.Match(Clean(dmg), @"^([\d.]+)\s*-\s*([\d.]+)$");
          if (m.Success) { s["min"] = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); s["max"] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
        }
        put("min", "weapon_damage_minimum", Num); put("max", "weapon_damage_maximum", Num);
        put("vss", "weapon_effectiveness_shields", Num); put("vsa", "weapon_effectiveness_armor", Num);
        put("eps", "weapon_energy_per_shot", Num); put("rr", "weapon_refire_rate", Num); break;
    }
    return s;
  }

  // ---------- evaluation (same rules as the Part Checker page) ----------

  static string Fmt(double v) { return v < 10 ? v.ToString("0.###", CultureInfo.InvariantCulture) : v.ToString("#,0.#", CultureInfo.InvariantCulture); }

  internal sealed class Row { public string Label, Value, Need, Result; }
  internal sealed class Rating { public string Word, Sub, TypeName; public int? Level; public List<Row> Rows = new List<Row>(); }

  internal static Rating Rate(string type, int? lvl, Dictionary<string, double> stats) {
    var r = new Rating { TypeName = char.ToUpper(type[0]) + type.Substring(1), Level = lvl };
    Dictionary<string, Tuple<char, double>> need = null;
    if (lvl.HasValue) SHEET[type].TryGetValue(lvl.Value, out need);
    var fails = new List<Tuple<string, double>>();
    int missing = 0, ok = 0;
    foreach (var f in FIELDS[type]) {
      double v; bool have = stats.TryGetValue(f[0], out v);
      Tuple<char, double> req = null;
      if (need != null) need.TryGetValue(f[0], out req);
      string res = "";
      if (req != null) {
        if (!have) { res = "?"; missing++; }
        else if (req.Item1 == '>' ? v >= req.Item2 : v <= req.Item2) { res = "PASS"; ok++; }
        else { res = "FAIL"; fails.Add(Tuple.Create(f[1], Math.Abs(v - req.Item2) / req.Item2)); }
      }
      r.Rows.Add(new Row { Label = f[1], Value = have ? Fmt(v) : "-", Need = req == null ? "" : (req.Item1 == '>' ? ">= " : "<= ") + Fmt(req.Item2), Result = res });
    }
    if (need == null) {
      r.Word = "NOT VALUABLE";
      r.Sub = type == "booster" ? "boosters aren't on the value sheet (crafted ones beat loot)"
          : lvl.HasValue ? string.Format("no level {0} {1} on the 1M+ sheet", lvl, type) : "level unknown";
    } else if (ok > 0) {
      // One threshold cleared is enough: buyers want parts that excel at a single stat
      r.Word = "VALUABLE";
      r.Sub = fails.Count == 0 && missing == 0 ? "clears every 1M+ threshold"
          : string.Format("clears {0} of {1}: {2}", ok, ok + fails.Count + missing, string.Join(", ", r.Rows.Where(x => x.Result == "PASS").Select(x => x.Label)));
    }
    else if (missing > 0) { r.Word = "NEED MORE"; r.Sub = missing + " stat(s) missing, none of the rest clear the sheet"; }
    else {
      var best = fails.OrderBy(x => x.Item2).First();
      if (best.Item2 <= 0.10) { r.Word = "CLOSE"; r.Sub = string.Format("nothing clears; {0} misses by only {1:0.0}% - check the Bazaar", best.Item1, best.Item2 * 100); }
      else { r.Word = "NOT VALUABLE"; r.Sub = string.Format("fails all {0}: {1}", fails.Count, string.Join(", ", fails.Select(x => x.Item1))); }
    }
    return r;
  }

  // Rating for one cached attribute list, or null when it isn't a rateable ship component
  internal static Rating RateAttrs(Dictionary<string, string> a) {
    var type = TypeOf(a);
    if (type == null || type == "other") return null;
    return Rate(type, LevelOf(a), Stats(type, a));
  }

  // Report output: (text, verdict). A non-null verdict means the line starts with that word, padded to 15 columns
  internal delegate void LineWriter(string text, string verdict);

  static void Evaluate(LineWriter line, string name, string where, string type, int? lvl, Dictionary<string, double> stats, bool verbose) {
    var r = Rate(type, lvl, stats);
    line(string.Format("  {0,-13}{1}  ({2} L{3}{4})", r.Word, name, r.TypeName, lvl.HasValue ? lvl.ToString() : "?", where.Length > 0 ? ", " + where : ""), r.Word);
    line("               " + r.Sub, null);
    if (verbose || r.Word != "NOT VALUABLE")
      foreach (var row in r.Rows) line(string.Format("      {0,-14} {1,10}   {2,-10} {3}", row.Label, row.Value, row.Need, row.Result), null);
  }

  static void ConsoleLine(string text, string verdict) {
    if (verdict == null) { Console.WriteLine(text); return; }
    Console.ForegroundColor = verdict == "VALUABLE" ? ConsoleColor.Green : verdict == "NOT VALUABLE" ? ConsoleColor.Red : ConsoleColor.Yellow;
    Console.Write(text.Substring(0, 15));
    Console.ResetColor();
    Console.WriteLine(text.Substring(15));
  }

  // ---------- shared with the overlay ----------

  const ulong RVA_MEDIATOR_FACTORY = 0x2243c30; // CuiMediatorFactory: std::map<std::string, ConstructorBase*>* (head, size)
  const ulong VT_CTOR_HUD_GROUND = 0x1d29548, VT_HUD_GROUND = 0x1d64438, VT_HUD_WINDOWS = 0x1d9bee8;
  const ulong VT_CTOR_HUD_SPACE = 0x1d295e8, VT_HUD_SPACE = 0x1d64658, VT_HUD_WINDOWS_SPACE = 0x1d9c688; // the HUD while in a ship
  const ulong VT_INVENTORY = 0x1d649c8, VT_INVENTORY_INFO = 0x1d9b3f8, VT_WATCHER = 0x1cee9e8, VT_TANGIBLE = 0x1cf68c8;
  static ulong hudCtor, hudCtorSpace;

  internal static bool Attach() {
    Detach();
    var procs = Process.GetProcessesByName("SwgClient_r");
    if (procs.Length == 0) return false;
    h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, procs[0].Id);
    if (h == IntPtr.Zero) return false;
    var mods = new IntPtr[1];
    int needed;
    EnumProcessModulesEx(h, mods, IntPtr.Size, out needed, 0x03);
    baseAddr = (ulong)mods[0].ToInt64();
    hudCtor = hudCtorSpace = 0;
    return baseAddr != 0;
  }

  internal static void Detach() { if (h != IntPtr.Zero) { CloseHandle(h); h = IntPtr.Zero; } }

  internal static bool Alive() { return h != IntPtr.Zero && Read(baseAddr, 2) != null; }

  // Constructor<SwgCuiHudGround> / <SwgCuiHudSpace> from the mediator factory map, found once by vtable and re-checked on every call
  static ulong HudCtor() { return IsA(hudCtor, VT_CTOR_HUD_GROUND) ? hudCtor : (hudCtor = FindCtor(VT_CTOR_HUD_GROUND)); }
  static ulong HudCtorSpace() { return IsA(hudCtorSpace, VT_CTOR_HUD_SPACE) ? hudCtorSpace : (hudCtorSpace = FindCtor(VT_CTOR_HUD_SPACE)); }

  static ulong FindCtor(ulong vtable) {
    ulong map = Ptr(baseAddr + RVA_MEDIATOR_FACTORY);
    ulong head = Ptr(map);
    long size = (long)Ptr(map + 8);
    if (head == 0 || size <= 0 || size > 10000) return 0;
    var stack = new Stack<ulong>();
    ulong node = Ptr(head + 8);
    int guard = 0;
    while ((stack.Count > 0 || !IsNil(node)) && guard++ < 4 * size + 16) {
      if (!IsNil(node)) { stack.Push(node); node = Ptr(node); continue; }
      node = stack.Pop();
      ulong ctor = Ptr(node + 0x40);
      if (IsA(ctor, vtable)) return ctor;
      node = Ptr(node + 0x10);
    }
    return 0;
  }

  // The item shown in the inventory window's info panel (the last one clicked), or 0. Space has its own HUD and
  // inventory window, so the space chain is tried first (the ground HUD lives on, with a stale selection).
  // HUD ctor +0x30 -> SwgCuiHudGround/Space +WM_OFF -> SwgCuiHudWindowManagerGround/Space +0x58 -> SwgCuiInventory
  //   +0x158 -> SwgCuiInventoryInfo +0x188 -> Watcher<Object> +8 -> TangibleObject
  internal static ulong SelectedItem() {
    ulong obj = SelectedItem(HudCtorSpace(), VT_HUD_SPACE, VT_HUD_WINDOWS_SPACE);
    return obj != 0 ? obj : SelectedItem(HudCtor(), VT_HUD_GROUND, VT_HUD_WINDOWS);
  }

  static ulong SelectedItem(ulong ctor, ulong vtHud, ulong vtWindows) {
    if (ctor == 0) return 0;
    ulong hud = Ptr(ctor + 0x30);
    if (!IsA(hud, vtHud)) return 0;
    ulong wm = Ptr(hud + 0x348);
    if (!IsA(wm, vtWindows)) return 0;
    ulong inv = Ptr(wm + 0x58);
    if (!IsA(inv, VT_INVENTORY)) return 0;
    ulong info = Ptr(inv + 0x158);
    if (!IsA(info, VT_INVENTORY_INFO)) return 0;
    ulong w = Ptr(info + 0x188);
    if (!IsA(w, VT_WATCHER)) return 0;
    ulong obj = Ptr(w + 8);
    return IsA(obj, VT_TANGIBLE) ? obj : 0;
  }

  internal static ulong ObjectId(ulong obj) { return Ptr(obj + OBJ_ID); }

  // The display name: an MSVC std::wstring at OBJ_NAME (names under 8 chars sit inline, longer ones behind a pointer),
  // without colour codes
  internal static string ObjectName(ulong obj) {
    var b = Read(obj + OBJ_NAME, 0x20);
    return Regex.Replace((b == null ? null : StdStr(b, 0, true)) ?? "", @"\\#([0-9a-fA-F]{6}|\.)", "").Trim();
  }

  // Your character's name (the DPS meter uses it to tell you apart from your group), or null when not in game
  internal static string PlayerName() {
    if (!Alive()) return null;
    ulong p = Ptr(baseAddr + RVA_PLAYER);
    var n = p == 0 ? "" : ObjectName(p);
    return n.Length > 0 ? n : null;
  }

  // One object's cached examine attributes: binary search of the ObjectAttributeManager map (NetworkId compares as int64)
  internal static Dictionary<string, string> ReadAttrs(ulong id) {
    ulong head = Ptr(baseAddr + RVA_ATTR_MAP);
    ulong node = Ptr(head + 8);
    for (int depth = 0; depth < 64 && !IsNil(node); depth++) {
      long key = (long)Ptr(node + 0x20);
      if ((long)id < key) node = Ptr(node);
      else if ((long)id > key) node = Ptr(node + 0x10);
      else {
        return ReadPairs(node + 0x28);
      }
    }
    return null;
  }

  // A std::vector<std::pair<std::string, std::wstring>> (64-byte pairs) at addr, as a dictionary
  static Dictionary<string, string> ReadPairs(ulong addr) {
    var nb = Read(addr, 0x10);
    if (nb == null) return null;
    ulong beg = BitConverter.ToUInt64(nb, 0), end = BitConverter.ToUInt64(nb, 8);
    if (end <= beg || (end - beg) % 64 != 0 || end - beg > 64 * 512) return null;
    var vb = Read(beg, (int)(end - beg));
    if (vb == null) return null;
    var attrs = new Dictionary<string, string>();
    for (int i = 0; i < vb.Length; i += 64) {
      var k = StdStr(vb, i, false);
      if (k != null && !attrs.ContainsKey(k)) attrs[k] = StdStr(vb, i + 32, true) ?? "";
    }
    return attrs;
  }

  // ---------- vendor / bazaar item details ----------

  internal sealed class VendorItem { public ulong Id; public string Template; public Dictionary<string, string> Attrs; }

  // Every entry of the auction details map, filled when you open "Item Details" on a vendor or bazaar item.
  // Node: key NetworkId +0x20, attribute vector +0x50, object template std::string +0x68
  internal static List<VendorItem> VendorDetails() {
    var list = new List<VendorItem>();
    ulong head = Ptr(baseAddr + RVA_VENDOR_DETAILS);
    long size = (long)Ptr(baseAddr + RVA_VENDOR_DETAILS + 8);
    if (head == 0 || size <= 0 || size > 1000) return list;
    var stack = new Stack<ulong>();
    ulong node = Ptr(head + 8);
    int guard = 0;
    while ((stack.Count > 0 || !IsNil(node)) && guard++ < 4 * size + 16) {
      if (!IsNil(node)) { stack.Push(node); node = Ptr(node); continue; }
      node = stack.Pop();
      var tb = Read(node + 0x68, 0x20);
      list.Add(new VendorItem { Id = Ptr(node + 0x20), Template = tb == null ? null : StdStr(tb, 0, false), Attrs = ReadPairs(node + 0x50) });
      node = Ptr(node + 0x10);
    }
    return list;
  }

  // The vendor item to show: the entry that appeared in the details map most recently (still present)
  static readonly Dictionary<ulong, long> vendorSeen = new Dictionary<ulong, long>();
  static long vendorTick;
  internal static VendorItem CurrentVendorItem() {
    var list = VendorDetails();
    vendorTick++;
    VendorItem best = null;
    long bestAt = -1;
    foreach (var v in list) {
      long at;
      if (!vendorSeen.TryGetValue(v.Id, out at)) vendorSeen[v.Id] = at = vendorTick;
      if (at > bestAt) { bestAt = at; best = v; }
    }
    if (vendorSeen.Count > 4 * list.Count + 64) {
      var live = new HashSet<ulong>(list.Select(v => v.Id));
      foreach (var k in vendorSeen.Keys.Where(k => !live.Contains(k)).ToList()) vendorSeen.Remove(k);
    }
    return best;
  }

  // Template facts from bin/data/ship-parts.tsv (or bin/ship-parts.tsv) (tools/ship-part-table.js): the certification level and display name.
  // Vendor details carry neither: the game adds "Certified Level N" itself from the template's certificationsRequired.
  static Dictionary<string, Tuple<int?, string>> templateInfo;
  static Tuple<int?, string> TemplateInfo(string template) {
    if (templateInfo == null) {
      templateInfo = new Dictionary<string, Tuple<int?, string>>();
      try {
        var f = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "ship-parts.tsv");
        if (!System.IO.File.Exists(f)) f = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ship-parts.tsv");
        foreach (var l in System.IO.File.ReadAllLines(f)) {
          var c = l.Split('\t');
          if (c.Length < 3 || l.StartsWith("#")) continue;
          int lv;
          templateInfo[c[0]] = Tuple.Create(int.TryParse(c[1], out lv) ? lv : (int?)null, c[2]);
        }
      } catch { }
    }
    Tuple<int?, string> t;
    return template != null && templateInfo.TryGetValue(template, out t) ? t : null;
  }

  // Display name: the string table name from the table, else the file name tidied up
  // ("shared_eng_koensayr_tuned_small_ionjet.iff" -> "Koensayr Tuned Small Ionjet")
  internal static string TemplateName(string template) {
    if (string.IsNullOrEmpty(template)) return "Vendor item";
    var t = TemplateInfo(template);
    if (t != null && t.Item2.Length > 0) return t.Item2;
    var stem = System.IO.Path.GetFileNameWithoutExtension(template);
    if (stem.StartsWith("shared_")) stem = stem.Substring(7);
    var words = Regex.Replace(stem, @"^(eng|rct|shd|arm|cap|bst|wpn)_", "").Split('_');
    return string.Join(" ", words.Where(w => w.Length > 0).Select(w => char.ToUpper(w[0]) + w.Substring(1)));
  }

  // Rate a vendor item: its attributes, with the level taken from the template when the attributes have none
  internal static Rating RateVendor(VendorItem v) {
    if (v.Attrs == null) return null;
    var a = v.Attrs;
    var t = TemplateInfo(v.Template);
    if (LevelOf(a) == null && t != null && t.Item1.HasValue) {
      a = new Dictionary<string, string>(a);
      a["reverseEngineeringLevel"] = t.Item1.Value.ToString();
    }
    return RateAttrs(a);
  }

  // ---------- main ----------

  internal static int Main(string[] args) {
    bool all = args.Contains("--all"), verbose = args.Contains("-v");
    if (!Attach()) { Console.Error.WriteLine("SwgClient_r is not running (or OpenProcess failed: " + Marshal.GetLastWin32Error() + ")"); return 1; }
    try {
      if (args.Contains("--chain")) {
        // Each link of the selected-item chain with its vtable check (for re-finding offsets after a patch)
        ulong ctor = HudCtor(), hud = Ptr(ctor + 0x30), wm = Ptr(hud + 0x348), inv = Ptr(wm + 0x58), info = Ptr(inv + 0x158), w = Ptr(info + 0x188), obj = Ptr(w + 8);
        Console.WriteLine("ctor 0x{0:x}\nhud 0x{1:x} {2}\nwindows 0x{3:x} {4}\ninventory 0x{5:x} {6}\ninfo 0x{7:x} {8}\nwatcher 0x{9:x} {10}\nobject 0x{11:x} {12}",
          ctor, hud, IsA(hud, VT_HUD_GROUND), wm, IsA(wm, VT_HUD_WINDOWS), inv, IsA(inv, VT_INVENTORY), info, IsA(info, VT_INVENTORY_INFO), w, IsA(w, VT_WATCHER), obj, IsA(obj, VT_TANGIBLE));
        ulong sctor = HudCtorSpace(), shud = Ptr(sctor + 0x30);
        Console.WriteLine("space ctor 0x{0:x}\nspace hud 0x{1:x} {2}", sctor, shud, IsA(shud, VT_HUD_SPACE));
        var hb = IsA(shud, VT_HUD_SPACE) ? Read(shud, 0x800) : null;
        if (hb != null) for (int o = 0; o < hb.Length; o += 8)
          if (IsA(BitConverter.ToUInt64(hb, o), VT_HUD_WINDOWS_SPACE)) Console.WriteLine("  space window manager at +0x{0:x}", o);
        ulong swm = Ptr(shud + 0x348), sinv = Ptr(swm + 0x58), sinfo = Ptr(sinv + 0x158), sw = Ptr(sinfo + 0x188);
        Console.WriteLine("space inventory 0x{0:x} {1}\nspace info 0x{2:x} {3}\nspace watcher 0x{4:x} {5}", sinv, IsA(sinv, VT_INVENTORY), sinfo, IsA(sinfo, VT_INVENTORY_INFO), sw, IsA(sw, VT_WATCHER));
        var wb = IsA(swm, VT_HUD_WINDOWS_SPACE) ? Read(swm, 0x400) : null;
        if (wb != null) for (int o = 0; o < wb.Length; o += 8)
          if (IsA(BitConverter.ToUInt64(wb, o), VT_INVENTORY)) Console.WriteLine("  space inventory at +0x{0:x}", o);
        Console.WriteLine("selected 0x{0:x}", SelectedItem());
        return 0;
      }
      if (args.Contains("--vendor-watch")) {
        // Every change of the details map (ids, attribute counts, chosen entry), polled every 50 ms for N seconds
        int secs = 180, wi = Array.IndexOf(args, "--vendor-watch");
        if (wi + 1 < args.Length) int.TryParse(args[wi + 1], out secs);
        string prev = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < secs) {
          var vs = VendorDetails();
          var cur = CurrentVendorItem();
          var s = string.Format("size {0}: {1} -> current {2}", (long)Ptr(baseAddr + RVA_VENDOR_DETAILS + 8),
            string.Join(", ", vs.Select(v => string.Format("{0:x} ({1} attrs, {2})", v.Id, v.Attrs == null ? 0 : v.Attrs.Count, TemplateName(v.Template)))),
            cur == null ? "none" : cur.Id.ToString("x"));
          if (s != prev) { Console.WriteLine("{0,8:0.00}s  {1}", sw.Elapsed.TotalSeconds, s); prev = s; }
          System.Threading.Thread.Sleep(50);
        }
        return 0;
      }
      if (args.Contains("--vendor")) {
        var vs = VendorDetails();
        Console.WriteLine("{0} vendor item detail(s) cached (map size {1})", vs.Count, (long)Ptr(baseAddr + RVA_VENDOR_DETAILS + 8));
        foreach (var v in vs) {
          var r = RateVendor(v);
          Console.WriteLine("  {0}  (id {1:x}, {2}): {3}", TemplateName(v.Template), v.Id, v.Template, v.Attrs == null ? "no stats" : r == null ? "not a rateable ship component" : r.Word + " - " + r.Sub);
          if (verbose && r != null) foreach (var row in r.Rows) Console.WriteLine("      {0,-16}{1,10}   {2,-10}{3}", row.Label, row.Value, row.Need, row.Result);
        }
        return 0;
      }
      if (args.Contains("--selected")) {
        ulong sel = SelectedItem();
        if (sel == 0) { Console.WriteLine("nothing selected in the inventory window"); return 0; }
        var a = ReadAttrs(ObjectId(sel));
        var r = a == null ? null : RateAttrs(a);
        Console.WriteLine("{0}  (id {1:x}): {2}", ObjectName(sel), ObjectId(sel), a == null ? "no stats cached" : r == null ? "not a rateable ship component" : r.Word + " - " + r.Sub);
        return 0;
      }

      var err = Report(all, verbose, ConsoleLine);
      if (err != null) { Console.Error.WriteLine(err); return 1; }
    } finally { Detach(); }
    return 0;
  }

  // The inventory report; needs Attach() first. Returns an error message, or null
  internal static string Report(bool all, bool verbose, LineWriter line) {
    ulong player = Ptr(baseAddr + RVA_PLAYER);
    if (!IsA(player, VT_CREATURE)) return "player not found (not logged in, or the client was patched: re-check notes/memory.md)";

    var items = new List<Item>();
    inventoryId = Ptr(player + OBJ_ID) + 1;
    Walk(player, "", 0, items, new HashSet<ulong>());
    var cache = ReadAttrCache();
    var mine = new HashSet<ulong>(items.Select(i => i.Id));

    var found = new List<Tuple<Item, Dictionary<string, string>>>();
    foreach (var it in items) {
      Dictionary<string, string> a;
      if (cache.TryGetValue(it.Id, out a) && TypeOf(a) != null) found.Add(Tuple.Create(it, a));
    }
    if (all)
      foreach (var kv in cache)
        if (!mine.Contains(kv.Key) && TypeOf(kv.Value) != null)
          found.Add(Tuple.Create(new Item { Id = kv.Key, Name = "id " + kv.Key.ToString("x"), Where = "not in your containers" }, kv.Value));

    // Unnamed top-level slots are the datapad, equipment and the like; only report named containers (inventory, backpacks, bank)
    var bags = items.Where(i => i.Where.Length > 0 && !i.Where.StartsWith("slot")).ToList();
    line(string.Format("{0} items in your inventory and bags, {1} ship component(s) with cached stats", bags.Count, found.Count), null);
    foreach (var g in bags.Where(i => !cache.ContainsKey(i.Id)).GroupBy(i => i.Where))
      line(string.Format("  {0} item(s) in {1} have no stats cached yet: open it (or hover them) and rerun", g.Count(), g.Key), null);
    line("", null);
    foreach (var f in found.OrderBy(f => f.Item1.Where).ThenBy(f => f.Item1.Name)) {
      var type = TypeOf(f.Item2);
      if (type == "other") { line(string.Format("  {0,-13}{1}  (droid interface / cargo hold, not on the sheet)", "-", f.Item1.Name), null); continue; }
      Evaluate(line, f.Item1.Name, f.Item1.Where, type, LevelOf(f.Item2), Stats(type, f.Item2), verbose);
    }
    return null;
  }
}
