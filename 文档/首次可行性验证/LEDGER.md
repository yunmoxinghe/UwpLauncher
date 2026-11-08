# UWP XAML Island 透明背景验证台账

> 任务：考据 DesktopFlyouts 的 UWP XAML Island 透明实现 → 本机最小工程实测 → 嵌入桌面后复测。
> 规则：每阶段完成立即落盘；结论只认本机实测，不以静态阅读代替验证。

## 阶段 0：环境探测（已完成）

- 日期：2026-11-08，Asia/Shanghai
- OS：Windows `10.0.26300.0`（Build 26300），DWM 合成已开启，交互式会话 `console / session 1`，虚拟屏幕 1280x720
- VS：Visual Studio Community 2026 18.11.12328.437，安装路径 `C:\Program Files\Microsoft Visual Studio\18\Community`
- Windows SDK：10.0.26100.0 与 10.0.28000.0（Include/Bin 均存在）
- .NET SDK：10.0.403
- 已确认 VS 自带 UWP 现代化工具链：`MSBuild\Microsoft\WindowsXaml\v18.0\8.21\Microsoft.Windows.UI.Xaml.CSharp.ModernNET.*`，SDK 风格工程通过 `<UseUwp>true</UseUwp>` 即可获得 UWP XAML 编译支持
- 关键验证：DesktopFlyouts 官方 UWP 样例 `samples/DesktopFlyouts.Uwp.Sample.TrayHost` 在本机 **编译成功且进程可常驻运行**（PowerShell 启动 6 秒后 `HasExited=False`）
  - 说明：本机可走 “.NET 10 + UseUwp + Microsoft.UI.Xaml(2.8.7) + CsWinRT/CsWin32” 的 UWP XAML Islands（非 WinUI3）路径
- 屏幕截图能力：`System.Drawing.CopyFromScreen` 成功（1280x720 PNG 约 96KB，非黑屏）

## 阶段 1：DesktopFlyouts 考据结论（已完成）

仓库：`https://github.com/0x5bfa/DesktopFlyouts`
被测提交：`6cdcef5538db19e044ac20ba6fb010f322ef2881`（2026-11-06，`git describe` = `v1.6.0-1-g6cdcef5`）

### 核心结论

DesktopFlyouts 的 UWP 透明背景 = 对 `Window.Current` 做 QI 拿到**未在公共 SDK 投影中暴露的 WinRT 接口 `IXamlSourceTransparency`**（内部实现即 `IWindowPrivate`），调用 `Set IsBackgroundTransparent = true`。
该接口有微软官方文档页（`Windows 10 17763+`）：https://learn.microsoft.com/en-us/windows/apps/api-reference/interface-members/ixamlsourcetransparency-isbackgroundtransparent

除此之外的配合项：
1. 宿主窗口 `WS_POPUP` + `WS_EX_NOREDIRECTIONBITMAP`（去掉 GDI 重定向表面），使宿主窗口无系统绘制背景；
2. `WS_EX_TOOLWINDOW | WS_EX_TOPMOST`（工具窗 + 置顶），用于“桌面浮层”定位；
3. `DesktopWindowXamlSource` 的 XAML 岛子窗口通过 QI `IDesktopWindowXamlSourceNative2` → `AttachToWindow` → `get_WindowHandle` 获取并手动 `SetWindowPos` 铺满；
4. `CoreWindow`（`CoreWindow.GetForCurrentThread()`）通过 `ICoreWindowInterop` 拿到 HWND，用于把 `WM_SIZE` / `WM_SETTINGCHANGE` / `WM_THEMECHANGED` 转发给 XAML；
5. 形状裁剪用 `CreateRectRgn` + `SetWindowRgn`（按需，不参与透明）；
6. XAML 内容本身用半透明 `AcrylicBrush`（TintOpacity 0.5 / TintLuminosityOpacity 0.96 深色等）提供毛玻璃观感；
7. 可选的“永不激活”路径：`WS_EX_NOACTIVATE` + `WH_CBT` 钩子 + 子窗口 subclass（`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`）。

**未使用**：`DwmSetWindowAttribute` / `DWMWA_*` / `DwmExtendFrameIntoClientArea` / `SetWindowCompositionAttribute` / `WS_EX_LAYERED` / `SetLayeredWindowAttributes` / `SetParent` 到 Progman/WorkerW。DesktopFlyouts 的“桌面嵌入”本质是**置顶透明浮层**，不是 WorkerW 壁纸层挂载。

