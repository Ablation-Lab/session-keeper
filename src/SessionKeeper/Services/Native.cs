using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SessionKeeper.Services;

/// <summary>
/// Read-only Win32 helpers. Every handle is opened with query/read rights only;
/// nothing here can modify or terminate another process.
/// </summary>
internal static class Native
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(SafeProcessHandle h, out bool wow64);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle h, int infoClass, ref ProcessBasicInformation pbi, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle h, IntPtr address, [Out] byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    internal readonly record struct ProcessFacts(string? ImagePath, long? CreationFileTimeUtc, int? ParentPid);

    /// <summary>Image path, creation time and parent PID using limited query rights.</summary>
    public static ProcessFacts QueryProcess(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return new ProcessFacts(null, null, null);

        string? path = null;
        foreach (var capacity in new[] { 520, 32768 }) // most paths fit the first; long paths get a second try
        {
            var sb = new StringBuilder(capacity);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) { path = sb.ToString(0, size); break; }
            if (Marshal.GetLastWin32Error() != 122) break; // ERROR_INSUFFICIENT_BUFFER
        }

        long? created = GetProcessTimes(h, out var c, out _, out _, out _) ? c : null;

        int? parent = null;
        var pbi = new ProcessBasicInformation();
        if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0)
            parent = (int)pbi.InheritedFromUniqueProcessId.ToInt64();

        return new ProcessFacts(path, created, parent);
    }

    /// <summary>
    /// Reads the current directory of another 64-bit process from its PEB.
    /// Returns null when access is denied (e.g. elevated processes) or anything looks off.
    /// </summary>
    public static string? TryGetCurrentDirectory(int pid)
    {
        if (!Environment.Is64BitProcess) return null;
        try
        {
            using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h.IsInvalid) return null;
            if (IsWow64Process(h, out var wow) && wow) return null;

            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB->ProcessParameters at 0x20 (x64).
            var ptr = new byte[8];
            if (!ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, ptr, 8, out _)) return null;
            var parameters = (IntPtr)BitConverter.ToInt64(ptr, 0);
            if (parameters == IntPtr.Zero) return null;

            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (UNICODE_STRING) at 0x38.
            var us = new byte[16];
            if (!ReadProcessMemory(h, parameters + 0x38, us, 16, out _)) return null;
            int length = BitConverter.ToUInt16(us, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(us, 8);
            if (length == 0 || length > 32767 * 2 || buffer == IntPtr.Zero) return null;

            var chars = new byte[length];
            if (!ReadProcessMemory(h, buffer, chars, length, out var read) || read.ToInt64() != length) return null;
            var dir = Encoding.Unicode.GetString(chars);
            if (dir.Length > 3) dir = dir.TrimEnd('\\');
            return dir;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ terminal detection (all read-only)

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int ProcessCommandLineInformation = 60; // Windows 8.1+, needs only limited query rights

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle h, int infoClass, IntPtr buffer, int length, out int returnLength);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    /// <summary>One entry of the system process list: PID, parent PID (which may be stale) and exe file name.</summary>
    internal readonly record struct ProcEntry(int Pid, int ParentPid, string ExeName);

    /// <summary>Every process with its parent PID and exe name, from one system snapshot (no process is opened).</summary>
    public static List<ProcEntry> SnapshotProcesses()
    {
        var list = new List<ProcEntry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                list.Add(new ProcEntry((int)e.ProcessId, (int)e.ParentProcessId, e.ExeFile ?? ""));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    internal enum ConsoleKind { Classic, Pseudo }

    /// <summary>
    /// A console window and the process Windows reports as its owner (the console's first client). Hidden classic
    /// windows are reported too (Visible = false): they never become sessions, but a process that owns any console
    /// of its own isn't part of another terminal's session.
    /// </summary>
    internal readonly record struct ConsoleOwner(int Pid, ConsoleKind Kind, bool Visible = true);

    /// <summary>
    /// Console windows on the desktop and their owners. A classic console window (ConsoleWindowClass) is a
    /// session only when visible: hidden ones belong to background tools. Windows Terminal (and other ConPTY hosts)
    /// give each tab or pane an invisible PseudoConsoleWindow owned by the tab's root process. Reads window classes,
    /// visibility and owners only; no message is sent to any window.
    /// </summary>
    public static List<ConsoleOwner> ConsoleWindowOwners()
    {
        var list = new List<ConsoleOwner>();
        var cls = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            cls.Clear();
            if (GetClassName(hwnd, cls, cls.Capacity) == 0) return true;
            var name = cls.ToString();
            ConsoleKind? kind = name == "PseudoConsoleWindow" ? ConsoleKind.Pseudo
                : name == "ConsoleWindowClass" ? ConsoleKind.Classic
                : null;
            if (kind != null && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid > 0)
                list.Add(new ConsoleOwner(pid, kind.Value, kind == ConsoleKind.Pseudo || IsWindowVisible(hwnd)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Another process's full command line, using limited query rights. Null if it can't be read.</summary>
    public static string? TryGetCommandLine(int pid)
    {
        try
        {
            using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) return null;
            for (int attempt = 0; attempt < 2; attempt++) // the size can change between the two calls: try once more
            {
                NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
                if (size <= 2 * IntPtr.Size || size > 128 * 1024) return null;
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, size, out _) != 0) continue;
                    // A UNICODE_STRING header, then the text, all inside our own buffer.
                    int length = (ushort)Marshal.ReadInt16(buffer); // in bytes
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    long offset = text.ToInt64() - buffer.ToInt64();
                    if (length == 0 || offset < 2 * IntPtr.Size || offset + length > size) return null;
                    return Marshal.PtrToStringUni(text, length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an exe is a GUI (windowed) program, read from the PE header of the file on disk (the process itself
    /// isn't touched). Null if the file can't be read or isn't a PE image.
    /// </summary>
    public static bool? IsGuiImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[1024];
            int read = fs.Read(header, 0, header.Length);
            if (read < 0x40 || header[0] != 'M' || header[1] != 'Z') return null;
            int pe = BitConverter.ToInt32(header, 0x3C);
            if (pe < 0 || pe + 24 + 70 > read || header[pe] != 'P' || header[pe + 1] != 'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return null;
            int subsystem = BitConverter.ToUInt16(header, pe + 24 + 68); // same offset for PE32 and PE32+
            return subsystem == 2; // IMAGE_SUBSYSTEM_WINDOWS_GUI (3 = console)
        }
        catch
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int addressFamily, int tableClass, int reserved);

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>A TCP port some process is listening on (IPv4 or IPv6).</summary>
    internal readonly record struct Listener(int Pid, int Port);

    /// <summary>
    /// Every listening TCP port with its owning process, from the system's TCP table (read-only: no process is
    /// opened and no connection is made). Empty if the table can't be read.
    /// </summary>
    public static List<Listener> ListeningTcpPorts()
    {
        var list = new List<Listener>();
        // Row layouts: MIB_TCPROW_OWNER_PID is 24 bytes (port at 8, PID at 20); MIB_TCP6ROW_OWNER_PID is 56 bytes
        // (port at 20, PID at 52). Both tables start with a 4-byte entry count.
        foreach (var (family, rowSize, portOffset, pidOffset) in new[] { (AF_INET, 24, 8, 20), (AF_INET6, 56, 20, 52) })
        {
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                for (int attempt = 0; attempt < 3 && size > 0 && size < 16 * 1024 * 1024; attempt++)
                {
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int got = size;
                        var rc = GetExtendedTcpTable(buffer, ref got, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                        if (rc == ERROR_INSUFFICIENT_BUFFER) { size = got + 1024; continue; } // the table grew: retry bigger
                        if (rc != 0) break;
                        int count = Marshal.ReadInt32(buffer);
                        if (count < 0 || 4L + (long)count * rowSize > size) break;
                        for (int i = 0; i < count; i++)
                        {
                            int row = 4 + i * rowSize;
                            int raw = Marshal.ReadInt32(buffer, row + portOffset);
                            int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order in the low 16 bits
                            int pid = Marshal.ReadInt32(buffer, row + pidOffset);
                            if (port > 0 && pid > 0) list.Add(new Listener(pid, port));
                        }
                        break;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            catch
            {
                // This family's table couldn't be read: carry on with the other.
            }
        }
        return list;
    }

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // pre-20H1 builds
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
