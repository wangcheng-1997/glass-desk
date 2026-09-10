using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlassDesk.Models;
using GlassDesk.Native;

namespace GlassDesk.Services;

public sealed class WindowTargetResolver
{
    public WindowCandidate? GetForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return hwnd == nint.Zero ? null : TryCreateCandidate(hwnd);
    }

    public IReadOnlyList<WindowCandidate> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowCandidate>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var candidate = TryCreateCandidate(hwnd);
            if (candidate is not null && candidate.IsVisible && candidate.Width > 0 && candidate.Height > 0)
            {
                windows.Add(candidate);
            }

            return true;
        }, nint.Zero);

        return windows;
    }

    public bool StillMatches(WindowLease lease)
    {
        if (lease.ProcessStartFileTimeUtc == 0) return false;
        var candidate = TryCreateCandidate(lease.Hwnd);
        return candidate is not null
               && candidate.ProcessStartFileTimeUtc != 0
               && candidate.ProcessId == lease.ProcessId
               && candidate.ProcessStartFileTimeUtc == lease.ProcessStartFileTimeUtc
               && string.Equals(candidate.ProcessPath, lease.ProcessPath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(candidate.WindowClassName, lease.WindowClassName, StringComparison.Ordinal);
    }

    public WindowCandidate? TryCreateCandidate(nint hwnd)
    {
        if (hwnd == nint.Zero || !NativeMethods.IsWindow(hwnd)) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        var titleLength = Math.Max(256, NativeMethods.GetWindowTextLength(hwnd) + 1);
        var titleBuffer = new StringBuilder(titleLength);
        NativeMethods.GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);

        var classBuffer = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, classBuffer, classBuffer.Capacity);

        var processPath = TryGetProcessPath(pid);
        var processName = string.IsNullOrWhiteSpace(processPath)
            ? $"PID {pid}"
            : Path.GetFileName(processPath);
        var startFileTime = TryGetProcessStartFileTime(pid);
        var integrity = TryGetIntegrityLevel(pid);
        var protectedContent = NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity)
                              && affinity != 0;

        NativeMethods.GetWindowRect(hwnd, out var rect);

        return new WindowCandidate(
            hwnd,
            pid,
            startFileTime,
            CanonicalizePath(processPath),
            processName,
            classBuffer.ToString(),
            titleBuffer.ToString(),
            integrity,
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsIconic(hwnd),
            protectedContent,
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    private static string TryGetProcessPath(uint pid)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == nint.Zero) return string.Empty;

        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)
                ? buffer.ToString()
                : string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static long TryGetProcessStartFileTime(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).StartTime.ToFileTimeUtc();
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetIntegrityLevel(uint pid)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == nint.Zero) return 0;

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token)) return 0;
            try
            {
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, nint.Zero, 0, out var size);
                if (size <= 0) return 0;
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, size, out _)) return 0;
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = NativeMethods.GetSidSubAuthorityCount(sid);
                    if (count == 0) return 0;
                    var authority = Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
                    return authority switch
                    {
                        >= NativeMethods.SECURITY_MANDATORY_SYSTEM_RID => 4,
                        >= NativeMethods.SECURITY_MANDATORY_HIGH_RID => 3,
                        >= NativeMethods.SECURITY_MANDATORY_MEDIUM_RID => 2,
                        >= NativeMethods.SECURITY_MANDATORY_LOW_RID => 1,
                        _ => 0
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static string CanonicalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