### 具体文件 / 行 / 符号

| 项 | 位置 | 说明 |
|---|---|---|
| `IXamlSourceTransparency` 定义 | `src/DesktopFlyouts.Shared/Helpers/IXamlSourceTransparency.cs` L13-L35 | `[GeneratedComInterface, Guid("06636C29-5A17-458D-8EA2-2422D997A922")]`；注释称 “same interface as IWindowPrivate”；方法 `get_IsBackgroundTransparent` / `set_IsBackgroundTransparent`（`I1` BOOL） |
| 打开透明 | `src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs` L454-L455 | `Window.Current.As<IXamlSourceTransparency>()` → `set_IsBackgroundTransparent(true)` |
| 宿主窗口样式 | 同文件 L116-L120 | `WS_EX_NOREDIRECTIONBITMAP \| WS_EX_TOOLWINDOW \| WS_EX_TOPMOST`，`WS_POPUP` |
| DWS 初始化（UWP 分支） | 同文件 L438-L448 | QI `IID_IDesktopWindowXamlSourceNative2`（L438）；`AttachToWindow(HWnd)`（L447）；`get_WindowHandle`（L448） |
| CoreWindow HWND 获取 | 同文件 L458-L463 | `CoreWindow.GetForCurrentThread()` → QI `ICoreWindowInterop` → `get_WindowHandle` |
| 消息转发 | 同文件 L478-L500 | `WM_SIZE`：同步 `_xamlHwnd` 尺寸并 `SendMessage(_coreHwnd, WM_SIZE, ...)`（L478-L487）；`WM_SETTINGCHANGE/WM_THEMECHANGED` 转发（L496-L497） |
| 键盘消息预翻译 | 同文件 L336-L340 | `IDesktopWindowXamlSourceNative2.PreTranslateMessage` |
| 区域裁剪 | 同文件 L190-L194 | `CreateRectRgn` + `SetWindowRgn`（同时作用于宿主和岛 HWND） |
| 永不激活样式 | 同文件 L364-L365 | `WS_EX_NOACTIVATE` 增删（配合 `SWP_FRAMECHANGED`） |
| IID 常量 | `src/DesktopFlyouts.Shared/Helpers/ManualDefinitions.cs` L44-L58 | `IID_IDesktopWindowXamlSourceNative2 = {E3DCD8C7-3057-4692-99C3-7B7720AFDA31}`（L46-L50）；`IID_ICoreWindowInterop = {45D64A29-A63E-4CB6-B498-5781D298CB4F}`（L52-L56），字节序数组 |
| UWP 岛模板 | `src/DesktopFlyouts.Uwp/DesktopFlyout.xaml` L64-L80 | `Grid` 背景绑定 `Background`（L66）；`Translation="0,0,36"`（L70，阴影 Z 深度魔法数）；`ThemeShadow`（L72） |
| 半透明材质 | `samples/DesktopFlyouts.Uwp.Sample.TrayHost/MainDesktopFlyout.xaml` L16-L39 | 深色 `AcrylicBrush`：`FallbackColor=#FF1C1C1C`、`TintColor=#FF202020`、`TintLuminosityOpacity=0.96`、`TintOpacity=0.5`；浅色 `#FFEEEEEE / #FFF3F3F3 / 0.9 / 0.0`；岛 `Background="{ThemeResource FlyoutAcrylicBrush}"` |
| 透明引入提交 | commit `26f6612`（2025-12-06） | 首次加入 `IXamlSourceTransparency.cs` 与 `set_IsBackgroundTransparent(true)`；同时把岛模板背景改为 `TemplateBinding Background` |

### 关键“魔法数字”

- `06636C29-5A17-458D-8EA2-2422D997A922`：`IXamlSourceTransparency`（内部 `IWindowPrivate`）IID；在 `Window` 上 QI 失败则无法打开透明。
- `E3DCD8C7-3057-4692-99C3-7B7720AFDA31`：`IDesktopWindowXamlSourceNative2` IID（含 `AttachToWindow` / `get_WindowHandle` / `PreTranslateMessage`）。
- `45D64A29-A63E-4CB6-B498-5781D298CB4F`：`ICoreWindowInterop` IID。
- `WS_EX_NOREDIRECTIONBITMAP = 0x00200000`（宿主窗口不创建 GDI 重定向表面）。
- `WS_EX_TOOLWINDOW = 0x00000080`、`WS_EX_TOPMOST = 0x00000008`、`WS_EX_NOACTIVATE = 0x08000000`。
- `Translation="0,0,36"`：ThemeShadow 的 Z 轴位移（像素/逻辑单位）。
- 仅对 **当前线程** 上所有 `DesktopWindowXamlSource` 生效（官方文档 Remarks）。

