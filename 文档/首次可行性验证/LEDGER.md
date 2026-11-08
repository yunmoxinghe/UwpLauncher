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
- 该阶段未采用 MSIX 打包/声明框架包依赖等手段绕过（为保住“未打包宿主 + WinUI 2”单变量前提）；因此“WinUI 2 内容自身透明”在**未打包前提**下无法判定。
- 结论（阶段 4）：**宿主层透明仍成立（true 透出绿底/壁纸，false 白底；WorkerW SetParent 成功 lastErr=0），但 WinUI 2 内容从未渲染；这不是透明失败。** 该结论已被阶段 5 的 MSIX 复测取代。

---

## 阶段 5：MSIX 打包路线（WinUI 2 正式承载）

### 联网文献要点（完整引用见 VERIFICATION.md 5.1）

- Microsoft Learn《Host UWP XAML controls in desktop apps》明确：XAML 岛承载 WinUI for UWP Library 是**有条件**的——用 MSIX 部署时可用正式版 `Microsoft.UI.Xaml` NuGet；未打包时只能用预发行版 NuGet 或 Dynamic Dependencies API。
- Microsoft Learn《MSIX framework packages and dynamic dependencies》明确：进程包图提供 “run-time class declarations”；打包应用的包图由清单 `PackageDependency` 初始化，**未打包应用的包图为空**。
- Microsoft Learn《Use the dynamic dependency API…》明确：动态依赖成功后 “the app might activate classes and use content from the MSIX package”。
- 推断：未打包进程包图为空 → WinRT 找不到 `Microsoft.UI.Xaml.*` 的可激活类 → `REGDB_E_CLASSNOTREG`；MSIX 声明框架包依赖后类注册可见 → 不再报错。文档未逐字给出该 HRESULT 因果句。
- 本机旁证：框架包 `Microsoft.UI.Xaml.2.8 8.2511.26001.0` 清单内有 `windows.activatableClass.inProcessServer` + `Microsoft.UI.Xaml.dll`，注册 `NavigationView`、`XamlControlsResources` 等；NuGet 2.8.7 不含原生 DLL。

### 实现与安装

- 新增 `src/WinUI2Probe/Package.appxmanifest`：`Windows.FullTrustApplication` + `runFullTrust` + `PackageDependency Microsoft.UI.Xaml.2.8`。
- 新增 `scripts/package-msix.ps1`：MSBuild → 组装布局 → 生成 logo → `makeappx` → 自签名 `signtool` → 安装/松注 → `artifacts/msix-install-status.txt`。
- `run-probe.ps1` 新增 `-Packaged`（`IApplicationActivationManager` 按 AUMID 启动）与 `-Content winui2|grid`；workerw 模式在启动前额外截取**无遮挡桌面基准图**。

### 结果

- 签名 MSIX 安装失败：`0x800B0109`；本会话非管理员，无法把自签名证书放入 `LocalMachine\TrustedPeople`。回退开发者模式 `Add-AppxPackage -Register` 成功，身份 `WinUI2Probe_1.0.0.0_x64__rdz54j04exa60`。
- 包标识下 `XamlControlsResources` / `NavigationView` / `InfoBar` 全部创建成功，`0x80040154` 消失；InfoBar 像素 `RGB(255,244,206)` 证明真实渲染。
- 透明：topmost/workerw × 开/关 4 组 + `grid` 对照。岛级透明成立（对照严格等于绿/壁纸）；`NavigationView` 模板自带 `NavigationViewContentBackground=#80FFFFFF`，画面为 50% 白叠底（`128,255,128`；workerw `246,234,232`），属控件内容层而非透明失效。
- `SetParent` 到 `Progman` 子 WorkerW 在打包上下文下无新限制（`lastErr=0`），壁纸采样与桌面基准一致。

### 证据文件

- `artifacts/WinUI2Probe-topmost-on-20261108-143135.log`（激活/渲染/资源诊断）
- `artifacts/WinUI2Probe-topmost-off-20261108-143146.log`
- `artifacts/WinUI2Probe-workerw-on-20261108-143452.log` / `workerw-off-20261108-143517.log`
- `artifacts/WinUI2Probe-workerw-on-20261108-143532.log`（grid 对照）及同名 `-desktop.png`
- `artifacts/msix-install-status.txt` / `artifacts/msix/package.log`（后者为临时目录，git 忽略）

---

## 阶段 6：Frame + Page 导航扩展性（新增）

