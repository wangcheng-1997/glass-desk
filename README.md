# GlassDesk

GlassDesk 是一个面向 Windows 11 的后台窗口透明化工具。它以管理员权限运行，以便处理管理员权限启动的应用；首次启动会出现一次 UAC 确认。它默认只修改用户明确操作的前台窗口，不启用鼠标穿透、不自动注入目标进程，也不承诺独占全屏、DRM 或安全桌面可用。

## 当前实现

- WPF + .NET 8 托盘应用。
- GlassDesk 专用玻璃窗口图标，支持托盘与 EXE 图标。
- `WS_EX_LAYERED` + `SetLayeredWindowAttributes` 直接透明。
- 管理员权限运行，解决普通权限下对高完整性窗口返回 `ERROR_ACCESS_DENIED` 的问题。
- 20% 到 100% 的透明度档位，默认每次调整 10%。
- 可配置全局快捷键，默认是 `Ctrl+Alt+Up`、`Ctrl+Alt+Down`、`Ctrl+Alt+0`。
- `Alt+H` 默认隐藏当前前台窗口，`Alt+R` 恢复上一批隐藏的窗口。设置中的“桌面窗口任务列表”可同时勾选多个目标；勾选后 `Alt+H` 会按每个目标的可执行文件路径、窗口类名和标题唯一查找并批量隐藏，找不到或匹配不唯一的目标会跳过，绝不会回退隐藏当前窗口。
- `Alt+X` 默认切换独立的 GlassDesk 进程隐藏界面（显示/收起）；透明度参数和透明度快捷键在独立的透明度设置中配置。
- 按可执行文件路径、窗口类名和标题保存隐藏目标，不持久化 HWND/PID/样式租约。
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

配置当前用户登录后自动启动（需要 UAC 确认一次）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1
```

该脚本创建名为 `GlassDesk AutoStart` 的计划任务，以最高权限启动 Release 版本。移除自启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1 -Remove
```

启动后应用只显示在系统托盘。按 `Alt+X` 显示或收起进程隐藏界面；右键托盘图标可进入进程隐藏或透明度设置。右键菜单也可将打开菜单前的前台窗口设为隐藏目标。未指定隐藏目标时，快捷键作用于当前前台窗口。设置文件位于 `%LocalAppData%\GlassDesk\settings.json`。

## 已知边界

- 已经由目标程序管理的 layered window 在自动规则中默认拒绝；用户明确按快捷键或托盘操作时允许调整 alpha。由于无法可靠读取原始 alpha，Reset/退出只恢复到 100% 并保留 layered 样式。
- `SetWindowLongPtr` 成功不等于画面一定生效；目标窗口重建、GPU surface、独占全屏和受保护内容可能仍不可用。
- Windows Graphics Capture 镜像模式尚未发布。它必须先验证源窗口隐藏、捕获帧持续性、重复合成和退出恢复，再接入产品。
- 目前没有自动应用规则编辑器；规则会在用户首次通过快捷键或托盘操作调整窗口后自动保存。
- 隐藏窗口按批次保留；`Alt+R` 会恢复上一批，并逐个检查 HWND、PID、进程启动时间、路径和窗口类名。每个租约只操作一次，最多将一个窗口置前。窗口关闭或身份变化时会安全跳过；退出时也会尝试恢复所有尚未恢复的批次。
- 隐藏目标只持久化可执行文件路径、窗口类名和标题，不持久化 HWND/PID。进程隐藏界面会列出当前桌面上可见的应用窗口（不含 GlassDesk、系统桌面、工具窗口，以及 SystemSettings、ApplicationFrameHost、TextInputHost 等系统宿主进程）；暂时不可见但已保存的目标会保留并标记，勾选仅在点击“保存并关闭”后写入配置，点击“取消”不会修改配置。

## 手工验收建议

1. 在记事本、WPF 应用、浏览器和 Electron 应用上测试增加/降低/恢复快捷键。
2. 检查鼠标仍能操作透明窗口，确认没有意外添加 `WS_EX_TRANSPARENT`。
3. 以管理员身份启动一个目标应用，确认权限失败时只提示失败，不自动提权。
4. 关闭或重建目标窗口，确认旧 HWND 租约被丢弃，不会误操作复用后的句柄。
5. 手工修改目标窗口样式后执行恢复，确认出现恢复冲突提示而不是覆盖目标应用的变化。
6. 在设置页的“桌面窗口任务列表”勾选两个窗口后保存，按 `Alt+H` 确认两者被隐藏、`Alt+R` 确认整批恢复；再打开两个同标题同类的目标窗口，确认 `Alt+H` 只跳过匹配不唯一的项且不隐藏当前窗口。
