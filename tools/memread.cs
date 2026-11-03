// Read-only memory inspector for the running SWG client.
// The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only, so it cannot write,
// inject, suspend or debug. This file must never call WriteProcessMemory, VirtualProtectEx,
// CreateRemoteThread, DebugActiveProcess or open the process with any other access right.
//
// build: csc /platform:x64 /optimize /out:memread.exe memread.cs
// usage: memread info
//        memread objs <vtableRVA>                        list objects whose first qword is base+vtableRVA
//        memread objfind <vtableRVA> <size> <int>        search each object's first <size> bytes for an int32
//        memread objints <vtableRVA> <offset> <count>    print <count> int32s at <offset> in every object
//        memread dump <hexAddr> <len>                    hex dump with int32 view
//        memread findint <int>                           every int32 match in private read/write memory (first 200)
//        memread findstr <text> [ascii|utf16]            every occurrence of text in private read/write memory (first 200)
//        memread attrpairs <name>                        std::pair<std::string, std::wstring> entries whose key is <name>
//        memread attrvec <pairAddr>                      every pair in the array around one attrpairs hit
//        memread path <fromAddr> <to1,to2,...> [depth] [span]  breadth-first pointer paths from one address to targets
//        memread strs <addr> [depth] [span] [filter]   ASCII/UTF-16 strings within <depth> pointer hops of an address
//        memread modmaps <fromRVA> <toRVA> [minSize]    std::maps held by module globals (directly or via one pointer), with size and first key
//        memread ticks <fromRVA> <toRVA> <lo> <hi>     module uint32/float/double values in [lo,hi] that grow by about 1 per second
//        memread range <fromRVA> <toRVA> <lo> <hi>     module uint32/int64 values in [lo,hi]
//        memread buffwatch <buff.tsv> [minutes]          watch the player's buffs; when one is applied, end - duration = game time now, then search for the clock
//        memread hclock <gameLo> <gameHi>                heap uint32s that tick with real time inside [gameLo,gameHi], or hold unix-now minus a value in it
//        memread strmap <mapObjAddr> [filter]           std::map<std::string, T*> entries: key, value pointer and its vtable RVA
//        memread objlists <vtableRVA>                    arrays (8-byte strides) in the heap holding 8+ pointers to objects with this vtable
//        memread vecobjs <RVA>                           a module-global std::vector<Object*>: each entry's vtable RVA, template key and distance
//        memread ship                                    space: the player's container chain, positions of player / own ship / target, target shield floats
//        memread aggro [vecRVA]                               every CreatureObject: distance, HP, and what its +0xd18 target points at (you / other / none)
//        memread buffs [offset]                          the player's buffs (crc, end, duration) and the time left under a clock offset
//        memread buffscan <buff.tsv>                     std::maps in the player's and target's CreatureObject whose keys are buff-name CRCs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;

static class MemRead {
  const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;
  const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
  const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80; // RW, WC, XRW, XWC

