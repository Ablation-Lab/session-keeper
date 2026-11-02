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
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
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
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString(0, size);

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
            using var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
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