工程：`WinUI2Probe` 新增 `--content nav`；页面 `PageA.xaml` / `PageB.xaml`（XAML 编译，类型进入 `XamlTypeInfo`）+ `NavigationPages.cs`。自动化序列：初始 → 改 PageA 状态（文本 + 滚动 120）→ `Navigate(PageB)` → `GoBack` → 放大宿主，全阶段像素采样。

### 结果

- `DesktopWindowXamlSource.Content = Windows.UI.Xaml.Controls.Frame`；`Navigate(PageA)` 返回 `True`，页面真实渲染：顶部色带像素 A=`RGB(30,144,255)`、B=`RGB(255,69,0)`、返回后 A=`RGB(30,144,255)`。
- 导航栈正常：`BackStackDepth` 0→1→0，`CanGoBack` False→True→False，返回 `NavigationMode=Back`；`OnNavigatedTo` 能读到 `Frame`、参数与栈状态。
- **状态保留坑**：默认 `NavigationCacheMode=Disabled` 时 `GoBack` 重建页面（实例数 1→2），`text` 由 `A0-mutated` 回到 `state`、滚动由 120 回到 0；`--navcache 1`（`Required`）时复用实例（1→1），文本与滚动位置保留。
- **透明保持**：topmost 开 三阶段均 `(0,255,0)` / `(128,127,0)`；workerw 开 三阶段均 `(3,14,32)` / `(132,12,25)`，与启动前桌面基准同点逐通道一致，右半 `132 ≈ 0.5×255 + 0.5×8`（G/B 误差 ≤1）。
- **尺寸同步**：`Frame` 初始 `346.7x253.3` DIP = 岛 520x380 @150%；宿主放大到 640x440 后 `426.7x293.3`，`SizeChanged` 2 次。
- **资源解析**：页面级 `PageAOnlyBrush=#FF1E90FF` 可解析；应用级 `ApplicationPageBackgroundThemeBrush` 可解析（打包观察到 Light `#FFF3F3F3` / Dark `#FF202020`；未打包 `#FF000000`）。
- **常见坑**：纯 C# Page（未进 `XamlTypeInfo`）会让 `Frame.Navigate` 直接崩溃（日志停在 `Navigating`，无托管异常/错误码），改为 XAML 编译页面后正常（推断：需 XAML 元数据提供者认识目标类型）；UWP `Page` **没有** `NavigationService` 属性（等价物 `Page.Frame`）；`GoBack()` 返回 `void`。
- **打包 vs 未打包**：Frame/Page 属系统 UWP XAML，两种上下文都能导航与渲染、行为一致；差异仅在未打包时 WinUI 2 主题资源仍报 `0x80040154`（阶段 4/5 结论不变）。

### 证据文件

- `artifacts/WinUI2Probe-topmost-on-20261108-150810.log`（打包，默认缓存，透明开）
- `artifacts/WinUI2Probe-topmost-off-20261108-151126.log`（不透明对照 + 三阶段状态）
- `artifacts/WinUI2Probe-topmost-on-20261108-151144.log`（`Required` 缓存对照）
- `artifacts/WinUI2Probe-workerw-on-20261108-151203.log` / `-desktop.png`（壁纸基准 + 混色验算）
- `artifacts/WinUI2Probe-workerw-off-20261108-151224.log` / `-desktop.png`
- `artifacts/WinUI2Probe-topmost-on-20261108-151238.log`（未打包上下文对照）

### 剩余待验证（阶段 6 范围内）

- 无 `Window.Current` 的岛宿主（本宿主 `Window.Current` 非 null）
- 无效页面类型的失败码、`Frame` 的 `Enabled` 缓存策略/转场、`OnNavigatingFrom` 取消、多岛并发
- 页面内输入/焦点/命中测试（含 WorkerW 子窗）→ 已由阶段 7 用 UIA 真实点击覆盖

## 阶段 7：嵌入态输入修复（新增，已完成）

### 调研出处（官方 / 社区分开标注）

