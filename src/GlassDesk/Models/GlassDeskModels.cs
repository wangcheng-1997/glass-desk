namespace GlassDesk.Models;

public enum GlassDeskStatus
{
    Applied,
    Restored,
    NoOp,
    NotEligible,
    TargetGone,
    TargetChanged,
    TargetIdentityUnavailable,
    PermissionDenied,
    PermissionIndeterminate,
    AlreadyLayeredUnsupported,
    ExStyleReadFailed,
    ExStyleWriteFailed,
    LayerAttributesFailed,
    FrameRefreshFailed,
    RestoreConflict,
    HotkeyConflict,
    HotkeyRegistrationFailed,
    MirrorUnavailable,
    Unsupported,
    ConfigInvalid
}

public sealed record GlassDeskResult(GlassDeskStatus Status, string Message, int NativeError = 0)
{
    public bool IsSuccess => Status is GlassDeskStatus.Applied or GlassDeskStatus.Restored or GlassDeskStatus.NoOp;
}

public sealed record WindowCandidate(
    nint Hwnd,
    uint ProcessId,
    long ProcessStartFileTimeUtc,
    string ProcessPath,
    string ProcessName,
    string WindowClassName,
    string Title,
    int IntegrityLevel,
    bool IsVisible,
    bool IsMinimized,
    bool IsProtectedContent,
    int Left,
    int Top,
    int Width,
    int Height);

public sealed record WindowTargetIdentity(
    string ProcessPath,
    string WindowClassName,
    string Title);

public sealed class WindowTargetIdentityComparer : IEqualityComparer<WindowTargetIdentity>
{
    public static WindowTargetIdentityComparer Instance { get; } = new();

    public bool Equals(WindowTargetIdentity? x, WindowTargetIdentity? y) =>
        ReferenceEquals(x, y)
        || (x is not null && y is not null
            && string.Equals(x.ProcessPath, y.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.WindowClassName, y.WindowClassName, StringComparison.Ordinal)
            && string.Equals(x.Title, y.Title, StringComparison.Ordinal));

    public int GetHashCode(WindowTargetIdentity obj)
    {
        var hash = new HashCode();
        hash.Add(obj.ProcessPath, StringComparer.OrdinalIgnoreCase);
        hash.Add(obj.WindowClassName, StringComparer.Ordinal);
        hash.Add(obj.Title, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class WindowLease
{
    public required nint Hwnd { get; init; }
    public required uint ProcessId { get; init; }
    public required long ProcessStartFileTimeUtc { get; init; }
    public required uint OriginalExStyle { get; init; }
    public required uint AppliedExStyle { get; init; }
    public required bool OwnsLayeredStyle { get; init; }
    public required byte AppliedAlpha { get; set; }
    public required string ProcessPath { get; init; }
    public required string WindowClassName { get; init; }
}

public sealed class HiddenWindowLease
{
    public required nint Hwnd { get; init; }
    public required uint ProcessId { get; init; }
    public required long ProcessStartFileTimeUtc { get; init; }
    public required string ProcessPath { get; init; }
    public required string WindowClassName { get; init; }
}

public sealed record AppRule(
    string CanonicalExePath,
    string? WindowClassName,
    byte Alpha,
    bool AutoApplyOnNewWindow = false);

public sealed record HotkeySettings(
    string Increase = "Ctrl+Alt+Up",
    string Decrease = "Ctrl+Alt+Down",
    string Reset = "Ctrl+Alt+0",
    string Hide = "Alt+H",
    string Restore = "Alt+R",
    string OpenTray = "Alt+X");

public sealed class GlassDeskSettings
{
    public int DefaultOpacityPercent { get; set; } = 70;
    public int OpacityStepPercent { get; set; } = 10;
    public List<AppRule> Rules { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public List<string> ExcludedProcessPaths { get; set; } = new();
    public WindowTargetIdentity? HiddenTarget { get; set; }
    public List<WindowTargetIdentity> HiddenTargets { get; set; } = new();
}
