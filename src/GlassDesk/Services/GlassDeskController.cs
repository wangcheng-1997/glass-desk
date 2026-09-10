using System.Windows.Threading;
using GlassDesk.Models;

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
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;
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

    private GlassDeskResult Publish(GlassDeskResult result)
    {
        StatusChanged?.Invoke(this, result.Message);
        return result;
    }

    private void PublishMessage(string message) => StatusChanged?.Invoke(this, message);
}