| 结论 | 出处 | 性质 |
|---|---|---|
| `DefWindowProc` 在处理前把子窗 `WM_MOUSEACTIVATE` 交给父窗，父窗决定是否激活 | https://learn.microsoft.com/windows/win32/inputdev/wm-mouseactivate | 官方文档 |
| `AttachThreadInput` 让两个线程共享输入状态（键盘状态、当前焦点窗） | https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-attachthreadinput | 官方文档 |
| 跨进程父子窗“合法但有代价” | https://devblogs.microsoft.com/oldnewthing/20130412-00/?p=4683 | 微软官方博客（非产品文档） |
| `WS_EX_TRANSPARENT` 只影响绘制；分层窗口 + 该样式才会忽略形状、让鼠标事件穿透；普通窗口要穿透应在 `WM_NCHITTEST` 返回 `HTTRANSPARENT` | https://learn.microsoft.com/windows/win32/winmsg/window-features | 官方文档 |
| `WS_EX_NOACTIVATE`：点击不激活但仍收鼠标消息 | https://learn.microsoft.com/windows/win32/winmsg/extended-window-styles | 官方文档 |
| `SHELLDLL_DefView` 覆盖 `WorkerW` 子窗，点击被其接管 | 本机 `[DESK]`/`[HITTEST]` 实测 + https://learn.microsoft.com/en-us/answers/questions/1386630/doubts-about-the-window-of-program-manager | 本机实测 + 社区问答 |
| Lively Wallpaper 用 `SetParent(handle, workerW)`，另在 raised desktop 下调整层序 | https://deepwiki.com/rocksdanister/lively/3.1-desktop-core | 开源代码索引 |
| Wallpaper Engine / Rainmeter 的桌面层实现细节 | 社区经验（无公开源码/实现文档；Rainmeter 手册仅定义 “On Desktop” 语义） | 社区经验 |

### 实现

- `ReparentToDesktopPane()`：`FindWindow("Progman")` → `SetParent(host, Progman)` → `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_SHOWWINDOW|SWP_FRAMECHANGED)`，把宿主放到 `SHELLDLL_DefView` 之上；旧路线 `ReparentToWorkerW()` 保留用于复现失败。
- `WndProc` 处理 `WM_MOUSEACTIVATE` 返回 `MA_ACTIVATE`，不再交给桌面父窗裁决。
- `[DESK]` 打印 Progman 子窗 Z 序；`[HITTEST]` 打印采样点命中类名/是否宿主；`[INPUT]` 记录鼠标/激活消息，用于区分“消息没到”和“XAML 没反应”。
- 启动顺序：先 `TrySetIsBackgroundTransparent(true)`，`ShowWindows` 后 `ApplyTransparency()`，再做桌面挂载与工作区布局。
- 全局热键 `Ctrl+F2/F3/F4`（透明/模式/退出），嵌入后无焦点也能操作。

### 结果

- `topmost`：UIA 真实点击 导航/透明/铺满桌面/退出 **4/4 PASS**（`artifacts/WinUI2Demo-topmost-uia-20261108-171957.result.txt`）。
- `desktoppane`（路线 C）：**4/4 PASS**，点击下方均为本进程 `WinUI2DemoHost`，`root=Progman 0x1015C`（`...-desktoppane-uia-20261108-172121.result.txt`）。
- `workerw`（旧路线）：**4/4 FAIL**，全部命中 `SHELLDLL_DefView 0x10160 pid=8812`，demo 日志无任何 `[NAV]`/`[DEMO]` 事件（`...-workerw-uia-20261108-170219.result.txt`）。
- **首启时序（如实记录）**：重新注册包后的首轮（17:17）nav/quit 在 4s 内无反应，日志显示点击被延迟约 11s 才由 XAML 处理；同包热启动复跑 4/4，最终干净回归（17:19/17:21）4/4。脚本已加前台等待（`SetForegroundWindow`）、首步 12s 等待与失败补点；系统侧确切原因未定位，仅记录现象，不作为修复结论。
- 透明未被破坏（新布局 `(0,0,1920,1008)`，144 DPI）：on 时 `(200,300)=RGB(4,15,33)` 与桌面基线逐通道一致、`(1400,300)=RGB(193,5,12)` 满足 `0.5×红 + 0.5×基线`；off 时 `RGB(0,0,0)`/`RGB(128,0,0)`；topmost 与 desktoppane 完全一致（`WinUI2Demo-*-transparent{on,off}-20261108-1703*`）。最终 Release 未重跑该矩阵（用户要求停止新增测试）；透明相关代码未改动，详见 VERIFICATION 10.5 的未完成说明。

### 失效路线

| 路线 | 现象 / 错误码 |
|---|---|
| 顶层同名 `WorkerW` 当父窗 | `SetParent lastErr=87 (ERROR_INVALID_PARAMETER)`，窗口变无父 `WS_CHILD` 后不再显示（阶段 3.3） |
| Progman 子 `WorkerW`（壁纸层） | `SetParent lastErr=0`、透明成立，但点击全部落在 `SHELLDLL_DefView` |
| `WS_EX_TRANSPARENT` / `HTTRANSPARENT` | 会放弃接收点击，与目标相反；未采用（按官方语义排除，未实测） |
| `WH_MOUSE_LL` 钩子转发 | 社区备选；未实现/未实测，不作为结论 |
| `AttachThreadInput` | 未采用/未实测；路线 C 不需要 |

