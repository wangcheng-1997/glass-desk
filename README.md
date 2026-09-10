# GlassDesk

GlassDesk 是一个面向 Windows 11 的后台窗口透明化工具。它以管理员权限运行，以便处理管理员权限启动的应用；首次启动会出现一次 UAC 确认。它默认只修改用户明确操作的前台窗口，不启用鼠标穿透、不自动注入目标进程，也不承诺独占全屏、DRM 或安全桌面可用。

## 当前实现

- WPF + .NET 8 托盘应用。
- GlassDesk 专用玻璃窗口图标，支持托盘与 EXE 图标。
- `WS_EX_LAYERED` + `SetLayeredWindowAttributes` 直接透明。
- 管理员权限运行，解决普通权限下对高完整性窗口返回 `ERROR_ACCESS_DENIED` 的问题。
- 20% 到 100% 的透明度档位，默认每次调整 10%。
- 可配置全局快捷键，默认是 `Ctrl+Alt+Up`、`Ctrl+Alt+Down`、`Ctrl+Alt+0`。
- 按可执行文件路径和窗口类名保存规则，不持久化 HWND/PID/样式租约。
- 检测权限、受保护内容、已有 layered window、窗口重建和恢复冲突。
- 只读镜像与应用适配器接口已预留；镜像当前会明确返回“尚未启用”，不会伪装成透明成功。

## 构建与运行

```powershell
dotnet restore .\src\GlassDesk\GlassDesk.csproj
dotnet build .\src\GlassDesk\GlassDesk.csproj --configuration Release
Start-Process .\src\GlassDesk\bin\Release\net8.0-windows10.0.22621.0\GlassDesk.exe
```

快速启动/退出 smoke test：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

启动后应用只显示在系统托盘。双击托盘图标打开设置；快捷键作用于当前前台窗口。设置文件位于 `%LocalAppData%\GlassDesk\settings.json`。

## 已知边界

- 已经由目标程序管理的 layered window 在自动规则中默认拒绝；用户明确按快捷键或托盘操作时允许调整 alpha。由于无法可靠读取原始 alpha，Reset/退出只恢复到 100% 并保留 layered 样式。
- `SetWindowLongPtr` 成功不等于画面一定生效；目标窗口重建、GPU surface、独占全屏和受保护内容可能仍不可用。
- Windows Graphics Capture 镜像模式尚未发布。它必须先验证源窗口隐藏、捕获帧持续性、重复合成和退出恢复，再接入产品。
- 目前没有自动应用规则编辑器；规则会在用户首次通过快捷键或托盘操作调整窗口后自动保存。

## 手工验收建议

1. 在记事本、WPF 应用、浏览器和 Electron 应用上测试增加/降低/恢复快捷键。
2. 检查鼠标仍能操作透明窗口，确认没有意外添加 `WS_EX_TRANSPARENT`。
3. 以管理员身份启动一个目标应用，确认权限失败时只提示失败，不自动提权。
4. 关闭或重建目标窗口，确认旧 HWND 租约被丢弃，不会误操作复用后的句柄。
5. 手工修改目标窗口样式后执行恢复，确认出现恢复冲突提示而不是覆盖目标应用的变化。
