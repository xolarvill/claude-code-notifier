// notify-taskbar.cs
//
// Flashes the Windows taskbar button of the window hosting this process --
// the same "keep flashing until you come back" behaviour WeChat uses.
//
// Invoked by Claude Code's Stop and Notification hooks. Exits 0 no matter
// what: a notification must never break a session.
//
// Build (source of truth lives beside the binary):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//       /target:exe /optimize+ /out:notify-taskbar.exe notify-taskbar.cs
//
// Run with --verbose to print how the window was resolved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

public static class TaskbarFlash
{
    // ---------------------------------------------------------------- flash

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint   cbSize;
        public IntPtr hwnd;
        public uint   dwFlags;
        public uint   uCount;
        public uint   dwTimeout;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_ALL       = 0x00000003; // caption + taskbar button
    private const uint FLASHW_TIMERNOFG = 0x0000000C; // keep flashing until foregrounded

    /// <summary>
    /// Flashes the taskbar button until the window is brought to the foreground.
    /// Returns false when the window was already active -- i.e. you are looking
    /// at the terminal, so there is nothing to tell you about. That is correct.
    /// </summary>
    public static bool Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        FLASHWINFO f = new FLASHWINFO();
        f.cbSize    = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
        f.hwnd      = hwnd;
        f.dwFlags   = FLASHW_ALL | FLASHW_TIMERNOFG;
        f.uCount    = uint.MaxValue;
        f.dwTimeout = 0;