### 结论

推荐路线 C（Progman 子窗 + DefView 之上 Z 序 + `MA_ACTIVATE`）：实测稳定、无钩子副作用、透明不变；代价是宿主矩形内桌面图标不可点，需在模式切换后重排布局。

## 阶段 8：工作区检测与铺满范围（新增，已完成）

### 实现

- `EnumDisplayMonitors` + `MONITORINFOEXW`（`rcMonitor`/`rcWork`/`szDevice`/primary）+ `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`；`MonitorFromWindow(host, MONITOR_DEFAULTTONEAREST)` 选当前屏。
- `SPI_GETWORKAREA` 与 `ABM_GETTASKBARPOS` 对照，日志输出 `equalCurrentRcWork`/`equalPrimaryRcWork`、任务栏边与矩形、insets。
- 布局基准默认当前屏 `rcWork`；`--fill work|desktop|none`、界面“铺满桌面”开关；模式切换后重新套用布局并做父客户区偏移校正。
- `WM_SETTINGCHANGE(SPI_SETWORKAREA)` / `WM_DISPLAYCHANGE` / `WM_DPICHANGED` 触发重算。
- 界面 `WorkspaceText` 显示显示器、像素/DIP、两组工作区矩形、SPI 对照、任务栏边、布局判定。

### 本机实测

- `rcMonitor=(0,0,1920,1080)`、`rcWork=(0,0,1920,1008)`、`dpi=144`、`dipMonitor=1280x720`、`dipWork=1280x672`；`SPI_GETWORKAREA` 与当前/主屏 `rcWork` 都相等；任务栏 `edge=下(3) rect=(0,1008,1920,1080)`，底部 inset 72px/48dip。
- 四组组合（`test-workspace.ps1`）`RESULT failures=0`（`artifacts/workspace-20261108-172144.result.txt`）：
  - topmost+work / desktoppane+work：`workBottom-6px` 命中本进程且与基线差 252；任务栏带命中 `Shell_TrayWnd`、像素差 232（宿主在下）→ 铺满工作区、任务栏在上。
  - topmost+desktop：任务栏带命中 `Shell_TrayWnd`（早期一轮曾命中本进程）、差 232 → 窗口矩形铺满整个桌面，但**不保证盖住任务栏**（同为 topmost，Z 序可变）。
  - desktoppane+desktop：任务栏带仍命中 `Shell_TrayWnd`、像素差 232（Acrylic 透出下层宿主）→ 窗口矩形铺满，可见范围仍被任务栏裁掉。
- 特殊行为：嵌入态任务栏始终在宿主之上；“铺满整个桌面”只等于窗口矩形铺满；置顶态是否盖住任务栏按 `WindowFromPoint` 实测输出，不再硬编码断言；父客户区坐标需复核；DIP 跟随当前屏 DPI；图标可点性让位于输入修复。

### 未覆盖

- 任务栏换到左/上（需改注册表并重启 explorer，属修改系统设置，未执行）
- 多显示器 / 副屏异 DPI（本机 `SM_CMONITORS=1`）
- 运行中改 DPI / 拔插显示器（未触发；代码已接消息）
- 任务栏自动隐藏 / 其他 AppBar

### 证据

- `artifacts/workspace-20261108-172144.result.txt`（含四组采样与判定）
- `artifacts/workspace-<mode>-<fill>-20261108-172144.log/.png`、`workspace-20261108-172144-baseline.png`
- [scripts/test-workspace.ps1](scripts/test-workspace.ps1)

## 阶段 9：文件视图 Page（新增，已完成）

### 目标与范围

按 `文档/文件显示性能调研/` 落地 `FileViewPage`，可从 PageA/PageB 导航进入；只做第一版：后台 Win32 枚举 + 时间片喂入 + 虚拟化列表 + 两段式图标 + 可见进度。

### 实现