  [StructLayout(LayoutKind.Sequential)]
  struct MBI { public ulong BaseAddress, AllocationBase; public uint AllocationProtect, pad1; public ulong RegionSize; public uint State, Protect, Type, pad2; }

  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte[] buf, IntPtr size, out IntPtr read);
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, ulong addr, out MBI mbi, IntPtr len);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int needed, uint filter);
  [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameExW(IntPtr h, IntPtr mod, StringBuilder name, int size);

  static IntPtr h;
  static ulong baseAddr;

  static int Main(string[] a) {
    if (a.Length == 0) { Console.Error.WriteLine("see header for usage"); return 1; }
    var procs = Process.GetProcessesByName("SwgClient_r");
    if (procs.Length == 0) { Console.Error.WriteLine("SwgClient_r is not running"); return 1; }
    var p = procs[0];
    h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, p.Id);
    if (h == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess failed: " + Marshal.GetLastWin32Error()); return 1; }
    try {
      var mods = new IntPtr[1];
      int needed;
      EnumProcessModulesEx(h, mods, IntPtr.Size, out needed, 0x03);
      baseAddr = (ulong)mods[0].ToInt64();
      switch (a[0]) {
        case "info": {
          var sb = new StringBuilder(1024);
          GetModuleFileNameExW(h, mods[0], sb, sb.Capacity);
          Console.WriteLine("pid {0}  base 0x{1:x}  {2}", p.Id, baseAddr, sb);
          if (procs.Length > 1) Console.WriteLine("note: {0} SwgClient_r processes, using the first", procs.Length);
          break;
        }
        case "objs": foreach (var o in Objects(Hex(a[1]))) Console.WriteLine("0x{0:x}", o); break;
        case "objfind": ObjFind(Hex(a[1]), Num(a[2]), Num(a[3])); break;
        case "objints": ObjInts(Hex(a[1]), Num(a[2]), Num(a[3])); break;
        case "dump": Dump(Hex(a[1]), Num(a[2])); break;
        case "findint": FindInt(Num(a[1])); break;
        case "findstr": FindStr(a[1], a.Length > 2 ? a[2] : "ascii"); break;
        case "attrpairs": AttrPairs(a[1]); break;
        case "attrvec": AttrVec(Hex(a[1])); break;
        case "strs": Strs(Hex(a[1]), a.Length > 2 ? Num(a[2]) : 2, a.Length > 3 ? Num(a[3]) : 0x400, a.Length > 4 ? a[4] : null); break;
        case "path": PathTo(Hex(a[1]), a[2].Split(','), a.Length > 3 ? Num(a[3]) : 3, a.Length > 4 ? Num(a[4]) : 0x400); break;
        case "vecs": Vecs(Hex(a[1]), Num(a[2])); break;
        case "deepfind": DeepFind(Hex(a[1]), Num(a[2]), Num(a[3])); break;
        case "ptrints": PtrInts(Hex(a[1]), a[2].Split(','), Num(a[3])); break;
        case "deepstr": DeepStr(Hex(a[1]), Num(a[2]), a[3]); break;
        case "creatures": Creatures(Hex(a[1])); break;
        case "buffscan": BuffScan(a[1]); break;
        case "objlists": ObjLists(Hex(a[1])); break;
        case "vecobjs": VecObjs(Hex(a[1])); break;
        case "ship": Ship(); break;
        case "dumpchain": DumpChain(a[1]); break;
        case "moneysnap": MoneySnap(a[1]); break;
        case "base": Console.WriteLine("0x{0:x}", baseAddr); break;
        case "moneydiff": MoneyDiff(a[1], a[2]); break;
        case "aggro": Aggro(a.Length > 1 ? Hex(a[1]) : 0); break;
        case "buffs": BuffDump(a.Length > 1 ? long.Parse(a[1]) : 0); break;
        case "buffwatch": BuffWatch(a[1], a.Length > 2 ? Num(a[2]) : 10); break;
        case "hclock": HeapClock(long.Parse(a[1]), long.Parse(a[2])); break;
        case "strmap": StrMap(Hex(a[1]), a.Length > 2 ? a[2] : null); break;
        case "range": Range(Hex(a[1]), Hex(a[2]), long.Parse(a[3]), long.Parse(a[4])); break;
        case "ticks": Ticks(Hex(a[1]), Hex(a[2]), double.Parse(a[3]), double.Parse(a[4])); break;
        case "modmaps": ModMaps(Hex(a[1]), Hex(a[2]), a.Length > 3 ? Num(a[3]) : 1); break;
        case "findq": FindQ(a[1].Split(',')); break;
        case "q": foreach (var s in a[1].Split(',')) Console.WriteLine("0x{0:x}: {1:x}", Hex(s), Ptr(Hex(s))); break;
        case "findmod": FindMod(Hex(a[1])); break;
        default: Console.Error.WriteLine("unknown command"); return 1;
      }
    } finally { CloseHandle(h); }
    return 0;
  }

  static int Num(string s) { return s.StartsWith("0x") ? (int)Hex(s) : int.Parse(s); }

  static ulong Hex(string s) { return ulong.Parse(s.StartsWith("0x") ? s.Substring(2) : s, NumberStyles.HexNumber); }

  static byte[] Read(ulong addr, int len) {
    var b = new byte[len];
    IntPtr n;
    return ReadProcessMemory(h, addr, b, (IntPtr)len, out n) && (int)n == len ? b : null;
  }

  // Committed, private, writable regions: where the heap (and so live game objects) lives
  static IEnumerable<KeyValuePair<ulong, ulong>> HeapRegions() {
    ulong addr = 0;
    MBI m;
    while (VirtualQueryEx(h, addr, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) != IntPtr.Zero) {
      if (m.State == MEM_COMMIT && m.Type == MEM_PRIVATE && (m.Protect & WRITABLE) != 0 && (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
        yield return new KeyValuePair<ulong, ulong>(m.BaseAddress, m.RegionSize);
      ulong next = m.BaseAddress + m.RegionSize;
      if (next <= addr) break;
      addr = next;
    }
  }

  static IEnumerable<ulong> Objects(ulong vtRva) {
    ulong vt = baseAddr + vtRva;
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = 0; i + 8 <= len; i += 8)
          if (BitConverter.ToUInt64(b, i) == vt) yield return r.Key + off + (ulong)i;
      }
    }
  }

  static void ObjFind(ulong vtRva, int size, int value) {
    int count = 0;
    var hits = new Dictionary<int, int>();
    foreach (var o in Objects(vtRva)) {
      count++;
      var b = Read(o, size);
      if (b == null) continue;
      for (int i = 0; i + 4 <= size; i += 4)
        if (BitConverter.ToInt32(b, i) == value) {
          Console.WriteLine("obj 0x{0:x} +0x{1:x}", o, i);
          int c; hits.TryGetValue(i, out c); hits[i] = c + 1;
        }
    }
    Console.WriteLine("{0} objects scanned", count);
    foreach (var kv in hits) Console.WriteLine("offset +0x{0:x}: {1} hit(s)", kv.Key, kv.Value);
  }

  static void ObjInts(ulong vtRva, int offset, int n) {
    foreach (var o in Objects(vtRva)) {
      var b = Read(o + (ulong)offset, 4 * n);
      if (b == null) continue;
      var sb = new StringBuilder();
      for (int i = 0; i < n; i++) sb.Append(BitConverter.ToInt32(b, 4 * i)).Append(' ');
      Console.WriteLine("0x{0:x}: {1}", o, sb);
    }
  }

  // For every object: follow the pointer at each given offset and print <n> int32s there
  static void PtrInts(ulong vtRva, string[] offsets, int n) {
    foreach (var o in Objects(vtRva)) {
      var sb = new StringBuilder();
      foreach (var s in offsets) {
        var pb = Read(o + (ulong)Num(s), 8);
        var v = pb == null ? null : Read(BitConverter.ToUInt64(pb, 0), 4 * n);
        sb.Append(" | ");
        if (v == null) { sb.Append("-"); continue; }
        for (int i = 0; i < n; i++) sb.Append(BitConverter.ToInt32(v, 4 * i)).Append(' ');
      }
      Console.WriteLine("0x{0:x}{1}", o, sb);
    }
  }

  // CreatureObject offsets found so far (DX12 client): network id +0x30, name string ptr +0x280,
  // current HAM int[] ptr +0xa00, max HAM int[] ptr +0xa70 (health = [0], action = [2], mind = [4])
  static ulong Ptr(ulong addr) { var b = Read(addr, 8); return b == null ? 0 : BitConverter.ToUInt64(b, 0); }

  static string WStr(ulong addr) {
    var b = addr == 0 ? null : Read(addr, 128);
    if (b == null) return "?";
    var s = Encoding.Unicode.GetString(b);
    int z = s.IndexOf('\0');
    return z >= 0 ? s.Substring(0, z) : s;
  }

  static void Creatures(ulong vtRva) {
    foreach (var o in Objects(vtRva)) {
      var cur = Read(Ptr(o + 0xa00), 20);
      var max = Read(Ptr(o + 0xa70), 20);
      Console.WriteLine("0x{0:x}  id {1:x}  hp {2}/{3}  act {4}/{5}  mind {6}/{7}  {8}", o, Ptr(o + 0x30),
        cur == null ? -1 : BitConverter.ToInt32(cur, 0), max == null ? -1 : BitConverter.ToInt32(max, 0),
        cur == null ? -1 : BitConverter.ToInt32(cur, 8), max == null ? -1 : BitConverter.ToInt32(max, 8),
        cur == null ? -1 : BitConverter.ToInt32(cur, 16), max == null ? -1 : BitConverter.ToInt32(max, 16),
        WStr(Ptr(o + 0x280)));
      Console.WriteLine("    @{0}:{1}  {2}", StdStrA(o + 0x368), StdStrA(o + 0x388), CStr(Ptr(Ptr(o + 0x18) + 0x20)));
    }
  }

  // MSVC x64 std::string (16-byte buffer-or-pointer, size at +0x10, capacity at +0x18)
  static string StdStrA(ulong at) {
    var b = Read(at, 32);
    if (b == null) return "?";
    long size = BitConverter.ToInt64(b, 16), cap = BitConverter.ToInt64(b, 24);
    if (size < 0 || size > 512 || cap < size) return "?";
    var d = cap < 16 ? b : Read(BitConverter.ToUInt64(b, 0), (int)size);
    return d == null ? "?" : Encoding.ASCII.GetString(d, 0, (int)size);
  }

  // NUL-terminated ASCII (e.g. an object template's name at template +0x20)
  static string CStr(ulong addr) {
    var b = addr == 0 ? null : Read(addr, 160);
    if (b == null) return "?";
    int z = Array.IndexOf(b, (byte)0);
    return Encoding.ASCII.GetString(b, 0, z >= 0 ? z : b.Length);
  }

  static int IndexOf(byte[] hay, byte[] needle) {
    for (int i = 0; i + needle.Length <= hay.Length; i++) {
      int k = 0;
      while (k < needle.Length && hay[i + k] == needle[k]) k++;
      if (k == needle.Length) return i;
    }
    return -1;
  }

  // Search for text (UTF-16LE and ASCII) inside each object and one pointer hop away
  static void DeepStr(ulong vtRva, int size, string text) {
    var needles = new[] { Encoding.Unicode.GetBytes(text), Encoding.ASCII.GetBytes(text) };
    foreach (var o in Objects(vtRva)) {
      var b = Read(o, size);
      if (b == null) continue;
      foreach (var nd in needles) { int at = IndexOf(b, nd); if (at >= 0) Console.WriteLine("0x{0:x} +0x{1:x} inline ({2})", o, at, nd.Length == text.Length ? "ascii" : "utf16"); }
      for (int i = 0; i + 8 <= size; i += 8) {
        ulong p = BitConverter.ToUInt64(b, i);
        if (p < 0x10000 || p > 0x7fffffffffff) continue;
        var t = Read(p, 128);
        if (t == null) continue;
        foreach (var nd in needles) { int at = IndexOf(t, nd); if (at >= 0) Console.WriteLine("0x{0:x} +0x{1:x} -> +0x{2:x} ({3})", o, i, at, nd.Length == text.Length ? "ascii" : "utf16"); }
      }
    }
  }

  // Search for an int32 inside each object and one pointer hop away (first 256 bytes behind every qword pointer)
  static void DeepFind(ulong vtRva, int size, int value) {
    var objs = new List<ulong>(Objects(vtRva));
    Console.WriteLine("{0} objects", objs.Count);
    foreach (var o in objs) {
      var b = Read(o, size);
      if (b == null) continue;
      for (int i = 0; i + 4 <= size; i += 4)
        if (BitConverter.ToInt32(b, i) == value) Console.WriteLine("0x{0:x} +0x{1:x}", o, i);
      for (int i = 0; i + 8 <= size; i += 8) {
        ulong p = BitConverter.ToUInt64(b, i);
        if (p < 0x10000 || p > 0x7fffffffffff || p % 4 != 0) continue;
        var t = Read(p, 256);
        if (t == null) continue;
        for (int j = 0; j + 4 <= 256; j += 4)
          if (BitConverter.ToInt32(t, j) == value) Console.WriteLine("0x{0:x} +0x{1:x} -> 0x{2:x} +0x{3:x}", o, i, p, j);
      }
    }
  }

  // std::vector<int>-style begin/end pointer pairs inside each object that point at 2..8 ints
  static void Vecs(ulong vtRva, int size) {
    foreach (var o in Objects(vtRva)) {
      var b = Read(o, size);
      if (b == null) continue;
      Console.WriteLine("obj 0x{0:x}", o);
      for (int i = 0; i + 16 <= size; i += 8) {
        ulong beg = BitConverter.ToUInt64(b, i), end = BitConverter.ToUInt64(b, i + 8);
        if (beg == 0 || end <= beg || end - beg > 32 || (end - beg) % 4 != 0 || beg % 4 != 0) continue;
        var v = Read(beg, (int)(end - beg));
        if (v == null) continue;
        var sb = new StringBuilder();
        for (int j = 0; j < v.Length; j += 4) sb.Append(BitConverter.ToInt32(v, j)).Append(' ');
        Console.WriteLine("  +0x{0:x}: {1}", i, sb);
      }
    }
  }

  static void Dump(ulong addr, int len) {
    var b = Read(addr, len);
    if (b == null) { Console.Error.WriteLine("unreadable"); return; }
    for (int i = 0; i < len; i += 16) {
      var hex = new StringBuilder(); var ints = new StringBuilder(); var asc = new StringBuilder();
      for (int j = i; j < i + 16 && j < len; j++) { hex.AppendFormat("{0:x2} ", b[j]); asc.Append(b[j] >= 32 && b[j] < 127 ? (char)b[j] : '.'); }
      for (int j = i; j + 4 <= i + 16 && j + 4 <= len; j += 4) ints.AppendFormat("{0,11} ", BitConverter.ToInt32(b, j));
      Console.WriteLine("+{0:x4}  {1,-48} {2} {3}", i, hex, ints, asc);
    }
  }

  // Every 8-byte-aligned qword equal to <value> inside the main module's image (its .data globals), printed as RVAs
  static void FindMod(ulong value) {
    ulong addr = baseAddr;
    MBI m;
    while (VirtualQueryEx(h, addr, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) != IntPtr.Zero && m.AllocationBase == baseAddr) {
      if (m.State == MEM_COMMIT && (m.Protect & WRITABLE) != 0) {
        var b = Read(m.BaseAddress, (int)m.RegionSize);
        if (b != null)
          for (int i = 0; i + 8 <= b.Length; i += 8)
            if (BitConverter.ToUInt64(b, i) == value) Console.WriteLine("RVA 0x{0:x}", m.BaseAddress + (ulong)i - baseAddr);
      }
      addr = m.BaseAddress + m.RegionSize;
    }
  }

  // Every 4-byte-aligned qword equal to one of <values> in private read/write memory
  static void FindQ(string[] values) {
    var want = new HashSet<ulong>();
    foreach (var v in values) want.Add(Hex(v));
    int total = 0;
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = 0; i + 8 <= len; i += 4)
          if (want.Contains(BitConverter.ToUInt64(b, i))) { total++; Console.WriteLine("0x{0:x} {1:x}", r.Key + off + (ulong)i, BitConverter.ToUInt64(b, i)); }
      }
    }
    Console.Error.WriteLine("{0} match(es)", total);
  }

  static void FindInt(int value) {
    int shown = 0, total = 0;
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = 0; i + 4 <= len; i += 4)
          if (BitConverter.ToInt32(b, i) == value) { total++; if (shown++ < 200) Console.WriteLine("0x{0:x}", r.Key + off + (ulong)i); }
      }
    }
    Console.WriteLine("{0} match(es)", total);
  }

  static void FindStr(string text, string enc) {
    var nd = enc == "utf16" ? Encoding.Unicode.GetBytes(text) : Encoding.ASCII.GetBytes(text);
    int shown = 0, total = 0;
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = Array.IndexOf(b, nd[0]); i >= 0 && i + nd.Length <= len; i = Array.IndexOf(b, nd[0], i + 1)) {
          int k = 1;
          while (k < nd.Length && b[i + k] == nd[k]) k++;
          if (k == nd.Length) { total++; if (shown++ < 200) Console.WriteLine("0x{0:x}", r.Key + off + (ulong)i); }
        }
      }
    }
    Console.Error.WriteLine("{0} match(es)", total);
  }

  // Every chunk-aligned match address of <nd> in private read/write memory
  static List<ulong> FindBytes(byte[] nd) {
    var hits = new List<ulong>();
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = Array.IndexOf(b, nd[0]); i >= 0 && i + nd.Length <= len; i = Array.IndexOf(b, nd[0], i + 1)) {
          int k = 1;
          while (k < nd.Length && b[i + k] == nd[k]) k++;
          if (k == nd.Length) hits.Add(r.Key + off + (ulong)i);
        }
      }
    }
    return hits;
  }

  // MSVC x64 std::basic_string: 16-byte buffer-or-pointer, size at +0x10, capacity at +0x18
  static string StdStr(ulong at, bool wide) {
    var b = Read(at, 32);
    if (b == null) return null;
    long size = BitConverter.ToInt64(b, 16), cap = BitConverter.ToInt64(b, 24);
    if (size < 0 || size > 4096 || cap < size) return null;
    int bytes = (int)size * (wide ? 2 : 1);
    var data = cap < (wide ? 8 : 16) ? b : Read(BitConverter.ToUInt64(b, 0), bytes);
    if (data == null) return null;
    return wide ? Encoding.Unicode.GetString(data, 0, bytes) : Encoding.ASCII.GetString(data, 0, bytes);
  }

  static void AttrPairs(string name) {
    var nd = Encoding.ASCII.GetBytes(name + "\0");
    var strs = new HashSet<ulong>(FindBytes(nd));
    Console.Error.WriteLine("{0} copies of the key text", strs.Count);
    int n = 0;
    foreach (var r in HeapRegions()) {
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int i = 0; i + 64 <= len; i += 8) {
          if (!strs.Contains(BitConverter.ToUInt64(b, i)) || BitConverter.ToInt64(b, i + 16) != name.Length) continue;
          ulong at = r.Key + off + (ulong)i;
          n++;
          Console.WriteLine("0x{0:x}  value \"{1}\"", at, StdStr(at + 32, true));
        }
      }
    }
    Console.Error.WriteLine("{0} pair(s)", n);
  }

  static bool IsKey(string k) { return k != null && k.Length > 0 && k.Length < 128 && k.IndexOf((char)0) < 0; }

  // Walk 64-byte pair<string, wstring> slots backwards and forwards from one hit
  static void AttrVec(ulong at) {
    ulong first = at;
    while (IsKey(StdStr(first - 64, false))) first -= 64;
    Console.WriteLine("first 0x{0:x}", first);
    ulong a = first;
    for (; IsKey(StdStr(a, false)); a += 64) Console.WriteLine("  {0} = {1}", StdStr(a, false), StdStr(a + 32, true));
    Console.WriteLine("end 0x{0:x}  ({1} pairs)", a, (a - first) / 64);
  }

  // Breadth-first search over 8-byte-aligned pointers: reads <span> bytes at each node, follows every heap-looking qword
  static void PathTo(ulong from, string[] targets, int depth, int span) {
    var want = new HashSet<ulong>();
    foreach (var t in targets) want.Add(Hex(t));
    var prev = new Dictionary<ulong, KeyValuePair<ulong, int>>();
    var level = new List<ulong> { from };
    prev[from] = new KeyValuePair<ulong, int>(0, 0);
    for (int d = 0; d < depth && level.Count > 0; d++) {
      var next = new List<ulong>();
      foreach (var node in level) {
        var b = Read(node, span);
        if (b == null) continue;
        for (int i = 0; i + 8 <= span; i += 8) {
          ulong v = BitConverter.ToUInt64(b, i);
          if (v < 0x10000000000 || v > 0x7fffffffffff || (v & 7) != 0 || prev.ContainsKey(v)) continue;
          prev[v] = new KeyValuePair<ulong, int>(node, i);
          if (want.Contains(v)) {
            var chain = new List<string>();
            for (ulong c = v; c != from; c = prev[c].Key) chain.Insert(0, string.Format("+0x{0:x} -> 0x{1:x}", prev[c].Value, c));
            Console.WriteLine("0x{0:x} {1}", from, string.Join(" ", chain));
          }
          next.Add(v);
        }
      }
      Console.Error.WriteLine("depth {0}: {1} nodes", d + 1, next.Count);
      level = next;
    }
  }

  // Printable runs (>= 5 chars) in each node, following heap pointers breadth-first; prints the hop chain
  static void Strs(ulong from, int depth, int span, string filter) {
    var seen = new HashSet<ulong> { from };
    var level = new List<KeyValuePair<ulong, string>> { new KeyValuePair<ulong, string>(from, "") };
    for (int d = 0; d <= depth && level.Count > 0; d++) {
      var next = new List<KeyValuePair<ulong, string>>();
      foreach (var kv in level) {
        var b = Read(kv.Key, span);
        if (b == null) continue;
        foreach (var hit in Runs(b))
          if (filter == null || hit.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            Console.WriteLine("{0}+0x{1:x}: {2}", kv.Value, hit.Key, hit.Value);
        if (d == depth) continue;
        for (int i = 0; i + 8 <= span; i += 8) {
          ulong v = BitConverter.ToUInt64(b, i);
          if (v < 0x10000000000 || v > 0x7fffffffffff || !seen.Add(v)) continue;
          next.Add(new KeyValuePair<ulong, string>(v, kv.Value + string.Format("+0x{0:x}-> ", i)));
        }
      }
      level = next;
    }
  }

  static IEnumerable<KeyValuePair<int, string>> Runs(byte[] b) {
    for (int i = 0; i < b.Length; ) {
      int j = i;
      while (j < b.Length && b[j] >= 32 && b[j] < 127) j++;
      if (j - i >= 5) { yield return new KeyValuePair<int, string>(i, Encoding.ASCII.GetString(b, i, j - i)); i = j; continue; }
      j = i;
      while (j + 1 < b.Length && b[j] >= 32 && b[j] < 127 && b[j + 1] == 0) j += 2;
      if ((j - i) / 2 >= 5) { yield return new KeyValuePair<int, string>(i, "u\"" + Encoding.Unicode.GetString(b, i, j - i) + "\""); i = j; continue; }
      i++;
    }
  }

  // SWG's Crc::calculate (CRC-32, poly 0x04C11DB7, MSB first, init and final xor 0xFFFFFFFF); matches the faction CRCs
  static uint SwgCrc(string text) {
    uint c = 0xFFFFFFFF;
    foreach (byte ch in Encoding.ASCII.GetBytes(text)) {
      c ^= (uint)ch << 24;
      for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
    }
    return ~c;
  }

  // In-order walk of an MSVC std::map from its head node (isnil byte at +0x19); returns node addresses
  static List<ulong> MapNodes(ulong head, int limit) {
    var nodes = new List<ulong>();
    var hb = Read(head, 0x20);
    if (hb == null || hb[0x19] != 1) return nodes;
    var stack = new Stack<ulong>();
    ulong n = BitConverter.ToUInt64(hb, 8); // root
    while ((stack.Count > 0 || n != head) && nodes.Count < limit) {
      if (n != head && n != 0) {
        var b = Read(n, 0x20);
        if (b == null || b[0x19] > 1) return new List<ulong>();
        if (b[0x19] == 1) { n = head; continue; }
        stack.Push(n);
        n = BitConverter.ToUInt64(b, 0); // left
      } else {
        if (stack.Count == 0) break;
        n = stack.Pop();
        nodes.Add(n);
        n = Ptr(n + 0x10); // right
      }
    }
    return nodes;
  }

  static void BuffScan(string tsv) {
    var names = new Dictionary<uint, string>();
    foreach (var line in System.IO.File.ReadAllLines(tsv)) {
      var name = line.Split('	')[0];
      if (name.Length == 0 || name == "NAME") continue;
      names[SwgCrc(name.ToLowerInvariant())] = name;
    }
    Console.WriteLine("{0} buff names", names.Count);
    ulong player = Ptr(baseAddr + 0x21f22c0), target = player == 0 ? 0 : Ptr(player + 0xd18);
    foreach (var obj in new[] { Tuple.Create("player", player), Tuple.Create("target", target) }) {
      Console.WriteLine("== {0} 0x{1:x}", obj.Item1, obj.Item2);
      if (obj.Item2 == 0) continue;
      var body = Read(obj.Item2, 0x1900);
      if (body == null) continue;
      for (int off = 0; off + 16 <= body.Length; off += 8) {
        ulong head = BitConverter.ToUInt64(body, off), size = BitConverter.ToUInt64(body, off + 8);
        if (head < 0x10000 || head > 0x7fffffffffff || size == 0 || size > 300) continue;
        var nodes = MapNodes(head, 400);
        if (nodes.Count == 0 || (ulong)nodes.Count != size) continue;
        int hits = 0;
        var found = new List<string>();
        foreach (var nd in nodes) {
          var kb = Read(nd + 0x20, 4);
          uint key = kb == null ? 0 : BitConverter.ToUInt32(kb, 0);
          string nm;
          if (names.TryGetValue(key, out nm)) { hits++; found.Add(nm); }
        }
        Console.WriteLine("  +0x{0:x}: map size {1}, {2} buff keys {3}", off, size, hits, string.Join(", ", found));
        if (hits > 0) foreach (var nd in nodes) { Console.Write("    node 0x{0:x}:", nd); var nb = Read(nd + 0x20, 0x40); for (int k = 0; k < 0x40; k += 4) Console.Write(" {0:x8}", BitConverter.ToUInt32(nb, k)); Console.WriteLine(); }
      }
    }
  }

  // A plausible MSVC std::map head: isnil byte set and the root's parent pointing back to the head
  static bool IsMapHead(ulong head) {
    if (head < 0x10000 || head > 0x7fffffffffff || (head & 7) != 0) return false;
    var b = Read(head, 0x20);
    if (b == null || b[0x19] != 1) return false;
    ulong root = BitConverter.ToUInt64(b, 8);
    return root == head || Ptr(root + 8) == head;
  }

  static string KeyText(ulong node) {
    var b = Read(node + 0x20, 0x20);
    if (b == null) return "";
    long len = BitConverter.ToInt64(b, 0x10), cap = BitConverter.ToInt64(b, 0x18);
    string str = "";
    if (len > 0 && len < 200 && cap >= len) {
      var sb = cap < 16 ? b : Read(BitConverter.ToUInt64(b, 0), (int)len);
      if (sb != null) { str = Encoding.ASCII.GetString(sb, 0, (int)len); if (str.Any(c => c < 32 || c > 126)) str = ""; }
    }
    return string.Format("key {0:x16}{1}", BitConverter.ToUInt64(b, 0), str.Length > 0 ? " \"" + str + "\"" : "");
  }

  static void ModMaps(ulong from, ulong to, int minSize) {
    var img = Read(baseAddr + from, (int)(to - from));
    if (img == null) { Console.WriteLine("unreadable range"); return; }
    for (int off = 0; off + 16 <= img.Length; off += 8) {
      ulong v = BitConverter.ToUInt64(img, off), next = BitConverter.ToUInt64(img, off + 8);
      if (v < 0x10000 || v > 0x7fffffffffff) continue;
      if (next >= (ulong)minSize && next < 100000 && IsMapHead(v))
        Console.WriteLine("RVA 0x{0:x} direct  size {1,6}  {2}", from + (ulong)off, next, KeyText(Ptr(v + 8)));
      ulong head = Ptr(v), size = Ptr(v + 8);
      if (size >= (ulong)minSize && size < 100000 && IsMapHead(head))
        Console.WriteLine("RVA 0x{0:x} via ptr size {1,6}  {2}", from + (ulong)off, size, KeyText(Ptr(head + 8)));
    }
  }

  static void Ticks(ulong from, ulong to, double lo, double hi) {
    var a = Read(baseAddr + from, (int)(to - from));
    var sw = Stopwatch.StartNew();
    System.Threading.Thread.Sleep(3000);
    var b = Read(baseAddr + from, (int)(to - from));
    double secs = sw.Elapsed.TotalSeconds;
    for (int off = 0; off + 8 <= a.Length; off += 4) {
      double[] va = { BitConverter.ToUInt32(a, off), BitConverter.ToSingle(a, off), off % 8 == 0 ? BitConverter.ToDouble(a, off) : double.NaN };
      double[] vb = { BitConverter.ToUInt32(b, off), BitConverter.ToSingle(b, off), off % 8 == 0 ? BitConverter.ToDouble(b, off) : double.NaN };
      string[] kind = { "u32", "float", "double" };
      for (int k = 0; k < 3; k++) {
        double d = vb[k] - va[k];
        if (va[k] >= lo && va[k] <= hi && d >= secs * 0.6 && d <= secs * 1.4)
          Console.WriteLine("RVA 0x{0:x} {1} {2} -> {3}", from + (ulong)off, kind[k], va[k], vb[k]);
      }
    }
  }

  static void Range(ulong from, ulong to, long lo, long hi) {
    var a = Read(baseAddr + from, (int)(to - from));
    for (int off = 0; off + 8 <= a.Length; off += 4) {
      long u = BitConverter.ToUInt32(a, off), q = off % 8 == 0 ? BitConverter.ToInt64(a, off) : -1;
      if (u >= lo && u <= hi) Console.WriteLine("RVA 0x{0:x} u32 {1}", from + (ulong)off, u);
      else if (q >= lo && q <= hi) Console.WriteLine("RVA 0x{0:x} i64 {1}", from + (ulong)off, q);
    }
  }

  static Dictionary<uint, string> BuffNames(string tsv) {
    var names = new Dictionary<uint, string>();
    foreach (var line in System.IO.File.ReadAllLines(tsv)) {
      var name = line.Split('	')[0];
      if (name.Length > 0 && name != "NAME") names[SwgCrc(name.ToLowerInvariant())] = name;
    }
    return names;
  }

  // key -> (end, duration) from the player's buff map at CreatureObject +0x1508
  static Dictionary<uint, Tuple<uint, uint>> PlayerBuffs(bool target = false) {
    var r = new Dictionary<uint, Tuple<uint, uint>>();
    ulong player = Ptr(baseAddr + 0x21f22c0);
    if (target && player != 0) player = Ptr(player + 0xd18);
    if (player == 0) return r;
    foreach (var n in MapNodes(Ptr(player + 0x1508), 300)) {
      var b = Read(n + 0x20, 0x18);
      if (b != null) r[BitConverter.ToUInt32(b, 0)] = Tuple.Create(BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 0x10));
    }
    return r;
  }

  static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

  static float F(ulong a) { var b = Read(a, 4); return b == null ? float.NaN : BitConverter.ToSingle(b, 0); }
  static void ObjLists(ulong vtRva) {
    var objs = new HashSet<ulong>(Objects(vtRva));
    Console.WriteLine("{0} objects", objs.Count);
    foreach (var r in HeapRegions())
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        int runStart = -1, hits = 0, miss = 0;
        for (int i = 0; i + 8 <= len; i += 8) {
          bool hit = objs.Contains(BitConverter.ToUInt64(b, i));
          if (hit) { if (runStart < 0) { runStart = i; hits = 0; } hits++; miss = 0; }
          else if (runStart >= 0 && ++miss > 2) {
            if (hits >= 8) Console.WriteLine("0x{0:x}: {1} pointers over {2} bytes", r.Key + off + (ulong)runStart, hits, i - runStart);
            runStart = -1;
          }
        }
      }
  }

  static void VecObjs(ulong rva) {
    ulong b = Ptr(baseAddr + rva), e = Ptr(baseAddr + rva + 8);
    Console.WriteLine("{0} entries", (long)(e - b) / 8);
    var counts = new Dictionary<ulong, int>();
    for (ulong p = b; p < e && p - b < 8 * 5000; p += 8) {
      ulong o = Ptr(p), vt = Ptr(o) - baseAddr;
      int c; counts.TryGetValue(vt, out c); counts[vt] = c + 1;
    }
    foreach (var kv in counts) Console.WriteLine("  vtable 0x{0:x}: {1}", kv.Key, kv.Value);
  }

  static string VtName(ulong o) {
    ulong vt = Ptr(o) - baseAddr;
    return vt == 0x1cf7648 ? "Creature" : vt == 0x1cfc748 ? "Ship" : vt == 0x1cf68c8 ? "Tangible" : "vt 0x" + vt.ToString("x");
  }
  static void PosLine(string label, ulong o) {
    Console.WriteLine("{0,-10} 0x{1:x} {2,-12} pos ({3:F1}, {4:F1}, {5:F1})", label, o, o == 0 ? "-" : VtName(o), F(o + 0x84), F(o + 0x94), F(o + 0xa4));
  }
  // Raw bytes of the player and each container up the chain (seat, cell, ship), for diffing sub-target changes
  static void DumpChain(string prefix) {
    ulong o = Ptr(baseAddr + 0x21f22c0);
    for (int i = 0; i < 4 && o != 0; i++) {
      var b = Read(o, 0x3000);
      if (b != null) System.IO.File.WriteAllBytes(prefix + i + ".bin", b);
      ulong cb = Ptr(o + 0xf8);
      o = cb == 0 ? 0 : Ptr(cb + 0x28);
    }
  }

  // Credits: raw bytes of the player creature (c.bin) and its PlayerObject (p.bin), to find cash and bank by diffing
  static void MoneySnap(string prefix) {
    ulong player = Ptr(baseAddr + 0x21f22c0);
    var c = Read(player, 0x3000);
    if (c != null) System.IO.File.WriteAllBytes(prefix + "c.bin", c);
    ulong cont = Ptr(player + 0xb8), beg = Ptr(cont + 0x20), end = Ptr(cont + 0x28);
    for (ulong e = beg; e < end && e - beg < 0x18 * 256; e += 0x18) {
      ulong child = Ptr(e + 0x10);
      if (Ptr(child) == baseAddr + 0x1d0c838) { var p = Read(child, 0x1000); if (p != null) System.IO.File.WriteAllBytes(prefix + "p.bin", p); }
    }
    Console.WriteLine("player 0x{0:x}", player);
  }

  // Aligned ints that changed between two snapshots; pairs with equal and opposite changes (a cash <-> bank move) first
  static void MoneyDiff(string a, string b) {
    foreach (var f in new[] { "c", "p" }) {
      if (!System.IO.File.Exists(a + f + ".bin") || !System.IO.File.Exists(b + f + ".bin")) continue;
      byte[] x = System.IO.File.ReadAllBytes(a + f + ".bin"), y = System.IO.File.ReadAllBytes(b + f + ".bin");
      var ch = new List<Tuple<int, int, int>>();
      for (int o = 0; o + 4 <= Math.Min(x.Length, y.Length); o += 4) {
        int u = BitConverter.ToInt32(x, o), v = BitConverter.ToInt32(y, o);
        if (u != v && u >= 0 && v >= 0 && u < 2000000000 && v < 2000000000) ch.Add(Tuple.Create(o, u, v));
      }
      foreach (var p in ch) foreach (var q in ch)
        if (p.Item1 < q.Item1 && (long)p.Item3 - p.Item2 == -((long)q.Item3 - q.Item2))
          Console.WriteLine("{0} PAIR +0x{1:x} {2} -> {3}  /  +0x{4:x} {5} -> {6}", f, p.Item1, p.Item2, p.Item3, q.Item1, q.Item2, q.Item3);
      foreach (var p in ch) if (Math.Abs((long)p.Item3 - p.Item2) < 100000000) Console.WriteLine("{0} +0x{1:x} {2} -> {3}", f, p.Item1, p.Item2, p.Item3);
    }
  }

  static void Ship() {
    ulong player = Ptr(baseAddr + 0x21f22c0), target = Ptr(player + 0xd18);
    PosLine("player", player);
    ulong o = player;
    for (int i = 0; i < 5; i++) {
      ulong cb = Ptr(o + 0xf8), c = cb == 0 ? 0 : Ptr(cb + 0x28);
      if (c == 0) break;
      PosLine("contained" + i, c);
      var sb = Read(c + 0x1998, 4);
      Console.WriteLine("   vt rva 0x{0:x}  +0x1998 = {1}", Ptr(c) - baseAddr, sb == null ? "?" : BitConverter.ToInt32(sb, 0).ToString());
      o = c;
    }
    PosLine("target", target);
    foreach (var off in new[] { 0x14c0, 0x14e0, 0x1500, 0x1520, 0x9e8, 0xa08 }) Console.Write("+0x{0:x}={1:F0} ", off, F(target + (ulong)off));
    Console.WriteLine();
    // other floats near the shield block that sit between 0 and the max, for a player ship whose front/back may differ
    float mx = F(target + 0x14c0) + F(target + 0x14e0);
    for (ulong off = 0x1400; off < 0x1600; off += 4) { float v = F(target + off); if (v > 0 && v <= mx && v != F(target + 0x14c0)) Console.Write("+0x{0:x}={1:F1} ", off, v); }
    Console.WriteLine();
  }

  static void Aggro(ulong vecRva) {
    ulong player = Ptr(baseAddr + 0x21f22c0);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var all = new List<ulong>();
    if (vecRva == 0) all.AddRange(Objects(0x1cf7648));
    else for (ulong p = Ptr(baseAddr + vecRva), e = Ptr(baseAddr + vecRva + 8); p < e; p += 8) { ulong o = Ptr(p); if (Ptr(o) == baseAddr + 0x1cf7648) all.Add(o); }
    Console.WriteLine("{0} creatures, scan {1} ms; player 0x{2:x}", all.Count, sw.ElapsedMilliseconds, player);
    float px = F(player + 0x84), py = F(player + 0x94), pz = F(player + 0xa4);
    foreach (var o in all) {
      float dx = F(o + 0x84) - px, dy = F(o + 0x94) - py, dz = F(o + 0xa4) - pz;
      double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
      ulong t = Ptr(o + 0xd18);
      var cur = Read(Ptr(o + 0xa00), 4);
      var tid = Read(o + 0xd08, 8);
      string who = t == 0 ? "-" : t == player ? "YOU" : t == o ? "self" : "0x" + t.ToString("x");
      Console.WriteLine("0x{0:x} {1,7:F1} m hp {2,6} target {3,-14} id {4} {5}", o, d, cur == null ? -1 : BitConverter.ToInt32(cur, 0), who,
        tid == null ? 0 : BitConverter.ToInt64(tid, 0), StdStrA(o + 0x368) + ":" + StdStrA(o + 0x388));
    }
  }

  static void BuffDump(long offset) {
    long game = UnixNow() - offset;
    foreach (var t in new[] { false, true })
    foreach (var kv in PlayerBuffs(t)) {
      Console.Write(t ? "target " : "self   ");
      uint end = kv.Value.Item1, dur = kv.Value.Item2;
      string left = end == 0xffffffff || dur == 0 || offset == 0 ? "-" : ((long)end - game).ToString();
      Console.WriteLine("{0:x8} end {1} duration {2} left {3}", kv.Key, end, dur, left);
    }
  }

  static void BuffWatch(string tsv, int minutes) {
    var names = BuffNames(tsv);
    // Baseline only once a character is loaded (and settled), so buffs restored at login don't count as new
    Dictionary<uint, Tuple<uint, uint>> last = null;
    var until = DateTime.Now.AddMinutes(minutes);
    while (DateTime.Now < until) {
      System.Threading.Thread.Sleep(500);
      if (Ptr(baseAddr + 0x21f22c0) == 0) { if (last != null) Console.WriteLine("character unloaded"); last = null; continue; }
      if (last == null) {
        System.Threading.Thread.Sleep(8000);
        last = PlayerBuffs();
        Console.WriteLine("watching {0} buffs; apply a timed buff in game", last.Count);
        continue;
      }
      var cur = PlayerBuffs();
      foreach (var kv in cur) {
        Tuple<uint, uint> old;
        if (last.TryGetValue(kv.Key, out old) && old.Item1 == kv.Value.Item1) continue;
        uint end = kv.Value.Item1, dur = kv.Value.Item2;
        string nm; names.TryGetValue(kv.Key, out nm);
        Console.WriteLine("new/refreshed {0} ({1:x8}): end {2} duration {3}", nm ?? "?", kv.Key, end, dur);
        if (end == 0xffffffff || dur == 0) continue;
        long game = end - dur, unix = UnixNow(), offset = unix - game;
        Console.WriteLine("  game time now ~{0}, unix {1}, offset ~{2}", game, unix, offset);
        Console.WriteLine("  module values within 5 of the offset or the game time:");
        Range(0x1c00000, 0x2400000, offset - 5, offset + 5);
        Range(0x1c00000, 0x2400000, game - 5, game + 5);
        Console.WriteLine("  heap values within 5 of the offset or the game time:");
        int shown = 0;
        foreach (var r in HeapRegions())
          for (ulong off = 0; off < r.Value; off += 1 << 24) {
            int len = (int)Math.Min(1UL << 24, r.Value - off);
            var b = Read(r.Key + off, len);
            if (b == null) continue;
            for (int k = 0; k + 4 <= len; k += 4) {
              long v = BitConverter.ToUInt32(b, k);
              if ((Math.Abs(v - offset) <= 5 || Math.Abs(v - game) <= 5) && shown++ < 60) Console.WriteLine("  0x{0:x} = {1}", r.Key + off + (ulong)k, v);
            }
          }
        return;
      }
      last = cur;
    }
    Console.WriteLine("no new buff seen");
  }

  static Dictionary<ulong, long> HeapRange(long lo, long hi, long lo2, long hi2) {
    var hits = new Dictionary<ulong, long>();
    foreach (var r in HeapRegions())
      for (ulong off = 0; off < r.Value; off += 1 << 24) {
        int len = (int)Math.Min(1UL << 24, r.Value - off);
        var b = Read(r.Key + off, len);
        if (b == null) continue;
        for (int k = 0; k + 4 <= len; k += 4) {
          long v = BitConverter.ToUInt32(b, k);
          if ((v >= lo && v <= hi) || (v >= lo2 && v <= hi2)) hits[r.Key + off + (ulong)k] = v;
        }
      }
    return hits;
  }

  static void HeapClock(long gameLo, long gameHi) {
    long unix = UnixNow();
    var first = HeapRange(gameLo, gameHi, unix - gameHi, unix - gameLo);
    var t0 = DateTime.UtcNow;
    Console.WriteLine("{0} candidates; re-reading in 5 s", first.Count);
    System.Threading.Thread.Sleep(5000);
    double dt = (DateTime.UtcNow - t0).TotalSeconds;
    int shown = 0;
    foreach (var kv in first) {
      var b = Read(kv.Key, 4);
      if (b == null) continue;
      long v = BitConverter.ToUInt32(b, 0), d = v - kv.Value;
      bool ticking = kv.Value <= gameHi && d >= dt - 2 && d <= dt + 2;
      bool offset = kv.Value > gameHi && d == 0;
      if ((ticking || offset) && shown++ < 80)
        Console.WriteLine("0x{0:x} {1} {2} -> {3}  (game time {4})", kv.Key, ticking ? "ticks " : "offset", kv.Value, v, ticking ? v : UnixNow() - v);
    }
    Console.WriteLine("{0} shown", shown);
  }

  static void StrMap(ulong mapObj, string filter) {
    ulong head = Ptr(mapObj);
    foreach (var n in MapNodes(head, 5000)) {
      string key = KeyText(n);
      if (filter != null && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
      ulong v = Ptr(n + 0x40), vt = v == 0 ? 0 : Ptr(v);
      Console.WriteLine("{0}  value 0x{1:x}  vtable RVA 0x{2:x}", key, v, vt >= baseAddr ? vt - baseAddr : vt);
    }
  }
}
