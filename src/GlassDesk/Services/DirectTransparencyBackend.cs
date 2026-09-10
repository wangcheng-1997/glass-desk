using System.Runtime.InteropServices;
using GlassDesk.Models;
using GlassDesk.Native;

namespace GlassDesk.Services;

public sealed class DirectTransparencyBackend
{
    private readonly WindowTargetResolver _resolver;
    private readonly Dictionary<nint, WindowLease> _leases = new();

    public DirectTransparencyBackend(WindowTargetResolver resolver)
    {
        _resolver = resolver;
    }

    public IReadOnlyCollection<WindowLease> ActiveLeases => _leases.Values;

    public GlassDeskResult Apply(nint hwnd, byte opacityPercent, bool allowExistingLayered = false)
    {
        opacityPercent = (byte)Math.Clamp((int)opacityPercent, 1, 100);
        var candidate = _resolver.TryCreateCandidate(hwnd);
        if (candidate is null) return Result(GlassDeskStatus.TargetGone, "目标窗口已关闭或句柄无效。");
        if (candidate.ProcessStartFileTimeUtc == 0)
        {
            return Result(GlassDeskStatus.TargetIdentityUnavailable, "无法确认目标进程启动时间，未修改窗口以避免 HWND 复用误操作。");
        }
        if (candidate.IsProtectedContent) return Result(GlassDeskStatus.Unsupported, "目标窗口声明了受保护内容，未修改。");

        if (_leases.TryGetValue(hwnd, out var activeLease))
        {
            if (!_resolver.StillMatches(activeLease))
            {
                _leases.Remove(hwnd);
                return Result(GlassDeskStatus.TargetChanged, "目标窗口已经重建，已丢弃旧窗口租约。");
            }

            if (!SetAlpha(hwnd, opacityPercent, out var error))
            {
                return Result(GlassDeskStatus.LayerAttributesFailed, $"更新透明度失败：{NativeMethods.LastErrorMessage(error)}。", error);
            }

            activeLease.AppliedAlpha = opacityPercent;
            return Result(GlassDeskStatus.Applied, $"透明度已调整为 {opacityPercent}%。");
        }

        if (!TryReadExStyle(hwnd, out var originalStyle, out var readError))
        {
            return Result(GlassDeskStatus.ExStyleReadFailed, $"读取窗口样式失败：{NativeMethods.LastErrorMessage(readError)}。", readError);
        }

        if ((originalStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            if (!allowExistingLayered)
            {
                return Result(GlassDeskStatus.AlreadyLayeredUnsupported, "目标窗口已经是 layered window，自动规则不会覆盖其未知的原始透明状态。");
            }

            if (!SetAlpha(hwnd, opacityPercent, out var existingAlphaError))
            {
                return Result(GlassDeskStatus.LayerAttributesFailed, $"更新已有 layered window 的透明度失败：{NativeMethods.LastErrorMessage(existingAlphaError)}。", existingAlphaError);
            }

            if (!RefreshFrame(hwnd, out var existingFrameError))
            {
                return Result(GlassDeskStatus.FrameRefreshFailed, $"刷新已有 layered window 失败：{NativeMethods.LastErrorMessage(existingFrameError)}。", existingFrameError);
            }

            _leases[hwnd] = new WindowLease
            {
                Hwnd = hwnd,
                ProcessId = candidate.ProcessId,
                ProcessStartFileTimeUtc = candidate.ProcessStartFileTimeUtc,
                OriginalExStyle = originalStyle,
                AppliedExStyle = originalStyle,
                OwnsLayeredStyle = false,
                AppliedAlpha = opacityPercent,
                ProcessPath = candidate.ProcessPath,
                WindowClassName = candidate.WindowClassName
            };

            return Result(GlassDeskStatus.Applied, $"已将已有 layered 窗口设置为 {opacityPercent}%；退出时保留其 layered 样式。");
        }

        var appliedStyle = originalStyle | NativeMethods.WS_EX_LAYERED;
        if (!TryWriteExStyle(hwnd, appliedStyle, out var writeError))
        {
            return ClassifyWriteFailure(writeError);
        }

        var provisionalLease = new WindowLease
        {
            Hwnd = hwnd,
            ProcessId = candidate.ProcessId,
            ProcessStartFileTimeUtc = candidate.ProcessStartFileTimeUtc,
            OriginalExStyle = originalStyle,
            AppliedExStyle = appliedStyle,
            OwnsLayeredStyle = true,
            AppliedAlpha = opacityPercent,
            ProcessPath = candidate.ProcessPath,
            WindowClassName = candidate.WindowClassName
        };
        _leases[hwnd] = provisionalLease;

        if (!SetAlpha(hwnd, opacityPercent, out var alphaError))
        {
            if (TryRollbackStyle(hwnd, originalStyle, appliedStyle)) _leases.Remove(hwnd);
            return Result(
                _leases.ContainsKey(hwnd) ? GlassDeskStatus.RestoreConflict : GlassDeskStatus.LayerAttributesFailed,
                _leases.ContainsKey(hwnd)
                    ? $"设置透明度失败且样式回滚失败，已保留恢复租约：{NativeMethods.LastErrorMessage(alphaError)}。"
                    : $"设置透明度失败：{NativeMethods.LastErrorMessage(alphaError)}。",
                alphaError);
        }

        if (!RefreshFrame(hwnd, out var frameError))
        {
            if (TryRollbackStyle(hwnd, originalStyle, appliedStyle)) _leases.Remove(hwnd);
            return Result(
                _leases.ContainsKey(hwnd) ? GlassDeskStatus.RestoreConflict : GlassDeskStatus.FrameRefreshFailed,
                _leases.ContainsKey(hwnd)
                    ? $"刷新窗口边框失败且样式回滚失败，已保留恢复租约：{NativeMethods.LastErrorMessage(frameError)}。"
                    : $"刷新窗口边框失败：{NativeMethods.LastErrorMessage(frameError)}。",
                frameError);
        }

        return Result(GlassDeskStatus.Applied, $"已将 {candidate.ProcessName} 设置为 {opacityPercent}%。");
    }

    public GlassDeskResult Restore(nint hwnd, bool allowUnownedLayered = false)
    {
        if (!_leases.TryGetValue(hwnd, out var lease))
        {
            if (allowUnownedLayered)
            {
                return ResetUnownedLayered(hwnd);
            }
            return Result(GlassDeskStatus.NoOp, "目标窗口没有 GlassDesk 修改记录。");
        }

        if (!NativeMethods.IsWindow(hwnd))
        {
            _leases.Remove(hwnd);
            return Result(GlassDeskStatus.TargetGone, "目标窗口已关闭，已释放租约。");
        }

        if (!_resolver.StillMatches(lease))
        {
            _leases.Remove(hwnd);
            return Result(GlassDeskStatus.TargetChanged, "目标窗口身份已变化，未恢复旧样式。");
        }

        if (!TryReadExStyle(hwnd, out var currentStyle, out var readError))
        {
            return Result(GlassDeskStatus.ExStyleReadFailed, $"读取当前窗口样式失败：{NativeMethods.LastErrorMessage(readError)}。", readError);
        }

        if (currentStyle != lease.AppliedExStyle)
        {
            if (!lease.OwnsLayeredStyle && (currentStyle & NativeMethods.WS_EX_LAYERED) != 0)
            {
                return ResetUnownedLayered(hwnd, lease);
            }
            return Result(GlassDeskStatus.RestoreConflict, "目标应用已修改窗口样式，未覆盖它的变化。");
        }

        if (!lease.OwnsLayeredStyle)
        {
            return ResetUnownedLayered(hwnd, lease);
        }

        // Win32 exposes no reliable getter for a layered window's alpha. We can
        // therefore verify the style lease, but cannot prove that another
        // process did not change alpha while keeping the same style bit.
        if (!TryWriteExStyle(hwnd, lease.OriginalExStyle, out var writeError))
        {
            return ClassifyWriteFailure(writeError);
        }

        if (!RefreshFrame(hwnd, out var frameError))
        {
            return Result(GlassDeskStatus.FrameRefreshFailed, $"恢复后刷新窗口失败：{NativeMethods.LastErrorMessage(frameError)}。", frameError);
        }

        _leases.Remove(hwnd);
        return Result(GlassDeskStatus.Restored, "已恢复窗口原始样式。");
    }

    public IReadOnlyList<GlassDeskResult> RestoreAll()
    {
        var results = new List<GlassDeskResult>();
        foreach (var hwnd in _leases.Keys.ToArray())
        {
            results.Add(Restore(hwnd));
        }

        return results;
    }

    public byte? GetAppliedOpacity(nint hwnd) =>
        _leases.TryGetValue(hwnd, out var lease) ? lease.AppliedAlpha : null;

    private static bool SetAlpha(nint hwnd, byte opacityPercent, out int error)
    {
        var alpha = (byte)Math.Round(opacityPercent * 255 / 100d);
        var success = NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    private static bool RefreshFrame(nint hwnd, out int error)
    {
        var success = NativeMethods.SetWindowPos(
            hwnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    private static bool TryReadExStyle(nint hwnd, out uint style, out int error)
    {
        var value = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        error = Marshal.GetLastWin32Error();
        style = unchecked((uint)value.ToInt64());
        return value != nint.Zero || error == 0;
    }

    private static bool TryWriteExStyle(nint hwnd, uint style, out int error)
    {
        var previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, unchecked((nint)(long)style));
        error = Marshal.GetLastWin32Error();
        return previous != nint.Zero || error == 0;
    }

    private static bool TryRollbackStyle(nint hwnd, uint originalStyle, uint appliedStyle)
    {
        if (!TryReadExStyle(hwnd, out var currentStyle, out _)) return false;
        if (currentStyle != appliedStyle) return false;
        if (!TryWriteExStyle(hwnd, originalStyle, out _)) return false;
        return RefreshFrame(hwnd, out _);
    }

    private static GlassDeskResult ClassifyWriteFailure(int error)
    {
        var status = error == NativeMethods.ERROR_ACCESS_DENIED
            ? GlassDeskStatus.PermissionDenied
            : GlassDeskStatus.ExStyleWriteFailed;
        return Result(status, $"写入窗口样式失败：{NativeMethods.LastErrorMessage(error)}。", error);
    }

    private static GlassDeskResult Result(GlassDeskStatus status, string message, int error = 0) =>
        new(status, message, error);

    private GlassDeskResult ResetUnownedLayered(nint hwnd, WindowLease? lease = null)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            if (lease is not null) _leases.Remove(hwnd);
            return Result(GlassDeskStatus.TargetGone, "目标窗口已关闭。");
        }

        if (lease is not null && !_resolver.StillMatches(lease))
        {
            _leases.Remove(hwnd);
            return Result(GlassDeskStatus.TargetChanged, "目标窗口身份已变化，未重置未知的原生 layered 状态。");
        }

        if (!TryReadExStyle(hwnd, out var currentStyle, out var readError))
        {
            return Result(GlassDeskStatus.ExStyleReadFailed, $"读取已有 layered window 失败：{NativeMethods.LastErrorMessage(readError)}。", readError);
        }

        if ((currentStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            if (lease is not null) _leases.Remove(hwnd);
            return Result(GlassDeskStatus.RestoreConflict, "目标窗口已不再是 layered window，未继续修改。");
        }

        if (!SetAlpha(hwnd, 100, out var alphaError))
        {
            return Result(GlassDeskStatus.LayerAttributesFailed, $"将已有 layered window 恢复为 100% 失败：{NativeMethods.LastErrorMessage(alphaError)}。", alphaError);
        }

        if (!RefreshFrame(hwnd, out var frameError))
        {
            return Result(GlassDeskStatus.FrameRefreshFailed, $"恢复已有 layered window 后刷新失败：{NativeMethods.LastErrorMessage(frameError)}。", frameError);
        }

        if (lease is not null) _leases.Remove(hwnd);
        return Result(GlassDeskStatus.Restored, "已将透明度设为 100%，保留目标窗口原有的 layered 样式。");
    }
}
