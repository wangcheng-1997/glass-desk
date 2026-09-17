using System.Windows.Threading;
using GlassDesk.Models;
using GlassDesk.Native;

namespace GlassDesk.Services;

public sealed class GlassDeskController : IDisposable
{
    private readonly WindowTargetResolver _resolver;
    private readonly DirectTransparencyBackend _direct;
    private readonly CaptureMirrorBackend _mirror;
    private readonly CompatibilityAdapterHost _adapters;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _monitorTimer;
    private readonly HotkeyService _hotkeys;
    private readonly DiagnosticLog _diagnosticLog;
    private GlassDeskSettings _settings;
    private readonly Stack<List<HiddenWindowLease>> _hiddenWindowBatches = new();
    private static readonly HashSet<string> NonDesktopProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemSettings.exe",
        "ApplicationFrameHost.exe",
        "TextInputHost.exe"
    };
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? OpenProcessRequested;
    public GlassDeskSettings Settings => _settings;
    public IReadOnlyCollection<WindowLease> ActiveLeases => _direct.ActiveLeases;

    public GlassDeskController()
    {
        _resolver = new WindowTargetResolver();
        _direct = new DirectTransparencyBackend(_resolver);
        _mirror = new CaptureMirrorBackend();
        _adapters = new CompatibilityAdapterHost();
        _settingsStore = new SettingsStore();
        _diagnosticLog = new DiagnosticLog();
        _settings = _settingsStore.Load();
        _settings.HiddenTargets ??= new List<WindowTargetIdentity>();
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkeyPressed;
        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _monitorTimer.Tick += (_, _) => ReconcileActiveWindows();
        _monitorTimer.Start();

        var hotkeyResult = _hotkeys.Register(_settings.Hotkeys);
        PublishMessage(hotkeyResult.Message);
    }

    public WindowCandidate? GetForegroundTarget() => _resolver.GetForegroundWindow();

    public IReadOnlyList<WindowCandidate> EnumerateWindows() => _resolver.EnumerateTopLevelWindows();

    public IReadOnlyList<WindowCandidate> EnumerateSelectableWindows() =>
        _resolver.EnumerateDesktopWindows()
            .Where(candidate => candidate.ProcessId != (uint)Environment.ProcessId
                && !NonDesktopProcessNames.Contains(candidate.ProcessName))
            .ToArray();

    public GlassDeskResult SetHiddenTarget(WindowCandidate target)
    {
        if (!HasStableIdentity(target))
        {
            return Publish(new GlassDeskResult(GlassDeskStatus.TargetIdentityUnavailable, "无法确认窗口身份，未设置隐藏目标。"));
        }

        SetHiddenTargetsCore(new[] { new WindowTargetIdentity(target.ProcessPath, target.WindowClassName, target.Title) });
        return Publish(new GlassDeskResult(GlassDeskStatus.Applied, $"已将 {target.ProcessName} 设为隐藏目标。"));
    }

    public GlassDeskResult SetHiddenTargets(IEnumerable<WindowTargetIdentity> targets)
    {
        var count = SetHiddenTargetsCore(targets);
        return Publish(new GlassDeskResult(
            GlassDeskStatus.Applied,
            count == 0 ? "未指定隐藏目标；Alt+H 将隐藏当前前台窗口。" : $"已保存 {count} 个隐藏目标。"));
    }

    public GlassDeskResult ClearHiddenTarget()
    {
        if (_settings.HiddenTarget is null && _settings.HiddenTargets.Count == 0)
        {
            return Publish(new GlassDeskResult(GlassDeskStatus.NoOp, "当前没有指定隐藏目标。"));
        }

        _settings.HiddenTarget = null;
        _settings.HiddenTargets.Clear();
        SaveSettings();
        return Publish(new GlassDeskResult(GlassDeskStatus.Applied, "已清除指定隐藏目标。"));
    }

    public GlassDeskResult ApplyToForeground()
    {
        var target = _resolver.GetForegroundWindow();
        return target is null
            ? Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可操作的前台窗口。"))
            : Apply(target, ResolveOpacity(target, _settings.DefaultOpacityPercent), allowExistingLayered: true);
    }

    public GlassDeskResult IncreaseOpacity()
    {
        var target = _resolver.GetForegroundWindow();
        if (target is null) return Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可操作的前台窗口。"));
        var current = _direct.GetAppliedOpacity(target.Hwnd) ?? ResolveOpacity(target, _settings.DefaultOpacityPercent);
        return Apply(target, (byte)Math.Min(100, current + Math.Max(1, _settings.OpacityStepPercent)), allowExistingLayered: true);
    }

    public GlassDeskResult DecreaseOpacity()
    {
        var target = _resolver.GetForegroundWindow();
        if (target is null) return Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可操作的前台窗口。"));
        var current = _direct.GetAppliedOpacity(target.Hwnd) ?? ResolveOpacity(target, _settings.DefaultOpacityPercent);
        return Apply(target, (byte)Math.Max(1, current - Math.Max(1, _settings.OpacityStepPercent)), allowExistingLayered: true);
    }

    public GlassDeskResult ResetForeground()
    {
        var target = _resolver.GetForegroundWindow();
        if (target is null) return Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可操作的前台窗口。"));
        return Publish(_direct.Restore(target.Hwnd, allowUnownedLayered: true));
    }

    public GlassDeskResult HideForegroundWindow()
    {
        var configuredTargets = GetConfiguredHiddenTargets();
        if (configuredTargets.Count == 0)
        {
            var foreground = _resolver.GetForegroundWindow();
            return foreground is null
                ? Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可隐藏的前台窗口。"))
                : HideTargets(new[] { foreground }, configured: false);
        }

        var targets = new List<WindowCandidate>();
        var unresolved = 0;
        foreach (var identity in configuredTargets)
        {
            var target = _resolver.Find(identity);
            if (target is null) unresolved++;
            else targets.Add(target);
        }

        return HideTargets(targets, configured: true, unresolved);
    }

    public GlassDeskResult RestoreLastHiddenWindow()
    {
        var hiddenWindows = TakeLastHiddenWindows();
        if (hiddenWindows.Count == 0) return Publish(new GlassDeskResult(GlassDeskStatus.TargetGone, "没有可恢复的隐藏窗口。"));

        var restored = RestoreHiddenWindows(hiddenWindows, allowForeground: true, out var skipped, out var failed, out var remaining);
        if (remaining.Count > 0) _hiddenWindowBatches.Push(remaining);
        if (restored > 0)
        {
            var message = $"已恢复 {restored} 个最近隐藏窗口";
            if (skipped > 0 || failed > 0) message += $"；跳过 {skipped} 个身份已变化或已关闭窗口，{failed} 个恢复失败";
            return Publish(new GlassDeskResult(GlassDeskStatus.Restored, message + "。"));
        }

        return Publish(new GlassDeskResult(
            failed > 0 ? GlassDeskStatus.Unsupported : GlassDeskStatus.TargetChanged,
            $"最近隐藏窗口未恢复；跳过 {skipped} 个身份已变化或已关闭窗口，{failed} 个恢复失败。"));
    }

    public GlassDeskResult StartMirrorForForeground()
    {
        var target = _resolver.GetForegroundWindow();
        return target is null
            ? Publish(new(GlassDeskStatus.TargetGone, "没有可镜像的前台窗口。"))
            : Publish(_mirror.StartReadOnlyMirror(target));
    }

    public GlassDeskResult UpdateHotkeys(HotkeySettings hotkeys)
    {
        var result = _hotkeys.Register(hotkeys);
        if (!result.IsSuccess) return Publish(result);
        _settings.Hotkeys = hotkeys;
        SaveSettings();
        return Publish(result);
    }

    public void SaveSettings() => _settingsStore.Save(_settings);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorTimer.Stop();
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Dispose();
        RestoreHiddenWindowsOnExit();
        var restoreResults = _direct.RestoreAll();
        foreach (var result in restoreResults.Where(result => !result.IsSuccess))
        {
            _diagnosticLog.Write($"退出恢复失败 [{result.Status}] {result.Message}");
        }
    }

    private GlassDeskResult Apply(WindowCandidate target, byte opacity, bool allowExistingLayered)
    {
        if (IsExcluded(target))
        {
            return Publish(new GlassDeskResult(GlassDeskStatus.NotEligible, $"已排除 {target.ProcessName}。"));
        }

        var result = _direct.Apply(target.Hwnd, opacity, allowExistingLayered);
        if (result.Status == GlassDeskStatus.AlreadyLayeredUnsupported)
        {
            var adapterResult = _adapters.TryApply(target, opacity);
            if (adapterResult.Status != GlassDeskStatus.Unsupported) result = adapterResult;
        }

        if (result.Status == GlassDeskStatus.Applied)
        {
            UpsertRule(target, opacity);
            SaveSettings();
        }

        return Publish(result);
    }

    private void OnHotkeyPressed(object? sender, GlassDeskHotkey hotkey)
    {
        switch (hotkey)
        {
            case GlassDeskHotkey.Increase: IncreaseOpacity(); break;
            case GlassDeskHotkey.Decrease: DecreaseOpacity(); break;
            case GlassDeskHotkey.Reset: ResetForeground(); break;
            case GlassDeskHotkey.Hide: HideForegroundWindow(); break;
            case GlassDeskHotkey.Restore: RestoreLastHiddenWindow(); break;
            case GlassDeskHotkey.OpenTray: OpenProcessRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private void ReconcileActiveWindows()
    {
        foreach (var lease in _direct.ActiveLeases.ToArray())
        {
            if (!_resolver.StillMatches(lease))
            {
                _direct.Restore(lease.Hwnd);
            }
        }

        var foreground = _resolver.GetForegroundWindow();
        if (foreground is null) return;
        var rule = FindRule(foreground);
        if (rule?.AutoApplyOnNewWindow == true && _direct.GetAppliedOpacity(foreground.Hwnd) is null)
        {
            Apply(foreground, rule.Alpha, allowExistingLayered: false);
        }
    }

    private byte ResolveOpacity(WindowCandidate target, int fallback)
    {
        var rule = FindRule(target);
        return (byte)Math.Clamp(rule?.Alpha ?? fallback, 1, 100);
    }

    private AppRule? FindRule(WindowCandidate target) =>
        _settings.Rules.FirstOrDefault(rule =>
            string.Equals(rule.CanonicalExePath, target.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(rule.WindowClassName)
                || string.Equals(rule.WindowClassName, target.WindowClassName, StringComparison.Ordinal)));

    private void UpsertRule(WindowCandidate target, byte opacity)
    {
        var index = _settings.Rules.FindIndex(rule =>
            string.Equals(rule.CanonicalExePath, target.ProcessPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rule.WindowClassName, target.WindowClassName, StringComparison.Ordinal));
        var replacement = new AppRule(target.ProcessPath, target.WindowClassName, opacity, index >= 0 && _settings.Rules[index].AutoApplyOnNewWindow);
        if (index >= 0) _settings.Rules[index] = replacement;
        else _settings.Rules.Add(replacement);
    }

    private bool IsExcluded(WindowCandidate target) =>
        _settings.ExcludedProcessPaths.Any(path => string.Equals(path, target.ProcessPath, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<WindowTargetIdentity> GetConfiguredHiddenTargets()
    {
        if (_settings.HiddenTargets.Count > 0)
        {
            return _settings.HiddenTargets.Distinct(WindowTargetIdentityComparer.Instance).ToArray();
        }
        return _settings.HiddenTarget is null ? Array.Empty<WindowTargetIdentity>() : new[] { _settings.HiddenTarget };
    }

    private int SetHiddenTargetsCore(IEnumerable<WindowTargetIdentity> targets)
    {
        var configured = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.ProcessPath) && !string.IsNullOrWhiteSpace(target.WindowClassName))
            .Distinct(WindowTargetIdentityComparer.Instance)
            .ToList();
        _settings.HiddenTargets = configured;
        _settings.HiddenTarget = configured.Count == 1 ? configured[0] : null;
        SaveSettings();
        return configured.Count;
    }

    private GlassDeskResult HideTargets(IReadOnlyList<WindowCandidate> targets, bool configured, int unresolved = 0)
    {
        var hiddenWindows = new List<HiddenWindowLease>();
        var skipped = unresolved;
        var failed = 0;
        foreach (var target in targets.DistinctBy(target => target.Hwnd))
        {
            if (target.ProcessId == (uint)Environment.ProcessId || target.Hwnd == _hotkeys.MessageWindowHandle || !HasStableIdentity(target))
            {
                skipped++;
                continue;
            }

            NativeMethods.ShowWindow(target.Hwnd, NativeMethods.SW_HIDE);
            if (!NativeMethods.IsWindow(target.Hwnd) || !_resolver.StillMatches(new HiddenWindowLease
                {
                    Hwnd = target.Hwnd,
                    ProcessId = target.ProcessId,
                    ProcessStartFileTimeUtc = target.ProcessStartFileTimeUtc,
                    ProcessPath = target.ProcessPath,
                    WindowClassName = target.WindowClassName
                }))
            {
                skipped++;
                continue;
            }
            if (NativeMethods.IsWindowVisible(target.Hwnd))
            {
                failed++;
                continue;
            }

            hiddenWindows.Add(new HiddenWindowLease
            {
                Hwnd = target.Hwnd,
                ProcessId = target.ProcessId,
                ProcessStartFileTimeUtc = target.ProcessStartFileTimeUtc,
                ProcessPath = target.ProcessPath,
                WindowClassName = target.WindowClassName
            });
        }

        if (hiddenWindows.Count == 0)
        {
            var message = configured
                ? $"未隐藏任何指定窗口；跳过 {skipped} 个未找到、匹配不唯一或身份不可确认目标，{failed} 个隐藏失败。未回退隐藏当前前台窗口。"
                : "没有可隐藏的前台窗口。";
            return Publish(new GlassDeskResult(failed > 0 ? GlassDeskStatus.Unsupported : GlassDeskStatus.TargetGone, message));
        }

        _hiddenWindowBatches.Push(hiddenWindows);
        var successMessage = $"已隐藏 {hiddenWindows.Count} 个窗口";
        if (skipped > 0 || failed > 0) successMessage += $"；跳过 {skipped} 个目标，{failed} 个隐藏失败";
        return Publish(new GlassDeskResult(GlassDeskStatus.Applied, successMessage + "。"));
    }

    private List<HiddenWindowLease> TakeLastHiddenWindows()
    {
        return _hiddenWindowBatches.Count == 0 ? new List<HiddenWindowLease>() : _hiddenWindowBatches.Pop();
    }

    private int RestoreHiddenWindows(
        IReadOnlyList<HiddenWindowLease> hiddenWindows,
        bool allowForeground,
        out int skipped,
        out int failed,
        out List<HiddenWindowLease> remaining)
    {
        var restored = 0;
        skipped = 0;
        failed = 0;
        remaining = new List<HiddenWindowLease>();
        var foregroundAttempted = false;
        foreach (var hidden in hiddenWindows)
        {
            if (hidden.Hwnd == _hotkeys.MessageWindowHandle || !NativeMethods.IsWindow(hidden.Hwnd) || !_resolver.StillMatches(hidden))
            {
                skipped++;
                continue;
            }

            NativeMethods.ShowWindow(hidden.Hwnd, NativeMethods.SW_RESTORE);
            if (!NativeMethods.IsWindow(hidden.Hwnd) || !NativeMethods.IsWindowVisible(hidden.Hwnd))
            {
                failed++;
                remaining.Add(hidden);
                continue;
            }

            restored++;
            if (allowForeground && !foregroundAttempted)
            {
                NativeMethods.SetForegroundWindow(hidden.Hwnd);
                foregroundAttempted = true;
            }
        }

        return restored;
    }

    private void RestoreHiddenWindowsOnExit()
    {
        var skipped = 0;
        var failed = 0;
        while (_hiddenWindowBatches.Count > 0)
        {
            RestoreHiddenWindows(TakeLastHiddenWindows(), allowForeground: false, out var batchSkipped, out var batchFailed, out _);
            skipped += batchSkipped;
            failed += batchFailed;
        }
        if (skipped > 0 || failed > 0)
        {
            _diagnosticLog.Write($"退出时隐藏窗口恢复不完整：跳过 {skipped} 个，恢复失败 {failed} 个。");
        }
    }

    private static bool HasStableIdentity(WindowCandidate target) =>
        target.ProcessStartFileTimeUtc != 0
        && !string.IsNullOrWhiteSpace(target.ProcessPath)
        && !string.IsNullOrWhiteSpace(target.WindowClassName);

    private GlassDeskResult Publish(GlassDeskResult result)
    {
        StatusChanged?.Invoke(this, result.Message);
        return result;
    }

    private void PublishMessage(string message) => StatusChanged?.Invoke(this, message);
}
