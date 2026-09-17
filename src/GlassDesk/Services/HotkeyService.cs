using System.Windows.Interop;
using System.Runtime.InteropServices;
using GlassDesk.Models;
using GlassDesk.Native;

namespace GlassDesk.Services;

public enum GlassDeskHotkey
{
    Increase,
    Decrease,
    Reset,
    Hide,
    Restore,
    OpenTray
}

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, GlassDeskHotkey> _registeredIds = new();
    private readonly Dictionary<GlassDeskHotkey, HotkeyDefinition> _definitions = new();

    public event EventHandler<GlassDeskHotkey>? Pressed;
    public nint MessageWindowHandle => _source.Handle;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("GlassDesk.Hotkeys")
        {
            Width = 1,
            Height = 1,
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00000080 | 0x00000020,
            UsesPerPixelOpacity = true
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public GlassDeskResult Register(HotkeySettings settings)
    {
        var parsed = new Dictionary<GlassDeskHotkey, HotkeyDefinition>();
        if (!TryParse(settings.Increase, out var increase, out var increaseError))
            return new(GlassDeskStatus.ConfigInvalid, $"增加透明度快捷键无效：{increaseError}");
        if (!TryParse(settings.Decrease, out var decrease, out var decreaseError))
            return new(GlassDeskStatus.ConfigInvalid, $"降低透明度快捷键无效：{decreaseError}");
        if (!TryParse(settings.Reset, out var reset, out var resetError))
            return new(GlassDeskStatus.ConfigInvalid, $"恢复透明度快捷键无效：{resetError}");
        if (!TryParse(settings.Hide, out var hide, out var hideError))
            return new(GlassDeskStatus.ConfigInvalid, $"隐藏窗口快捷键无效：{hideError}");
        if (!TryParse(settings.Restore, out var restore, out var restoreError))
            return new(GlassDeskStatus.ConfigInvalid, $"恢复隐藏窗口快捷键无效：{restoreError}");
        if (!TryParse(settings.OpenTray, out var openTray, out var openTrayError))
            return new(GlassDeskStatus.ConfigInvalid, $"打开托盘快捷键无效：{openTrayError}");

        parsed[GlassDeskHotkey.Increase] = increase;
        parsed[GlassDeskHotkey.Decrease] = decrease;
        parsed[GlassDeskHotkey.Reset] = reset;
        parsed[GlassDeskHotkey.Hide] = hide;
        parsed[GlassDeskHotkey.Restore] = restore;
        parsed[GlassDeskHotkey.OpenTray] = openTray;

        var oldDefinitions = _definitions.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!UnregisterAll())
        {
            return new(GlassDeskStatus.HotkeyRegistrationFailed, "旧快捷键注销失败，未切换快捷键配置。");
        }
        foreach (var pair in parsed)
        {
            var id = (int)pair.Key + 1;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, pair.Value.Modifiers | NativeMethods.MOD_NOREPEAT, pair.Value.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                UnregisterAll();
                var restored = RestoreDefinitions(oldDefinitions);
                if (!restored)
                {
                    return new(GlassDeskStatus.HotkeyRegistrationFailed,
                        $"新快捷键冲突，且旧快捷键未能全部恢复：{NativeMethods.LastErrorMessage(error)}。请重新启动 GlassDesk。", error);
                }
                var message = error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "快捷键已被其他程序占用。"
                    : $"快捷键注册失败：{NativeMethods.LastErrorMessage(error)}。";
                return new(GlassDeskStatus.HotkeyConflict, message, error);
            }

            _registeredIds[id] = pair.Key;
        }

        _definitions.Clear();
        foreach (var pair in parsed) _definitions[pair.Key] = pair.Value;
        return new(GlassDeskStatus.Applied, "快捷键已注册。");
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private bool RestoreDefinitions(Dictionary<GlassDeskHotkey, HotkeyDefinition> oldDefinitions)
    {
        var restoredAll = true;
        var restoredDefinitions = new Dictionary<GlassDeskHotkey, HotkeyDefinition>();
        foreach (var pair in oldDefinitions)
        {
            var id = (int)pair.Key + 1;
            if (NativeMethods.RegisterHotKey(_source.Handle, id, pair.Value.Modifiers | NativeMethods.MOD_NOREPEAT, pair.Value.VirtualKey))
            {
                _registeredIds[id] = pair.Key;
                restoredDefinitions[pair.Key] = pair.Value;
            }
            else
            {
                restoredAll = false;
            }
        }

        _definitions.Clear();
        foreach (var pair in restoredDefinitions) _definitions[pair.Key] = pair.Value;
        return restoredAll;
    }

    private bool UnregisterAll()
    {
        var allUnregistered = true;
        foreach (var id in _registeredIds.Keys.ToArray())
        {
            if (NativeMethods.UnregisterHotKey(_source.Handle, id))
            {
                _registeredIds.Remove(id);
            }
            else
            {
                allUnregistered = false;
            }
        }

        return allUnregistered;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registeredIds.TryGetValue(wParam.ToInt32(), out var hotkey))
        {
            Pressed?.Invoke(this, hotkey);
            handled = true;
        }

        return nint.Zero;
    }

    private static bool TryParse(string text, out HotkeyDefinition definition, out string error)
    {
        definition = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "不能为空";
            return false;
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    error = "不允许使用 Win 修饰键";
                    return false;
                case "up": key = 0x26; break;
                case "down": key = 0x28; break;
                case "left": key = 0x25; break;
                case "right": key = 0x27; break;
                case "space": key = 0x20; break;
                case "0": key = 0x30; break;
                case "1": key = 0x31; break;
                case "2": key = 0x32; break;
                case "3": key = 0x33; break;
                case "4": key = 0x34; break;
                case "5": key = 0x35; break;
                case "6": key = 0x36; break;
                case "7": key = 0x37; break;
                case "8": key = 0x38; break;
                case "9": key = 0x39; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        key = char.ToUpperInvariant(part[0]);
                    }
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out var fn) && fn is >= 1 and <= 24)
                    {
                        key = (uint)(0x70 + fn - 1);
                    }
                    else
                    {
                        error = $"未知按键：{part}";
                        return false;
                    }
                    break;
            }
        }

        if (key is null)
        {
            error = "必须包含一个主按键";
            return false;
        }

        if (key == 0x7B)
        {
            error = "不允许占用 F12";
            return false;
        }

        definition = new HotkeyDefinition(modifiers, key.Value);
        return true;
    }

    private readonly record struct HotkeyDefinition(uint Modifiers, uint VirtualKey);
}
