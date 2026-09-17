using System.Windows;
using GlassDesk.Models;
using GlassDesk.Services;

namespace GlassDesk.UI;

public partial class ProcessWindow : Window
{
    private readonly GlassDeskController _controller;
    private readonly HashSet<WindowTargetIdentity> _pendingTargets = new(WindowTargetIdentityComparer.Instance);

    public ProcessWindow(GlassDeskController controller)
    {
        InitializeComponent();
        _controller = controller;
        HideHotkeyBox.Text = controller.Settings.Hotkeys.Hide;
        RestoreHotkeyBox.Text = controller.Settings.Hotkeys.Restore;
        OpenProcessHotkeyBox.Text = controller.Settings.Hotkeys.OpenTray;
        foreach (var target in controller.Settings.HiddenTargets) _pendingTargets.Add(target);
        if (controller.Settings.HiddenTarget is not null) _pendingTargets.Add(controller.Settings.HiddenTarget);
        LoadProcesses();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadProcesses();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelectedTargets();
        var hotkeys = _controller.Settings.Hotkeys with
        {
            Hide = HideHotkeyBox.Text,
            Restore = RestoreHotkeyBox.Text,
            OpenTray = OpenProcessHotkeyBox.Text
        };
        var hotkeyResult = _controller.UpdateHotkeys(hotkeys);
        if (!hotkeyResult.IsSuccess)
        {
            System.Windows.MessageBox.Show(hotkeyResult.Message, "快捷键未更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _controller.SetHiddenTargets(_pendingTargets);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadProcesses()
    {
        CaptureSelectedTargets();
        var options = _controller.EnumerateSelectableWindows()
            .Select(candidate => new ProcessOption(candidate, _pendingTargets.Contains(ToIdentity(candidate))))
            .ToList();
        var visibleIdentities = options.Select(option => option.Identity).ToHashSet(WindowTargetIdentityComparer.Instance);
        options.AddRange(_pendingTargets
            .Where(identity => !visibleIdentities.Contains(identity))
            .Select(identity => new ProcessOption(identity)));
        ProcessList.ItemsSource = options;
    }

    private void CaptureSelectedTargets()
    {
        foreach (var option in ProcessList.Items.OfType<ProcessOption>())
        {
            if (option.IsSelected) _pendingTargets.Add(option.Identity);
            else _pendingTargets.Remove(option.Identity);
        }
    }

    private static WindowTargetIdentity ToIdentity(WindowCandidate candidate) =>
        new(candidate.ProcessPath, candidate.WindowClassName, candidate.Title);

    private sealed class ProcessOption
    {
        public ProcessOption(WindowCandidate candidate, bool isSelected)
        {
            Identity = ToIdentity(candidate);
            Title = string.IsNullOrWhiteSpace(candidate.Title) ? "(无标题窗口)" : candidate.Title;
            ProcessName = $"进程：{candidate.ProcessName}";
            WindowClassName = $"类名：{candidate.WindowClassName}";
            CanPersist = !string.IsNullOrWhiteSpace(candidate.ProcessPath)
                && !string.IsNullOrWhiteSpace(candidate.WindowClassName);
            Availability = "当前可见";
            IsSelected = isSelected;
        }

        public ProcessOption(WindowTargetIdentity identity)
        {
            Identity = identity;
            Title = string.IsNullOrWhiteSpace(identity.Title) ? "(无标题窗口)" : identity.Title;
            ProcessName = $"目标路径：{identity.ProcessPath}";
            WindowClassName = $"类名：{identity.WindowClassName}";
            CanPersist = !string.IsNullOrWhiteSpace(identity.ProcessPath)
                && !string.IsNullOrWhiteSpace(identity.WindowClassName);
            Availability = "当前未发现，仍会保留配置";
            IsSelected = true;
        }

        public WindowTargetIdentity Identity { get; }
        public string Title { get; }
        public string ProcessName { get; }
        public string WindowClassName { get; }
        public bool CanPersist { get; }
        public string Availability { get; }
        public bool IsSelected { get; set; }
    }
}