### 待验证（进入阶段 2/3）

- [x] 本机最小 UWP XAML Island 工程：`IsBackgroundTransparent=false` vs `true` 的像素级差异 → 已完成，见阶段 2
- [x] 置顶透明浮层（DesktopFlyouts 原方案）在本机桌面上的透视效果 → 已完成，见阶段 2
- [x] 用 `SetParent` 挂到 `Progman/WorkerW`（任务额外要求）后透明是否仍成立 → 已完成，见阶段 3

## 阶段 2：最小 UWP XAML Island 工程实测（已完成）

工程：`src/IslandProbe/`（C# / net10.0-windows10.0.26100.0 / `<UseUwp>true</UseUwp>` / Microsoft.UI.Xaml 2.8.7 / CsWinRT 2.2.0 / CsWin32 0.3.275）
构建：VS 2026 MSBuild `IslandProbe.csproj /p:Platform=x64 /p:Configuration=Debug` → 成功
运行环境：DPI=144（150%），交互式会话；窗口物理矩形 `(60,60) 520x380`，绿色对照窗 `(20,20) 600x460`

对照设计（单变量）：同一工程、同一份 XAML 内容，仅切换 `IXamlSourceTransparency.IsBackgroundTransparent`。
XAML 内容：左右两列，左列无背景，右列 50% 红（`#80FF0000`）。采样为屏幕合成后的真实像素（`GetPixel` + 外部截图）。

| 配置 | 左半透明区采样 (190,269) | 右半 50% 红采样 (450,269) | 岛左上角 (65,65) | 对照窗外部 (30,30) |
|---|---|---|---|---|
| `IsBackgroundTransparent=true` | RGB(0,255,0)（绿色对照窗透出） | RGB(128,127,0)（红+绿各半） | RGB(0,255,0) | RGB(0,255,0) |
| `IsBackgroundTransparent=false` | RGB(255,255,255)（XAML 不透明白底） | RGB(255,127,127)（红+白各半） | RGB(255,255,255) | RGB(0,255,0) |

- 设置调用均返回 `hr=0x00000000`，读回值与设置值一致（日志 `topmost-on.log` / `topmost-off.log`）。
- 结论：**透明成立**；`IsBackgroundTransparent=true` 时 XAML 岛自身背景不再绘制，宿主后方内容可见；关闭时恢复为不透明白底。
- 截图证据：`artifacts/topmost-on.png`、`artifacts/topmost-off.png`（裁剪图 `*-crop.png`）。

## 阶段 3：嵌入桌面（WorkerW）实测（已完成）

说明：DesktopFlyouts 仓库本身**没有** `SetParent`/`Progman`/`WorkerW`，其原方案就是阶段 2 的置顶透明浮层（已验证）。按任务要求额外做了 WorkerW 挂载实验。

本机真实桌面窗口树（实测枚举）：
- `Progman = 0x1015C`（visible），其子窗 `SHELLDLL_DefView = 0x10160`（图标层）与 `WorkerW = 0xC0908`（visible，壁纸宿主）。
- 顶层存在大量同名 `WorkerW`，但均为 **不可见** 辅助窗（explorer/SystemSettings 等进程），经典“找顶层 WorkerW”写法会拿到无效/不可见句柄。

首次失败证据（保留）：`workerw-on.log`（第一次）
- 选中的顶层 `WorkerW 0x1019E` 随后失效，`SetParent` 返回 `ERROR_INVALID_PARAMETER (87)`，`hostIsChild=False`；窗口被改成无父的 `WS_CHILD` 后不再显示，此时采样无效。
- 根因：Win11 Build 26300 的壁纸 WorkerW 是 Progman 的子窗口，不是顶层。
- 修正：优先取 `FindWindowEx(Progman, NULL, "WorkerW", NULL)`，回退才枚举可见顶层 WorkerW。

成功复测（`workerw-on2.log` / `workerw-off2.log`）：
- `Progman child WorkerW=0xC0908 valid=True visible=True`
- `SetParent` 成功：`lastErr=0`、`hostIsChild=True`、`backdropIsChild=True`；宿主样式 `0x44000000`（`WS_CHILD|WS_VISIBLE`），岛根窗口为 `Progman`。

