using System.Windows;
using GlassDesk.Models;
using GlassDesk.Services;

namespace GlassDesk.UI;

public partial class SettingsWindow : Window
{
    private readonly GlassDeskController _controller;

    public SettingsWindow(GlassDeskController controller)
    {
        InitializeComponent();
        _controller = controller;
        DefaultOpacityBox.Text = controller.Settings.DefaultOpacityPercent.ToString();
        StepBox.Text = controller.Settings.OpacityStepPercent.ToString();
        IncreaseHotkeyBox.Text = controller.Settings.Hotkeys.Increase;
        DecreaseHotkeyBox.Text = controller.Settings.Hotkeys.Decrease;
        ResetHotkeyBox.Text = controller.Settings.Hotkeys.Reset;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DefaultOpacityBox.Text, out var defaultOpacity) || defaultOpacity is < 1 or > 100)
        {
            System.Windows.MessageBox.Show("默认透明度必须是 1 到 100。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(StepBox.Text, out var step) || step is < 1 or > 99)
        {
            System.Windows.MessageBox.Show("步长必须是 1 到 99。", "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hotkeys = new HotkeySettings(IncreaseHotkeyBox.Text, DecreaseHotkeyBox.Text, ResetHotkeyBox.Text);
        var result = _controller.UpdateHotkeys(hotkeys);
        if (!result.IsSuccess)
        {
            System.Windows.MessageBox.Show(result.Message, "快捷键未更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _controller.Settings.DefaultOpacityPercent = defaultOpacity;
        _controller.Settings.OpacityStepPercent = step;
        _controller.SaveSettings();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
