// Live combat lines straight from the client's Combat chat tab, so combat text and the DPS meter don't wait for the
// chat log (the client writes the log in batches of 50 lines, seconds apart; see docs/DEVELOPMENT.md).
//
// READ-ONLY. The process handle is opened with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only, so it cannot write,
// inject, suspend or debug. Never add WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DebugActiveProcess
// or any other access right.
//
// Layout (notes/memory.md -> "Chat tabs"): every chat tab is a heap object whose first qword is base + TAB_MARKER.
// +0x70 std::wstring  the tab's first channel name ("Combat", "GroupChat", ...)
// +0xa8 std::wstring  the tab's text, the last 1001 lines: "hh:mm:ss \#rrggbbtext\#.\n" per line (colour codes inline)
// No fixed pointer path to the tabs is known, so they are found by a heap scan on a background thread (once per game
// process, cached in swgr-chatlive.cfg) and re-checked on every read.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class ChatLive {
  const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;
  const ulong TAB_MARKER = 0x1d9a4c8; // first qword of a chat tab object, module-relative (re-check after a client patch)
  const int TAB_NAME = 0x70, TAB_TEXT = 0xa8;
  const int TailChars = 2048;         // read the last 2048 chars (4 KB) of the tab text per poll
  const int Suffix = 4;               // lines matched to find where the new ones start

  [StructLayout(LayoutKind.Sequential)] struct MBI { public ulong BaseAddress, AllocationBase; public uint AllocationProtect, pad1; public ulong RegionSize; public uint State, Protect, Type, pad2; }
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte[] buf, IntPtr size, out IntPtr read);
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, ulong addr, out MBI mbi, IntPtr len);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

  static readonly string cacheFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "swgr-chatlive.cfg");
  static readonly Regex colour = new Regex(@"\\#(?:[0-9a-fA-F]{6}|\.)");
  static readonly Regex stamp = new Regex(@"^\d\d:\d\d:\d\d ");

  static IntPtr h;
  static int pid;
  static ulong baseAddr, tab;
  static volatile bool scanning;
  static ulong scanResult;
  static DateTime nextScan, nextAttach, lastRead;

  // Every line seen, numbered, so several readers (combat text, DPS meter, Damage page) each get all of them
  static readonly List<string> recent = new List<string>();
  static long firstSeq = 1;              // sequence number of recent[0]
  static List<string> lastLines;         // the tab's tail at the previous read, to find the new lines
  public static bool Working { get { return tab != 0 && lastLines != null; } }

  static byte[] Read(ulong addr, int len) {
    if (h == IntPtr.Zero || addr < 0x10000 || addr > 0x7fffffffffff || len <= 0) return null;
    var b = new byte[len]; IntPtr n;
    return ReadProcessMemory(h, addr, b, (IntPtr)len, out n) && (int)n == len ? b : null;
  }
  static ulong Q(ulong addr) { var b = Read(addr, 8); return b == null ? 0 : BitConverter.ToUInt64(b, 0); }

  // MSVC std::wstring: 16-byte buffer or pointer, size at +0x10, capacity at +0x18. Only the last maxChars are read.
  static string WStr(ulong at, int maxChars) {
    var b = Read(at, 32); if (b == null) return null;
    long size = BitConverter.ToInt64(b, 16), cap = BitConverter.ToInt64(b, 24);
    if (size < 0 || size > 8000000 || cap < size) return null;
    if (cap < 8) return Encoding.Unicode.GetString(b, 0, (int)size * 2);
    long n = Math.Min(size, maxChars);
    var d = Read(BitConverter.ToUInt64(b, 0) + (ulong)((size - n) * 2), (int)n * 2);
    return d == null ? null : Encoding.Unicode.GetString(d);
  }

  static bool Attach() {
    var p = Process.GetProcessesByName("SwgClient_r").FirstOrDefault();
    if (p == null) return false;
    if (h != IntPtr.Zero && p.Id == pid) return true;
    Detach();
    h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, p.Id);
    if (h == IntPtr.Zero) return false;
    try { baseAddr = (ulong)p.MainModule.BaseAddress.ToInt64(); } catch { Detach(); return false; }
    pid = p.Id;
    // the tab found for this game process last time (survives a tools restart)
    try {
      var c = File.ReadAllText(cacheFile).Split(' ');
      if (int.Parse(c[0]) == pid) { ulong t = Convert.ToUInt64(c[1], 16); if (IsTab(t)) Use(t); }
    } catch { }
    return true;
  }

  static void Detach() { if (h != IntPtr.Zero) CloseHandle(h); h = IntPtr.Zero; pid = 0; tab = 0; lastLines = null; }

  static bool IsTab(ulong t) { return t != 0 && Q(t) == baseAddr + TAB_MARKER; }

  static void Use(ulong t) {
    tab = t; lastLines = null;
    try { File.WriteAllText(cacheFile, pid + " " + t.ToString("x")); } catch { }
  }

  // How good a combat source a tab is: one named Combat wins, otherwise the one with the most combat lines
  static int Score(ulong t) {
    var name = WStr(t + TAB_NAME, 64);
    var text = WStr(t + TAB_TEXT, TailChars);
    if (text == null) return -1;
    int pts = Regex.Matches(text, @" pts\\#").Count + Regex.Matches(text, @" points? of ").Count;
    return (name == "Combat" ? 100000 : 0) + pts;
  }

  // Background heap scan for the tab marker, split over several threads (one pass is ~1 min single-threaded)
  static void StartScan() {
    if (scanning) return;
    scanning = true;
    IntPtr hs = h; ulong marker = baseAddr + TAB_MARKER;
    var t = new Thread(() => {
      var found = new List<ulong>();
      try {
        var regions = new List<KeyValuePair<ulong, ulong>>();
        ulong a = 0x10000; MBI m;
        while (VirtualQueryEx(hs, a, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) != IntPtr.Zero) {
          if (m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0 && (m.Protect & 0x101) == 0)
            regions.Add(new KeyValuePair<ulong, ulong>(m.BaseAddress, m.RegionSize));
          ulong next = m.BaseAddress + m.RegionSize; if (next <= a) break; a = next;
        }
        int idx = -1;
        var workers = Enumerable.Range(0, Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1))).Select(_ => new Thread(() => {
          var buf = new byte[1 << 22];
          int i;
          while ((i = Interlocked.Increment(ref idx)) < regions.Count) {
            var r = regions[i];
            for (ulong off = 0; off < r.Value; off += (ulong)buf.Length) {
              int len = (int)Math.Min((ulong)buf.Length, r.Value - off); IntPtr n;
              if (!ReadProcessMemory(hs, r.Key + off, buf, (IntPtr)len, out n)) continue;
              for (int j = 0; j + 8 <= (int)n; j += 8)
                if (BitConverter.ToUInt64(buf, j) == marker) lock (found) found.Add(r.Key + off + (ulong)j);
            }
          }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
      } catch { }
      ulong best = 0; int bestScore = 0;
      foreach (var c in found) { int s = Score(c); if (s > bestScore) { best = c; bestScore = s; } }
      scanResult = best;
      scanning = false;
    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
    t.Start();
  }

  // Reads the tab and appends any new lines. Cheap: two small reads. Called by every reader; runs at most every 8 ms.
  static void Update() {
    var now = DateTime.Now;
    if ((now - lastRead).TotalMilliseconds < 8) return;
    lastRead = now;
    if (h == IntPtr.Zero || Read(baseAddr, 2) == null) {
      if (h != IntPtr.Zero) Detach();
      if (now < nextAttach) return;
      nextAttach = now.AddSeconds(5);
      if (!Attach()) return;
    }
    if (!scanning && scanResult != 0) { if (IsTab(scanResult)) Use(scanResult); scanResult = 0; }
    if (tab != 0 && !IsTab(tab)) { tab = 0; lastLines = null; } // tab closed or the UI rebuilt (relog)
    if (tab == 0) {
      if (!scanning && now >= nextScan) { nextScan = now.AddSeconds(30); StartScan(); }
      return;
    }
    var text = WStr(tab + TAB_TEXT, TailChars);
    if (text == null) return;
    // the first piece is usually cut mid-line by the 4 KB window: only whole, timestamped lines count
    var lines = text.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).Where(l => stamp.IsMatch(l)).ToList();
    if (lines.Count == 0) return;
    if (lastLines == null) { lastLines = lines; return; } // first read: start from here, don't replay
    int start = NewFrom(lastLines, lines);
    if (start < 0) { lastLines = lines; return; } // lost track (a burst bigger than the tail): resync quietly
    for (int i = start; i < lines.Count; i++) recent.Add("[Combat]  " + colour.Replace(lines[i], ""));
    lastLines = lines;
    if (recent.Count > 2000) { recent.RemoveRange(0, 1000); firstSeq += 1000; }
  }

  // Index in cur of the first line after the last Suffix lines of prev, or -1 if they can't be found
  static int NewFrom(List<string> prev, List<string> cur) {
    int k = Math.Min(Suffix, prev.Count);
    for (int end = cur.Count - 1; end >= k - 1; end--) {
      bool ok = true;
      for (int j = 0; j < k && ok; j++) ok = cur[end - j] == prev[prev.Count - 1 - j];
      if (ok) return end + 1;
    }
    return -1;
  }

  // For CombatTracker.LiveLines: the lines after sequence number seq (log format, "[Combat]  hh:mm:ss text"),
  // and the new seq. null while the Combat tab isn't being read (the caller falls back to the chat log).
  public static Tuple<long, List<string>> Since(long seq) {
    Update();
    if (!Working) return null;
    long end = firstSeq + recent.Count;
    if (seq <= 0) return Tuple.Create(end, new List<string>()); // a new reader starts at the present
    int from = (int)Math.Max(0, seq - firstSeq);
    return Tuple.Create(end, recent.Skip(from).ToList());
  }
}