- `FileEnumeration.cs`：`FindFirstFileExW` + `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`，后台线程；首批 60 项 / 25 ms（至少 30 项，硬上限 100 ms），之后每 500 ms 或 2 万项一批；`FileEntry` 的大小/时间/类型字符串在后台预格式化。
- `FileViewPage.xaml(.cs)`：`ListView` + `ItemsStackPanel` + `x:Bind` + `x:Phase`；`BulkObservableCollection` 每批只发一次 `Reset`，不逐项 marshal；进度/指标状态栏；`OnNavigatedFrom` 取消并 generation 隔离旧回调。
- 图标：扩展名字形立即可见 → 容器实现时 8 路信号量 `StorageFile.GetThumbnailAsync` 补 32 px 真图标，失败保留字形（偏离调研的 `IShellItemImageFactory`，理由与代价见 VERIFICATION 12.2）。
- 导航：PageA/PageB 增加“→ 文件视图”；`--dir` 指定默认目录，`run-demo.ps1 -Dir` 可传。

### 本机实测

- 数据集：`C:\Users\gold\perf-research\data\f10k`（10 000）、`f100k`（100 000），真实 NTFS 目录。
- topmost + f10k：首批 60 @111 ms、首次容器 @139 ms、枚举 48 ms、全部入列表 113 ms、2 批、UI 应用最大 1 ms、喂入心跳 103 ms；滚动 first=4996、容器 79。
- desktoppane + f100k：首批 30 @126 ms、首次容器 @183 ms、枚举 321 ms、全部入列表 479 ms、6 批、UI 应用最大 5 ms、喂入心跳 265 ms；滚动 first=49996、容器 99。
- 图标：f10k/f100k 滚动后 79/79/0（请求/成功/失败）；图片目录 2/2/0。
- 输入/透明回归：两轮均通过真实点击进入、页内透明关/开、返回、再进入、退出；desktoppane 轮 Ctrl+F4 宏键退出通过。
- 证据：`artifacts/WinUI2Demo-FileView-topmost-20261108-175823.result.txt`、`...-desktoppane-20261108-175844.result.txt`、[scripts/test-fileview-uia.ps1](scripts/test-fileview-uia.ps1)。

### 失败路线与未完成

- `workerw`（壁纸层）点击仍到不了宿主（命中 `SHELLDLL_DefView`），该轮 `failures=11`；这是该层定位决定，不是本页回归。可点击嵌入用 `desktoppane`。
- 100k 喂入期出现过一次 ~265 ms UI 心跳间隔（XAML 大批 Reset 的布局成本；集合写入仅 5 ms）；后续点击/滚动正常，但不宣称“零影响”。
- 冷启动首批曾只有 1 项，已通过“open 后计时 + 最少 30 项”修正。
- 未做：排序、分组、右键菜单、拖放、搜索、缩略图大图、多目录并发、网络路径超时、实时刷新、键盘快捷键。
- 未覆盖：多显示器 / 副屏异 DPI；100k 的 topmost、10k 的 desktoppane 未成对跑（各有一条模式覆盖）。

## NativeAOT 一级路线（2026-11-08）

### 做了什么

- 只改构建属性，不改代码：`/t:Publish /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true`，由 VS MSBuild 直接编出原生 exe（7.02 MB）。
- AOT publish 不带 `*.xbf`，验证时从普通构建补齐后替换进已注册的 loose 布局。
- 顺带补了一个滚动观测的健壮性改动：`FileList.LayoutUpdated` 也尝试挂载 ScrollViewer（CoreCLR 下无回归，AOT 下仍未挂上，见下）。

### 结论

- **能跑**：包内启动、WinUI2 控件激活、DWS 挂载、`IXamlSourceTransparency`、Frame/Page 导航、FileViewPage 100k 枚举/滚动、页内透明开关、Ctrl+F4 退出全部实测通过；透明像素采样四组 `failures=0`。
- **内存**：空载 WS/Private 94.1/24.2 → 56.0/15.9 MB；100k feedDone 184.7/89.5 → 148.9/82.8；二次加载 233.4/138.9 → 189.3/125.4。托管 42.80 → 42.76 MB，**自持模型没有变化**，仍 ≈42.4 MB（目标 <10 MB 未达）。GC pauseΔ 204 → 115 ms。
- **一处差异**：AOT 下 `FindScrollViewer` 返回 null，`[FV] scrollviewer attached` 缺失（UIA 脚本 scroll 断言 FAIL）；独立探针证明滚动本身正常（pct 0→100、realized=38）。
- 未打包直跑 AOT exe 会因缺包注册报 `0x80040154`；打进 MSIX 后正常，不算 AOT 限制。

### 推荐

- 一级未失败 → 不启动二级 C++/WinRT。交付继续用 CoreCLR（打包简单、已验收）；AOT 作为可选的“低常驻”构建保留。
- 下一轮内存优化应针对条目模型与图标缓存，而不是运行时。