        return FlashWindowEx(ref f);
    }

    // -------------------------------------------------------------- windows

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const uint GW_OWNER = 4;
    private const int  DWMWA_CLOAKED = 14;

    /// <summary>
    /// True for windows a UWP/helper process left hidden. IsWindowVisible
    /// reports these as visible, so without this check the ancestor walk can
    /// latch onto explorer's ThumbnailDeviceHelperWnd instead of the terminal.
    /// </summary>
    private static bool IsCloaked(IntPtr hWnd)
    {
        int cloaked;
        int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static bool IsUsableTopLevel(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false; // dialog / tool window
        return !IsCloaked(hWnd);
    }

    /// <summary>
    /// First usable top-level window belonging to any of <paramref name="pids"/>,
    /// or IntPtr.Zero.
    /// </summary>
    public static IntPtr FindWindow(IList<uint> pids)
    {
        if (pids == null || pids.Count == 0) return IntPtr.Zero;

        var wanted = new HashSet<uint>(pids);
        IntPtr found = IntPtr.Zero;

        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsUsableTopLevel(hWnd)) return true;

            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!wanted.Contains(pid)) return true;

            found = hWnd;
            return false; // stop enumerating
        }, IntPtr.Zero);

        return found;
    }

    // ------------------------------------------------------------ processes

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint   dwSize;
        public uint   cntUsage;
        public uint   th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint   th32ModuleID;
        public uint   cntThreads;
        public uint   th32ParentProcessID;
        public int    pcPriClassBase;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    private struct Proc
    {
        public uint   Parent;
        public string Exe;
    }

    /// <summary>One snapshot of the process table: pid -> parent pid + exe name.</summary>
    private static Dictionary<uint, Proc> Snapshot()
    {
        var map = new Dictionary<uint, Proc>();

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE || snap == IntPtr.Zero) return map;

        try
        {
            var pe = new PROCESSENTRY32W();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
            int size = (int)pe.dwSize;

            if (Process32FirstW(snap, ref pe))
            {
                do
                {
                    var p = new Proc();
                    p.Parent = pe.th32ParentProcessID;
                    p.Exe    = (pe.szExeFile ?? "").ToLowerInvariant();
                    map[pe.th32ProcessID] = p;

                    pe.dwSize = (uint)size;
                }
                while (Process32NextW(snap, ref pe));
            }
        }
        finally { CloseHandle(snap); }

        return map;
    }

    private static readonly string[] TERMINAL_EXES =
    {
        "windowsterminal.exe", "openconsole.exe", "conhost.exe",
        "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe",
        "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "wsl.exe",
        "wezterm-gui.exe", "alacritty.exe", "mintty.exe", "tabby.exe",
        "hyper.exe", "conemu64.exe", "conemu.exe"
    };

    private static bool IsTerminal(string exe)
    {
        foreach (string t in TERMINAL_EXES)
            if (exe == t) return true;
        return false;
    }

    /// <summary>
    /// The shell owns a lot of invisible helper windows and is never the thing
    /// the user is looking at, so it must not win the fallback sweep.
    /// </summary>
    private static bool IsShell(string exe)
    {
        return exe == "explorer.exe" || exe == "sihost.exe" ||
               exe == "shellexperiencehost.exe" || exe == "startmenuexperiencehost.exe";
    }

    /// <summary>This process's ancestors, nearest first.</summary>
    private static List<uint> AncestorsOf(Dictionary<uint, Proc> procs, uint pid, int maxDepth)
    {
        var list = new List<uint>();
        uint cur = pid;

        for (int i = 0; i < maxDepth; i++)
        {
            Proc p;
            if (!procs.TryGetValue(cur, out p)) break;
            if (p.Parent == 0 || p.Parent == cur) break;

            list.Add(p.Parent);
            cur = p.Parent;
        }

        return list;
    }

    /// <summary>
    /// The window that owns this console, or IntPtr.Zero.
    ///
    /// Prefers an ancestor that is itself a terminal emulator (the emulator is
    /// the outermost one, hence the reversed scan); falls back to any ancestor
    /// that is not the shell, then to any terminal process on the machine.
    /// </summary>
    public static IntPtr ResolveConsoleWindow(out string how)
    {
        how = "none";

        Dictionary<uint, Proc> procs = Snapshot();
        List<uint> ancestors = AncestorsOf(procs, GetCurrentProcessId(), 16);

        // 1. An ancestor that IS a terminal emulator.
        var terminals = new List<uint>();
        for (int i = ancestors.Count - 1; i >= 0; i--)
        {
            Proc p;
            if (procs.TryGetValue(ancestors[i], out p) && IsTerminal(p.Exe))
                terminals.Add(ancestors[i]);
        }

        var trace = new List<string>();
        foreach (uint pid in ancestors)
        {
            Proc p;
            trace.Add(procs.TryGetValue(pid, out p) ? pid + ":" + p.Exe : pid + ":(gone)");
        }
        Trace = string.Join(" <- ", trace.ToArray());

        IntPtr h = FindWindow(terminals);
        if (h != IntPtr.Zero) { how = "terminal ancestor"; return h; }

        // 2. Any ancestor that is not the shell.
        var others = new List<uint>();
        foreach (uint pid in ancestors)
        {
            Proc p;
            if (procs.TryGetValue(pid, out p) && !IsShell(p.Exe))
                others.Add(pid);
        }

        h = FindWindow(others);
        if (h != IntPtr.Zero) { how = "other ancestor"; return h; }

        // 3. Process tree broken (relaunched shell, detached console): any
        //    terminal emulator on the machine.
        var all = new List<uint>();
        foreach (KeyValuePair<uint, Proc> kv in procs)
            if (IsTerminal(kv.Value.Exe)) all.Add(kv.Key);

        h = FindWindow(all);
        if (h != IntPtr.Zero) how = "machine-wide scan";

        return h;
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>Ancestor chain from the last resolve, for --verbose.</summary>
    public static string Trace = "";

    /// <summary>Human-readable description of a window, for --verbose.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);

        return string.Format("hwnd=0x{0:X} pid={1} foreground={2} cloaked={3} title=\"{4}\" class=\"{5}\"",
                             hwnd.ToInt64(), pid,
                             GetForegroundWindow() == hwnd,
                             IsCloaked(hwnd),
                             WindowText(hwnd), ClassName(hwnd));
    }

    private static string ReadChars(Func<char[], int, int> read)
    {
        var buf = new char[512];
        int n = read(buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    private static string WindowText(IntPtr h) { return ReadChars((b, n) => GetWindowTextW(h, b, n)); }
    private static string ClassName(IntPtr h)  { return ReadChars((b, n) => GetClassNameW(h, b, n)); }

    // ----------------------------------------------------------------- log

    /// <summary>
    /// One line per run, so a hook that silently does nothing can be told apart
    /// from one that never ran at all. Capped, so it cannot grow forever.
    /// </summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "notify-taskbar.log");

    private const long MaxLogBytes = 64 * 1024;

    public static void Log(string message)
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                string[] all = File.ReadAllLines(LogPath);
                int keep = Math.Min(all.Length, 100);
                var tail = new string[keep];
                Array.Copy(all, all.Length - keep, tail, 0, keep);
                File.WriteAllLines(LogPath, tail);
            }

            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Logging must never be the reason a notification fails.
        }
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        bool verbose = args != null && Array.IndexOf(args, "--verbose") >= 0;

        try
        {
            string how;
            IntPtr hwnd = TaskbarFlash.ResolveConsoleWindow(out how);

            if (hwnd != IntPtr.Zero)
            {
                bool flashed = TaskbarFlash.Flash(hwnd);

                // Record the ancestor chain only when the precise path did not
                // win, so a fallback that fires for an unexplained reason is
                // diagnosable from the log alone.
                string detail = how == "terminal ancestor"
                    ? ""
                    : " | ancestors: " + TaskbarFlash.Trace;

                TaskbarFlash.Log(string.Format("via {0} | {1} | flashed={2}{3}",
                                               how, TaskbarFlash.Describe(hwnd), flashed, detail));

                if (verbose)
                {
                    Console.WriteLine("resolved via : {0}", how);
                    Console.WriteLine("ancestors    : {0}", TaskbarFlash.Trace);
                    Console.WriteLine("window       : {0}", TaskbarFlash.Describe(hwnd));
                    Console.WriteLine("flashed      : {0}", flashed);
                    if (!flashed)
                        Console.WriteLine("               (window already foreground - nothing to signal)");
                }
            }
            else
            {
                TaskbarFlash.Log("no window found - nothing flashed");

                if (verbose)
                    Console.WriteLine("no window found - nothing flashed");
            }
        }
        catch (Exception ex)
        {
            // Never surface anything to the session.
            TaskbarFlash.Log("error: " + ex.Message);
            if (verbose) Console.WriteLine("error        : {0}", ex.Message);
        }

        return 0;
    }
}