| 配置 | 左半透明区采样 (190,269) | 右半 50% 红采样 (450,269) | 说明 |
|---|---|---|---|
| `IsBackgroundTransparent=true` | RGB(200,207,217)（桌面壁纸透出） | RGB(228,103,108)（红与壁纸各半） | 透明仍生效 |
| `IsBackgroundTransparent=false` | RGB(255,255,255)（不透明白底） | RGB(255,127,127)（红与白各半） | 对照成立 |

- 额外观察：同时挂进 WorkerW 的普通 GDI 绿色对比窗**不参与桌面合成**（其区域采样 (30,30) 始终为壁纸色 RGB(193,203,215)，非绿色）；同样位置换成 DComp/XAML 内容则正常显示。因此 WorkerW 实验以壁纸作为“后方参照”，并以 ON/OFF 差值 + 红色混色算式交叉验证。
- 结论：**SetParent 到 Progman 子 WorkerW 后透明仍然成立**；透明岛的左半显示壁纸、右半为 50% 红与壁纸混色。
- 截图证据：`artifacts/workerw-on2.png` / `workerw-off2.png`（裁剪图 `workerw-on.png`/`workerw-off.png` 及 `-crop.png`）。

## 阶段 4：WinUI 2 对照实验（Microsoft.UI.Xaml 2.x）（已完成，结论为“内容激活失败”）

工程：`src/WinUI2Probe/`（独立工程，与 IslandProbe 共存；宿主/采样代码复用，仅内容构造改为 WinUI 2）
构建：VS 2026 MSBuild `WinUI2Probe.csproj /p:Platform=x64 /p:Configuration=Debug` → **成功**（`Microsoft.UI.Xaml.Controls.NavigationView` 可赋给 `DesktopWindowXamlSource.Content`，类型兼容）
内容构造：合并 `Microsoft.UI.Xaml.Controls.XamlControlsResources` → `new Microsoft.UI.Xaml.Controls.NavigationView()` → 设 `Content` 为与阶段 2 相同的两列采样布局（左列无背景、右列 50% 红）。失败时不设替代内容，岛保持为空。

失败证据（4 组运行日志一致，`WinUI2Probe-*.log`）：
- `[WINUI2] XamlControlsResources FAILED: System.Runtime.InteropServices.COMException hr=0x80040154 msg=没有注册类 (0x80040154 (REGDB_E_CLASSNOTREG))`
- `[WINUI2] new NavigationView() FAILED: ... hr=0x80040154 (REGDB_E_CLASSNOTREG)`
- `[DWS] AttachToWindow(host) => 0x00000000`、`[XST] set_IsBackgroundTransparent(...) hr=0x00000000`（宿主与透明 API 本身正常）
- `[DWS] Content NOT set (WinUI 2 content creation failed)`

| 配置 | 左半 (190,269) | 右半 (450,269) | 说明 |
|---|---|---|---|
| topmost / `true` | RGB(0,255,0) | RGB(0,255,0) | 整岛透出绿对照窗，无红色内容 |
| topmost / `false` | RGB(255,255,255) | RGB(255,255,255) | 空白岛默认白底 |
| workerw / `true` | RGB(160,196,208) | RGB(164,198,210) | 透出壁纸，无红色内容 |
| workerw / `false` | RGB(255,255,255) | RGB(255,255,255) | 白底 |

根因（实测旁证）：
- 本机已安装 `Microsoft.UI.Xaml.2.8 8.2511.26001.0 (x64)` 框架包（`Get-AppxPackage Microsoft.UI.Xaml*`）。
- 但探针是**未打包 Win32 进程**，无 MSIX 包标识/框架包依赖图，WinRT 激活无法解析框架包内可激活类 → `REGDB_E_CLASSNOTREG`。
- 输出目录仅有 `Microsoft.UI.Xaml.Projection.dll`，无原生 `Microsoft.UI.Xaml.dll`。
- 未采用 MSIX 打包/声明框架包依赖等手段绕过；因此“WinUI 2 内容自身透明”无法判定。
- 结论：**宿主层透明仍成立（true 透出绿底/壁纸，false 白底；WorkerW SetParent 成功 lastErr=0），但 WinUI 2 内容从未渲染；这不是透明失败。**
