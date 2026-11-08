# UWP XAML Island 透明背景：DesktopFlyouts 考据 + 本机实测验证

> 日期：2026-11-08（Asia/Shanghai）
> 机器：Windows 11 Build 26300.0，VS 2026 18.11，Windows SDK 26100/28000，.NET SDK 10.0.403，DPI 150%（144），1280x720 单屏
> 被测仓库：`https://github.com/0x5bfa/DesktopFlyouts` @ `6cdcef5538db19e044ac20ba6fb010f322ef2881`（`git describe` = `v1.6.0-1-g6cdcef5`）

## 一句话结论

1. DesktopFlyouts 的 UWP 透明**不靠 DWM/分层窗口**，而是用 `Window.Current` 上未进入常规 SDK 投影的 WinRT 接口 `IXamlSourceTransparency`（IID `06636C29-5A17-458D-8EA2-2422D997A922`，内部即 `IWindowPrivate`）设置 `IsBackgroundTransparent = true`，配合 `WS_EX_NOREDIRECTIONBITMAP` 宿主窗口。
2. 本机最小工程实测：**透明成立**。`true` 时岛背景完全透出后方纯绿对照窗，`false` 时为不透明白底。
3. 嵌入桌面：DesktopFlyouts 原方案（置顶透明浮层）透明成立；额外用 `SetParent` 挂到 `Progman` 的子 `WorkerW` 后，**透明仍然成立**（左半透出壁纸，右半为 50% 红与壁纸混色）。
4. WinUI 2 对照：**未打包进程激活失败（`0x80040154`），MSIX 打包后激活与渲染成立**。第 4 节记录的 `REGDB_E_CLASSNOTREG` 属“未打包前提”的阶段结论；按正式路线改为 MSIX（声明 `Microsoft.UI.Xaml.2.8` 框架包依赖）后，`XamlControlsResources`、`NavigationView`、`InfoBar` 均正常创建与渲染（详见第 5 节）。
5. MSIX 打包后的透明复测：**岛级透明开关仍成立**（对照内容严格复现纯绿/壁纸透出），但以 `NavigationView` 为根时，其内容区模板自带 `#80FFFFFF`（50% 白）背景，画面是“50% 白 + 背景”的混色，不是纯壁纸色；这属于控件内容层，不是透明失效（详见 5.4）。
6. `Frame` + `Page` 导航扩展性：**可用**。`DesktopWindowXamlSource.Content = Frame`、`Navigate(typeof(Page))` 成功且页面真实渲染；导航栈 A→B→返回正常，透明在导航前后不丢失。两个关键坑：默认 `NavigationCacheMode=Disabled` 时 `GoBack` 会**重新创建页面实例**（控件状态与滚动位置丢失，需 `Required` 或外部状态管理）；纯 C#（未进 XamlTypeInfo）的 Page 类型会让 `Navigate` 直接崩溃，页面须经 XAML 编译（详见第 6 节）。

---

## 1. DesktopFlyouts 的实现考据

### 1.1 核心机制

```csharp
// src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs L454-455
var xst = Window.Current.As<IXamlSourceTransparency>();
xst.set_IsBackgroundTransparent(true);
```

接口按 IID 手工定义（C# 的 Windows SDK 投影没有现成类型）：

```csharp
// src/DesktopFlyouts.Shared/Helpers/IXamlSourceTransparency.cs L13-L35
[GeneratedComInterface, Guid("06636C29-5A17-458D-8EA2-2422D997A922")]
internal partial interface IXamlSourceTransparency // This is the same interface as IWindowPrivate
{
    // IInspectable 三个槽位（GeneratedComInterface 以 IUnknown 为基准，需要补齐）
    HRESULT GetIids(...); HRESULT GetRuntimeClassName(...); HRESULT GetTrustLevel(...);
    HRESULT get_IsBackgroundTransparent([MarshalAs(UnmanagedType.I1)] out bool pvalue);
    HRESULT set_IsBackgroundTransparent([MarshalAs(UnmanagedType.I1)] bool value);
}
```

该接口有独立官方文档页（Windows 10 17763+，且明确“对当前线程上所有 `DesktopWindowXamlSource` 生效”）：
<https://learn.microsoft.com/en-us/windows/apps/api-reference/interface-members/ixamlsourcetransparency-isbackgroundtransparent>

> 说明：它有文档，但不在 `DesktopWindowXamlSource` 主 API 文档与常规 .NET 投影中，必须按 IID 自行定义（本机复现也证明了这一点）。

### 1.2 配合项（缺一不可的部分）

| 作用 | 位置 | 关键点 |
|---|---|---|
| 宿主无 GDI 重定向表面 | [XamlIslandHostWindow.cs L116-L120](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L116-L120) | `WS_POPUP` + `WS_EX_NOREDIRECTIONBITMAP \| WS_EX_TOOLWINDOW \| WS_EX_TOPMOST` |
| 绑定 XAML 岛到 HWND | [L438-L448](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L438-L448) | QI `IDesktopWindowXamlSourceNative2` → `AttachToWindow(HWnd)` → `get_WindowHandle` |
| 取 XAML CoreWindow 的 HWND | [L458-L463](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L458-L463) | `CoreWindow.GetForCurrentThread()` → QI `ICoreWindowInterop` |
| 尺寸/主题消息转发 | [L478-L500](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L478-L500) | `WM_SIZE` 同步岛 HWND 后 `SendMessage(coreHwnd, WM_SIZE, ...)`；转发 `WM_SETTINGCHANGE/WM_THEMECHANGED` |
| 键盘消息预翻译 | [L336-L340](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L336-L340) | `IDesktopWindowXamlSourceNative2.PreTranslateMessage` |
| 形状裁剪（非透明必需） | [L190-L194](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L190-L194) | `CreateRectRgn` + `SetWindowRgn` |
| 永不激活（可选） | [L364-L365](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/XamlIslandHostWindow.cs#L364-L365) | `WS_EX_NOACTIVATE` 增删 |
| 毛玻璃观感（内容侧） | [DesktopFlyout.xaml L64-L80](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Uwp/DesktopFlyout.xaml#L64-L80)、[MainDesktopFlyout.xaml L16-L39](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/samples/DesktopFlyouts.Uwp.Sample.TrayHost/MainDesktopFlyout.xaml#L16-L39) | 岛背景 `AcrylicBrush`（深色 `TintOpacity=0.5 / TintLuminosityOpacity=0.96`，浅色 `0.0 / 0.9`） |

### 1.3 魔法数字 / IID 清单

| 常量 | 值 | 用途 |
|---|---|---|
| `IWindowPrivate` / `IXamlSourceTransparency` IID | `06636C29-5A17-458D-8EA2-2422D997A922` | 透明开关的 COM 入口 |
| `IDesktopWindowXamlSourceNative2` IID | `E3DCD8C7-3057-4692-99C3-7B7720AFDA31` | 绑定 HWND / 取岛 HWND / 预翻译消息 |
| `ICoreWindowInterop` IID | `45D64A29-A63E-4CB6-B498-5781D298CB4F` | 取 CoreWindow 的 HWND |
| `WS_EX_NOREDIRECTIONBITMAP` | `0x00200000` | 宿主窗口不创建 GDI 重定向表面 |
| `WS_EX_TOOLWINDOW` / `WS_EX_TOPMOST` / `WS_EX_NOACTIVATE` | `0x80` / `0x8` / `0x08000000` | 工具窗、置顶、不抢焦点 |
| `Translation="0,0,36"` | `0,0,36` | `ThemeShadow` 的 Z 位移（纯视觉，与透明无关） |
| Acrylic 参数 | `0.5`、`0.96`、`0.9`、`0.8`、`0.0`；`#1C1C1C`、`#202020`、`#EEEEEE`、`#F3F3F3` | 深/浅/强调色的毛玻璃材质 |

全部 IID 的字节数组定义见 [ManualDefinitions.cs L44-L58](https://github.com/0x5bfa/DesktopFlyouts/blob/6cdcef5538db19e044ac20ba6fb010f322ef2881/src/DesktopFlyouts.Shared/Helpers/ManualDefinitions.cs#L44-L58)。
透明功能由 commit `26f6612`（2025-12-06，“Pre-translate hwnd msg and fix the flyout visual”）引入。

### 1.4 没有用什么（重要）

在 DesktopFlyouts 全仓检索（含 src/samples/docs）**没有**发现：
`DwmSetWindowAttribute` / `DWMWA_*` / `DwmExtendFrameIntoClientArea` / `SetWindowCompositionAttribute` / `AccentPolicy` / `WS_EX_LAYERED` / `SetLayeredWindowAttributes` / `SetParent` / `Progman` / `WorkerW` 的实际调用。
（`NativeMethods.txt` 里声明了 `SetLayeredWindowAttributes`，但无调用点；属于未使用声明。）

结论：**透明 = XAML 合成层去掉不透明底 + 无重定向位图宿主窗口**，与 DWM 属性无关；“桌面嵌入”在其仓库里是 **置顶透明浮层**，不是 WorkerW 壁纸层。

---

## 2. 阶段 2：最小 UWP XAML Island 工程实测

### 2.1 工程与工具链

最小工程位于 [src/IslandProbe](src/IslandProbe)：

- [IslandProbe.csproj](src/IslandProbe/IslandProbe.csproj)：`net10.0-windows10.0.26100.0`、`<UseUwp>true</UseUwp>`、`Microsoft.UI.Xaml 2.8.7`、`CsWinRT 2.2.0`、`CsWin32 0.3.275`（与 DesktopFlyouts 同类 UWP XAML Islands 路径，非 WinUI3）
- [Program.cs](src/IslandProbe/Program.cs)：`WindowsXamlManager.InitializeForCurrentThread()` → `Window.Current` → `DesktopWindowXamlSource` → `IsBackgroundTransparent`
- [IXamlSourceTransparency.cs](src/IslandProbe/IXamlSourceTransparency.cs)：按 IID 手工定义
- [NativeMethods.json](src/IslandProbe/NativeMethods.json)：与 DesktopFlyouts 一致（`useSafeHandles=false`、禁用友好重载、COM 源生成 + `PreserveSig`）

构建命令（本机已验证成功）：

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "文档/首次可行性验证/src/IslandProbe/IslandProbe.csproj" `
  /restore /p:Platform=x64 /p:Configuration=Debug
```

### 2.2 实验设计（单变量）

- 同一工程、同一内容，仅切换 `--transparent 1|0`。
- 宿主窗口物理矩形 `(60,60) 520x380`；其后放一个纯绿（RGB 0,255,0）对照窗 `(20,20) 600x460`。
- XAML 内容：左右两列，左列**无背景**，右列 `#80FF0000`（50% 红）；无 ThemeShadow 等额外效果。
- 判据：屏幕合成后的真实像素（`GetPixel` + 外部截图），而不是窗口内部状态。

### 2.3 结果（置顶浮层 = DesktopFlyouts 原方案）

| 配置 | 左半透明区 (190,269) | 右半 50% 红 (450,269) | 岛左上 (65,65) | 对照窗外 (30,30) |
|---|---|---|---|---|
| `true` | **RGB(0,255,0)** 绿 | **RGB(128,127,0)** 红+绿各半 | RGB(0,255,0) | RGB(0,255,0) |
| `false` | **RGB(255,255,255)** 白 | **RGB(255,127,127)** 红+白各半 | RGB(255,255,255) | RGB(0,255,0) |

- `set_IsBackgroundTransparent` 均返回 `hr=0x00000000`，读回值与设置一致。
- 混合算式自洽：`true` 的红列 = 50% 红 + 绿 → (127.5+0, 0+127.5, 0) ≈ (128,127,0)；`false` 的红列 = 50% 红 + 白 → (255,127,127)。
- **结论：透明成立。** 关闭时并非“黑色/全透明”，而是 XAML 的不透明白底，说明开关确实控制 XAML 岛背景绘制。

截图：[topmost-on.png](artifacts\topmost-on.png) / [topmost-off.png](artifacts\topmost-off.png)（裁剪对照：[topmost-on-crop.png](artifacts\topmost-on-crop.png)）

---

## 3. 阶段 3：嵌入桌面（`Progman` / `WorkerW`）实测

### 3.1 先说明：DesktopFlyouts 不用 WorkerW

其仓库没有 `SetParent` 到桌面的代码；阶段 2 的置顶透明浮层就是它的“桌面呈现”方式，且已验证透明成立。以下是为满足任务要求额外做的 WorkerW 挂载实验。

### 3.2 本机真实窗口树（实测）

```
Progman 0x1015C (visible)
├─ SHELLDLL_DefView 0x10160 (visible)   ← 桌面图标层
└─ WorkerW 0xC0908 (visible)            ← 壁纸宿主
```

顶层还有十余个同名 `WorkerW`（explorer / SystemSettings 等进程），但全部 **`visible=False`**。

### 3.3 首次失败与根因（保留证据）

第一次按“经典写法”（枚举顶层 `WorkerW`、取无 `SHELLDLL_DefView` 者）选到 `0x1019E`：

```
[REPARENT] hwnd=0x1C0DC2 oldParent=0x0 newParent=0x1019E ... lastErr=87
[WORKERW] parent=0x1019E hostIsChild=False backdropIsChild=False
```

- `SetParent` 返回 `ERROR_INVALID_PARAMETER (87)`，失败后窗口已被改成无父的 `WS_CHILD`，因此两个窗口都不再显示，该轮采样无效。
- 根因：Win11 Build 26300 的壁纸 WorkerW 是 **Progman 的子窗口**；顶层同名窗口不是可用父窗。
- 修正：优先 `FindWindowEx(Progman, NULL, "WorkerW", NULL)`，仅在找不到时才枚举“可见且无 DefView”的顶层 WorkerW。

### 3.4 修正后结果（`workerw-on2.log` / `workerw-off2.log`）

```
[WORKERW] Progman child WorkerW=0xC0908 valid=True visible=True
[REPARENT] ... newParent=0xC0908 ... style=0x44000000 exStyle=0x200080 lastErr=0
[WORKERW] parent=0xC0908 hostIsChild=True backdropIsChild=True
[RECT] island screen=(60,60,580,440) root=0x1015C
```

| 配置 | 左半透明区 (190,269) | 右半 50% 红 (450,269) | 说明 |
|---|---|---|---|
| `true` | **RGB(200,207,217)** 壁纸透出 | **RGB(228,103,108)** 红+壁纸各半 | 透明仍生效 |
| `false` | **RGB(255,255,255)** 白 | **RGB(255,127,127)** 红+白各半 | 对照成立 |

红色混色交叉验证（以 `true` 的左半壁纸色为参照）：
`228 ≈ 255/2 + 200/2`，`103 ≈ 0 + 207/2`，`108 ≈ 0 + 217/2`。

**结论：`SetParent` 到 Progman 子 WorkerW 后，透明度仍然生效。**

截图：[workerw-on2.png](artifacts\workerw-on2.png) / [workerw-off2.png](artifacts\workerw-off2.png)（裁剪对照：[workerw-on-crop.png](artifacts\workerw-on-crop.png)）

### 3.5 额外异常（如实记录）

同轮挂进 WorkerW 的**普通 GDI 绿色对比窗不参与桌面合成**：其区域 `(30,30)` 始终采样为壁纸色 `RGB(193,203,215)`，而不是绿色；但同一颜色窗在阶段 2 的顶层模式正常显示纯绿。把对比窗从 `HWND_BOTTOM` 改到 `HWND_TOP` 后现象不变。

- 含义：WorkerW 桌面层只显示 DComp/XAML 合成内容（以及系统壁纸），普通 GDI 子窗绘制不进入桌面合成。
- 处理：WorkerW 实验改用“桌面壁纸”作为后方参照，并用 ON/OFF 同点差值 + 红色混色算式交叉验证；结论不受影响。
- 该异常也说明：DesktopFlyouts 选择“置顶浮层”而非 WorkerW，可能与此类合成限制有关（此为推断，仓库未给出设计说明）。

---

## 4. WinUI 2 对照实验（Microsoft.UI.Xaml 2.x）

**目标**：在同一套 `DesktopWindowXamlSource` 宿主机制下，把 WinUI 2 控件作为岛内容，验证 `IXamlSourceTransparency` 透明是否同样生效，并按阶段 3 的方式挂到 WorkerW 复测。

**工程**：[src/WinUI2Probe](src/WinUI2Probe)，与 `IslandProbe` 独立共存，复用同一宿主与采样代码。**编译通过**：`Microsoft.UI.Xaml.Controls.NavigationView` 可直接赋给 `DesktopWindowXamlSource.Content`（`Windows.UI.Xaml.UIElement`），说明类型层面兼容，瓶颈只可能出现在运行时激活。

**内容构造**：先合并 `Microsoft.UI.Xaml.Controls.XamlControlsResources`，再 `new Microsoft.UI.Xaml.Controls.NavigationView()`，把与阶段 2 相同的左右两列采样布局设为其 `Content`（左列全透明，右列 50% 红）。失败时不设置任何替代内容，岛保持为空，采样照常执行。

### 4.1 实测（未打包前提）：WinUI 2 内容激活失败

| 步骤 | 实测结果 |
|---|---|
| `XamlControlsResources` 创建/合并 | **失败**：`COMException hr=0x80040154`，`没有注册类 (REGDB_E_CLASSNOTREG)` |
| `new Microsoft.UI.Xaml.Controls.NavigationView()` | **失败**：`COMException hr=0x80040154 (REGDB_E_CLASSNOTREG)` |
| `DesktopWindowXamlSource.AttachToWindow` | 成功 `hr=0x00000000`（宿主层正常建立） |
| `set_IsBackgroundTransparent(true)` | 成功 `hr=0x00000000`，读回 `True` |
| 岛内容 | **未设置**（日志 `[DWS] Content NOT set (WinUI 2 content creation failed)`） |

### 4.2 像素采样（内容缺失状态）

| 模式 | 开关 | 左半 (190,269) | 右半 (450,269) |
|---|---|---|---|
| topmost | true | RGB(0,255,0) 透出绿对照窗 | RGB(0,255,0) 同样透出（无红色内容） |
| topmost | false | RGB(255,255,255) | RGB(255,255,255) |
| workerw | true | RGB(160,196,208) 透出壁纸 | RGB(164,198,210) 透出壁纸（无红色内容） |
| workerw | false | RGB(255,255,255) | RGB(255,255,255) |

**结论**：
- **宿主层透明仍然成立**：同一宿主、同一 `IsBackgroundTransparent` 设置在 `true` 时透出绿底/壁纸，`false` 时变白；WorkerW 挂载 `SetParent` 成功（`lastErr=0`、`hostIsChild=True`）。
- **WinUI 2 内容从未渲染**：右半始终没有出现任何红色区域，因此“WinUI 2 内容自身的透明是否成立”在**未打包前提**下无法作答。这**不是透明失败**，而是 WinUI 2 控件在未打包进程中无法激活；不能把空岛透出背景当作 WinUI 2 透明成功。
- **阶段性结论**：以上是未打包路线的结论；MSIX 打包后已在第 5 节复测，激活失败被消除，透明与渲染结论以第 5 节为准。

### 4.3 失败根因（基于实测证据）

- 本机**已安装** WinUI 2 框架包 `Microsoft.UI.Xaml.2.8 8.2511.26001.0 (x64)`，但本工程是**未打包 Win32 进程**，没有 MSIX 包标识与框架包依赖图，WinRT 激活无法解析该框架包内的可激活类 → `REGDB_E_CLASSNOTREG (0x80040154)`。
- 输出目录只有 `Microsoft.UI.Xaml.Projection.dll`，没有原生 `Microsoft.UI.Xaml.dll`（NuGet 不负责部署框架包）。
- 要让 WinUI 2 内容真正进入这个岛，需将宿主改为 MSIX 打包并对 `Microsoft.UI.Xaml.2.8` 声明依赖。本轮最初为保住“未打包宿主 + WinUI 2”这一单变量前提**未采用**；随后按确认的正式路线改为 MSIX，结果见第 5 节。
- 附带事实：`NavigationView` 与 `Windows.UI.Xaml.UIElement` 的类型兼容性在编译期已验证通过，因此失败点不是类型系统，而是 WinRT 激活。

**证据文件**：`artifacts/WinUI2Probe-topmost-on-20261108-135302.log`、`WinUI2Probe-topmost-off-20261108-135332.log`、`WinUI2Probe-workerw-on-20261108-135357.log`、`WinUI2Probe-workerw-off-20261108-135412.log` 及同名 `.png`。

---

## 5. MSIX 打包后复测（正式路线）

> 第 4 节的“WinUI 2 内容无法渲染”是**未打包前提**下的阶段性结论，不是最终结论。本节按正式路线把 `WinUI2Probe` 改成 MSIX 打包应用（声明 `Microsoft.UI.Xaml.2.8` 框架包依赖），在包标识上下文中重做激活与透明验证。

### 5.1 未打包为什么报 0x80040154：官方文档与本机旁证

**文档明确说明的部分**

- UWP XAML Islands 对 WinUI for UWP Library（WinUI 2）的支持是**有条件的**：

  > “Hosting controls from the WinUI for UWP Library in a XAML Island is supported conditionally in the current release of UWP XAML Islands. If your desktop app uses an MSIX package for deployment, you can host WinUI controls from prerelease or release versions of the Microsoft.UI.Xaml NuGet package. If your desktop app is not packaged using MSIX, you can host WinUI controls only if you install a prerelease version of the Microsoft.UI.Xaml NuGet package, or if you use the Dynamic Dependencies API.”

  出处：Microsoft Learn，*Host UWP XAML controls in desktop apps (UWP XAML Islands)* → “Limitations and workarounds”，<https://learn.microsoft.com/en-us/windows/uwp/xaml-islands/xaml-islands>

- 进程的**包图（package graph）**决定它能使用哪些包内组件：

  > “every process has a package graph … This graph allows the app to load DLLs, content, and run-time class declarations provided by a referenced package.”

  同一文档说明：打包应用的包图按清单中的 `PackageDependency` 在进程创建时初始化；**未打包应用的包图为空**，只能走标准 DLL 搜索顺序，无法访问框架包。
  出处：Microsoft Learn，*MSIX framework packages and dynamic dependencies*，<https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/framework-packages/framework-packages-overview>

- 动态依赖 API 的用途明确包含“激活类”：

  > “If successful, the app might activate classes and use content from the MSIX package.”

  出处：Microsoft Learn，*Use the dynamic dependency API to reference MSIX packages at run time*，<https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/framework-packages/use-the-dynamic-dependency-api>

**本机旁证（事实）**

- WinUI 2 以 **MSIX 框架包**分发：本机已安装 `Microsoft.UI.Xaml.2.8` `8.2511.26001.0 (x64)`。
- 框架包 `AppxManifest.xml` 用 `windows.activatableClass.inProcessServer` + `Microsoft.UI.Xaml.dll` 注册实现类，例如：

  ```xml
  <Path>Microsoft.UI.Xaml.dll</Path>
  <ActivatableClass ActivatableClassId="Microsoft.UI.Xaml.Controls.NavigationView" ThreadingModel="both" />
  <ActivatableClass ActivatableClassId="Microsoft.UI.Xaml.Controls.XamlControlsResources" ThreadingModel="both" />
  ```

- NuGet 包 `Microsoft.UI.Xaml 2.8.7` **不含**原生 `Microsoft.UI.Xaml.dll`：`build/Microsoft.UI.Xaml.props` 只提供 `AppxPackageRegistration`（`Microsoft.UI.Xaml.2.8.appx`，按架构）与 `lib` 下的投影 DLL / `Microsoft.UI.Xaml.winmd` / PRI；构建输出仅出现 `Microsoft.UI.Xaml.Projection.dll`，与第 4 节观察一致。

**推断（非文档逐字表述）**

- 未打包进程对 `Microsoft.UI.Xaml.*` 做 WinRT 激活时，可激活类注册来自“进程包图中的框架包”；包图为空 → 查不到类 → `REGDB_E_CLASSNOTREG (0x80040154)`。官方文档没有逐字写出这个 HRESULT 与该因果链，但“包图提供 run-time class declarations”“未打包包图为空”“动态依赖成功后才能 activate classes”三处文档 + 本机 HRESULT 共同支持该结论。
- MSIX 打包解决的原因：清单声明 `<PackageDependency Name="Microsoft.UI.Xaml.2.8" … />`，安装后进程启动时该框架包进入包图，其可激活类对 WinRT 可见，因此同一份代码不再报 `0x80040154`（5.3 实测）。直接因果句仍是推断，机制要素为文档明确。

**替代方案（文档明确，本次未采用）**：不打包时可用 Dynamic Dependencies API 运行时把框架包加入包图；Windows App SDK 的 DDLM 只为其自身框架包提供生命周期管理器，文档说明 “no other framework packages currently provide a similar lifetime manager component from Microsoft”（<https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deployment-architecture>）。按用户确认的前提，本轮走 MSIX。

### 5.2 打包实现与安装（全自动）

- 清单：[Package.appxmanifest](src/WinUI2Probe/Package.appxmanifest)
  - `Identity Name="WinUI2Probe" Publisher="CN=WinUI2ProbeDev" Version="1.0.0.0" ProcessorArchitecture="x64"`
  - `TargetDeviceFamily Name="Windows.Desktop"`
  - `<PackageDependency Name="Microsoft.UI.Xaml.2.8" MinVersion="8.2501.31001.0" Publisher="CN=Microsoft Corporation, …" />`
  - `Application Executable="WinUI2Probe.exe" EntryPoint="Windows.FullTrustApplication"` + `runFullTrust`
- 脚本：[package-msix.ps1](scripts/package-msix.ps1)：构建 → 组装布局 → 生成 logo → `makeappx pack` → `signtool sign` → 安装/注册 → 写 [msix-install-status.txt](artifacts/msix-install-status.txt)。
- 启动：[run-probe.ps1](scripts/run-probe.ps1) 的 `-Packaged` 通过 `IApplicationActivationManager::ActivateApplication` 按 AUMID 启动并传参，随后自动截图；**无手动点击**。
- 安装结果：
  - 签名 MSIX 安装：**失败** `0x800B0109`（“应用包或捆绑包中的签名的根证书必须是受信任的证书”）。本会话**非管理员**，无法把自签名证书导入 `LocalMachine\TrustedPeople`；仅导入 `CurrentUser\TrustedPeople` 不足以让 `Add-AppxPackage` 信任包签名。
  - 回退：开发者模式（`AllowDevelopmentWithoutDevLicense=1`）下 `Add-AppxPackage -Register artifacts/msix/layout/AppxManifest.xml` **成功**，得到 `PackageFullName=WinUI2Probe_1.0.0.0_x64__rdz54j04exa60`、`AUMID=WinUI2Probe_rdz54j04exa60!App`。
  - 说明：松散注册使用**同一份 AppxManifest 与同一框架包依赖**，包标识、包图与依赖解析真实生效；未验证的仅是“已签名包 + 证书信任”这一安装通道。

### 5.3 WinUI 2 控件激活与渲染（包标识下）

`artifacts/WinUI2Probe-topmost-on-20261108-143135.log` 关键行：

```text
[PKG] Package.Current fullName=WinUI2Probe_1.0.0.0_x64__rdz54j04exa60 family=WinUI2Probe_rdz54j04exa60
[WINUI2] XamlControlsResources merged into Application.Resources
[WINUI2] resource NavigationViewContentBackground => SolidColorBrush color=#80FFFFFF opacity=1
[WINUI2] new NavigationView() OK, type=Microsoft.UI.Xaml.Controls.NavigationView
[WINUI2] InfoBar created, severity=Warning
[WINUI2] NavigationView.Content set (WinUI 2 control as island root)
[SAMPLE] winui2_infobar (480,90) COLORREF=0x00CEF4FF RGB=(255,244,206)
```

- `0x80040154` 不再出现；`XamlControlsResources`、`NavigationView`、`InfoBar` 全部创建成功。
- InfoBar 右上留白像素 `RGB(255,244,206)` = `#FFF4CE`（WinUI 2 Warning 语义背景），证明内容不是空岛，确实由 `Microsoft.UI.Xaml` 渲染。

### 5.4 透明复测（2 模式 × 开关 + 内容对照）

主实验：岛根为 `NavigationView`（`Background=Transparent`），内容是顶部 `InfoBar` + 原两列判据布局。
对照诊断：同一打包上下文改用 `--content grid`（原判据布局、无 WinUI 2 控件），用于区分“岛级透明”与“控件自带底色”。

| 内容 | 模式 | `transparent` | 左半 (190,269) | 右半 50% 红 (450,269) | InfoBar (480,90) |
|---|---|---|---|---|---|
| WinUI 2 | topmost | `1` | **RGB(128,255,128)** | **RGB(192,127,64)** | RGB(255,244,206) |
| WinUI 2 | topmost | `0` | RGB(255,255,255) | RGB(255,127,127) | RGB(255,244,206) |
| WinUI 2 | workerw | `1` | **RGB(246,234,232)** | **RGB(241,114,116)** | RGB(255,244,206) |
| WinUI 2 | workerw | `0` | RGB(255,255,255) | RGB(255,127,127) | RGB(255,244,206) |
| grid 对照 | topmost | `1` | **RGB(0,255,0)** | **RGB(128,127,0)** | — |
| grid 对照 | workerw | `1` | **RGB(237,213,209)** | **RGB(226,101,104)** | — |

判据与验算：

- **岛级透明仍然成立**。`grid` 对照在包标识下严格复现阶段 2/3：topmost 左 = 纯绿；WorkerW 左 = 启动前桌面基准图同点壁纸色 `RGB(237,213,209)`，右 = `50% 红 + 壁纸`（`226 ≈ 255/2 + 196/2`、`101 ≈ 203/2`、`104 ≈ 209/2`，误差 ≤1）。
- **WinUI 2 内容真实渲染**（5.3）。
- 以 `NavigationView` 为根时不是纯背景色：其内容区背景资源 `NavigationViewContentBackground = #80FFFFFF`（5.3 日志），即控件模板自带的 **50% 白**层。混色验算：
  - topmost `1`：`(128,255,128) ≈ 0.5×255 + 0.5×(0,255,0)`；右 `(192,127,64) ≈ 0.5×(255,0,0) + 0.5×(128,255,128)`。
  - workerw `1`：桌面基准 `(237,213,209)` 叠 50% 白预测 `(246,234,232)`，实测**逐通道一致**；右预测 `≈(240,114,116)`，实测 `(241,114,116)`。
  - `0` 时两模式均为不透明白底，右半为 50% 红叠白，与阶段 2 一致。
- 因此：**透明开关生效**（绿底/壁纸确实透出），但**不能声称“WinUI 2 内容完全透明”**——`NavigationView` 模板的 `#80FFFFFF` 内容底色是控件行为，不是透明失效。若需纯透明观感，需在内容侧把该模板背景改为透明（属内容设计，本轮未改，以免用改主题资源的方式制造“全透明”结论）。

### 5.5 打包上下文下的 SetParent（如实记录）

无新限制或行为差异：`SetParent` 到 `Progman` 子 `WorkerW` 成功（`lastErr=0`）、`hostIsChild=True`；壁纸采样与启动前桌面基准图同点一致。与未打包时相同的既有差异仍是“普通 GDI 绿色对照窗不参与 WorkerW 合成”（3.5），故本轮同样用**桌面基准 + 混色算式**，不依赖绿窗观察。

### 5.6 本轮限制与剩余待验证项

- **签名 MSIX 正式安装未完成**：需要管理员把证书放入 `LocalMachine\TrustedPeople`（或用受信任证书/商店签名）。本轮已记录 `0x800B0109`，并以开发模式松散注册完成包标识验证。
- 未测试 Dynamic Dependencies API 替代路径（WinUI 2 无 DDLM，需 Windows 11 22H2+ 的 `AddPackageDependency`）。
- 未改动 `NavigationViewContentBackground`，因此“WinUI 2 内容完全透明”的观感未验证；本轮只判定透明开关语义与控件渲染。
- 打包后仍以 `DesktopWindowXamlSource` + `WS_EX_NOREDIRECTIONBITMAP` 宿主；未测试在其他宿主形态（如 WPF/WinForms 岛）中的同样组合。

---

## 6. Frame + Page 导航扩展性验证（新增）

> 目的：确认岛里可以直接用 `Windows.UI.Xaml.Controls.Frame` + `Page` 做页面导航，为后续多页面（文件视图、设置页等）提供依据。
> 工程：仍为 [src/WinUI2Probe](src/WinUI2Probe)，新增 `--content nav`；页面为 [PageA.xaml](src/WinUI2Probe/PageA.xaml) / [PageB.xaml](src/WinUI2Probe/PageB.xaml)（XAML 编译），代码见 [NavigationPages.cs](src/WinUI2Probe/NavigationPages.cs)。
> 判据：页面顶部 40 DIP 色带（PageA=DodgerBlue `RGB(30,144,255)`，PageB=OrangeRed `RGB(255,69,0)`）证明当前页面真的渲染；中部左列保持透明、右列 50% 红沿用原像素判据；底部状态区用于返回后状态检查。

### 6.1 实现方式

```csharp
var frame = new Windows.UI.Xaml.Controls.Frame();
frame.Navigate(typeof(PageA), "param-A");   // 返回 true
_dws.Content = frame;                        // Frame 直接作为岛根
```

自动化序列（`--content nav`，无需手动点按钮）：初始采样 → 修改 PageA 状态（TextBox 文本 + `ChangeView` 滚到 120）→ `Navigate(PageB)` 采样 → `GoBack()` 采样 → 放大宿主（640x440）验证 Frame 尺寸同步。

### 6.2 导航、栈与渲染（打包，`artifacts/WinUI2Probe-topmost-off-20261108-151126.log`）

```text
[NAV] Navigate(PageA) => True content=WinUI2Probe.PageA backStack=0 canGoBack=False
[NAV] Navigate(PageB) => True content=WinUI2Probe.PageB backStack=1 canGoBack=True
[NAV] GoBack() called; content=WinUI2Probe.PageA backStack=0 canGoBack=False
[PAGE-A] OnNavigatedTo #2 mode=Back param='param-A' Frame=Windows.UI.Xaml.Controls.Frame
```

| 阶段 | `Frame.Content` | `BackStackDepth` | `CanGoBack` | `nav_marker (320,80)` |
|---|---|---|---|---|
| 初始 | PageA | 0 | False | **RGB(30,144,255)**（PageA 色带） |
| 导航到 B | PageB | 1 | True | **RGB(255,69,0)**（PageB 色带） |
| GoBack | PageA | 0 | False | **RGB(30,144,255)**（回到 PageA） |

- `Navigate` 返回 `True`，`Navigated` 事件中 `contentType` 与页面类型一致；页面真实渲染（色带像素与页面身份一一对应），不是只看返回值。
- 返回时 `NavigationMode=Back`，`BackStackDepth` 0→1→0 正常。
- 观察到的回调顺序：`Navigated(新页)` → 旧页 `OnNavigatedFrom` → 新页 `OnNavigatedTo`。

### 6.3 返回后的状态保留：默认不保留（重要坑）

| 运行 | `NavigationCacheMode` | 阶段 | PageA 实例数 | TextBox 文本 | 滚动偏移 |
|---|---|---|---|---|---|
| 默认 | `Disabled` | 导航前 | 1 | `A0-mutated` | 120.0 |
| 默认 | `Disabled` | 返回后 | **2（新实例）** | **`state`（初始值）** | **0.0** |
| `--navcache 1` | `Required` | 导航前 | 1 | `A0-mutated` | 120.0 |
| `--navcache 1` | `Required` | 返回后 | **1（复用实例）** | **`A0-mutated`** | **120.0** |

- 默认 `Disabled` 时，`Frame` 的后退栈保存的是“页面类型 + 参数”（`PageStackEntry`），**不保存页面实例**；`GoBack` 会重新构造 `PageA`（日志 `[PAGE-A] ctor instance=#2`），因此控件内容与滚动位置都回到初始值。这与文档对 `NavigationCacheMode` 的说明一致：需要缓存页面实例时应显式设置 `Required`/`Enabled`。
- 设 `Required` 后同一实例被复用，`OnNavigatedTo` 仍会再次触发（计数 2），状态保留。
- 实践含义：多页面应用不能依赖默认的页面实例状态；要么开启缓存，要么把状态放到 ViewModel/服务里（推荐后者，避免缓存与内存的副作用）。

### 6.4 导航前后透明是否仍成立

每次采样都在同一坐标（`(190,269)` 左透明区、`(450,269)` 50% 红区），覆盖导航全阶段。

| 模式 / 开关 | 阶段 | 左透明区 | 右半 50% 红 | 结论 |
|---|---|---|---|---|
| topmost / `1` | 初始 / 到 B / 返回 | **RGB(0,255,0)** | **RGB(128,127,0)** | 三阶段完全一致，绿对照窗透出 |
| topmost / `0` | 初始 / 到 B / 返回 | RGB(0,0,0) | RGB(128,0,0) | 三阶段一致，不透明（底色随主题） |
| workerw / `1` | 初始 / 到 B / 返回 | **RGB(3,14,32)** | **RGB(132,12,25)** | 与启动前桌面基准同点逐通道一致 |
| workerw / `0` | 初始 / 到 B / 返回 | RGB(255,255,255) | RGB(255,127,127) | 三阶段一致，不透明 |

workerw 的交叉验算（同一轮启动前桌面基准图同点：左 `(3,14,32)`、右 `(8,24,50)`）：

- 左：`(3,14,32)` 与基准**逐通道相同** → 壁纸真实透出。
- 右：`0.5×红 + 0.5×基准` = `(131.5, 12, 25)`，实测 `(132,12,25)`，误差 ≤1。

**结论：Frame/Page 切换不会破坏 `IXamlSourceTransparency` 的效果**；透明开关在导航全阶段保持。

### 6.5 尺寸同步与资源解析

- **Frame 尺寸随岛同步**：初始 `ActualWidth/ActualHeight = 346.7x253.3` DIP，与岛物理尺寸 520x380 @150% 换算值一致；把宿主放大到 640x440 后变为 `426.7x293.3`，`Frame.SizeChanged` 共 2 次（0→346.7→426.7）。
- **页面级 `ResourceDictionary`**：`PageA.Resources["PageAOnlyBrush"]` 解析为 `SolidColorBrush #FF1E90FF`（XAML 中定义）。
- **应用级主题资源**：`ApplicationPageBackgroundThemeBrush` 可解析，但取值随当前主题/上下文变化——打包运行观察到 Light `#FFF3F3F3` 与 Dark `#FF202020` 两种；未打包运行（无 `XamlControlsResources`）为 `#FF000000`。说明**主题资源查找本身可用**，具体颜色由活动主题决定，不能把它当作固定常量。
- 页面 XAML 默认样式（TextBox 等）正常套用，说明页面级的主题字典查找链在岛内有效。

### 6.6 常见坑与失败项（如实记录）

1. **纯 C# Page 类型（未进入 XamlTypeInfo）会让导航崩溃**：第一次实现把 `PageA/PageB` 写成纯 C# 类（不编译 XAML），`Navigate` 时进程直接退出，日志停在 `[NAV] Navigating to ...`，**没有托管异常，也没有错误码**。改为 XAML 编译页面（`PageA.xaml`/`PageB.xaml` + `x:Class`，类型进入生成的 `XamlTypeInfo.g.cs`）后正常。  
   推断（非文档逐字）：`Frame.Navigate(Type)` 需要 XAML 元数据提供者认识目标类型；没有 XAML 元数据时在原生层失败。**扩展新页面时务必用 XAML 编译**（或确保类型进入 `IXamlMetadataProvider`）。
2. **UWP `Page` 没有 `NavigationService` 属性**：反射检查 `typeof(Page).GetProperty("NavigationService")` 返回 `null`（日志 `exists=False`）。WPF/Silverlight 的 `NavigationService` 概念在 UWP 中对应 `Page.Frame`；`OnNavigatedTo` 时 `Frame` 已可用、`BackStackDepth`/`CanGoBack` 可读。
3. **`GoBack()` 返回 `void`**（不是 `Navigate` 那样返回 `bool`），只能通过 `Content`/`BackStackDepth`/`CanGoBack` 判断结果；本探针据此记录。
4. **默认返回会重建页面**（6.3）：需要状态保留时必须显式缓存或外置状态。
5. **`Window.Current` 在本宿主中非 null**：日志显示 `Window.Current=Window`（`WindowsXamlManager` + `CoreWindow` 路径）。因此“完全没有 `Window.Current` 的岛宿主”未能在本环境验证；`Frame`/`Page` 导航本身不依赖 `Window.Current` 的显式使用，但该场景属剩余待验证项。
6. **未打包上下文**：同一份 Frame/Page 代码可正常导航与渲染（`artifacts/WinUI2Probe-topmost-on-20261108-151238.log`，标记 `[PKG] no package identity`），导航栈与状态行为一致；差异仅在 WinUI 2 主题资源仍报 `0x80040154`（与第 4/5 节一致），主题资源回退为 OS 默认值。**Frame/Page 属系统 UWP XAML，不依赖 MSIX 包标识**。

### 6.7 局限

- 页面内未做输入/焦点/命中测试；WorkerW 子窗的输入行为仍未验证（与 5.5/第 9 节同一限制）。
- 未测试 `Frame.Navigate` 到“无效页面类型”的失败码；未测试多 Frame/多岛并发导航。
- 未测试 `Frame` 的非默认缓存策略（`Enabled`）、`NavigationTransition`、页面 `OnNavigatingFrom` 取消等高级行为。
- 截图在放大宿主之后拍摄，像素判定以日志中的分阶段采样为准（截图仅作辅助）。

---

## 7. 复现方式

```powershell
# 1) 构建
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "文档/首次可行性验证/src/IslandProbe/IslandProbe.csproj" `
  /restore /p:Platform=x64 /p:Configuration=Debug

# 1b) 构建 WinUI 2 对照工程
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  "文档/首次可行性验证/src/WinUI2Probe/WinUI2Probe.csproj" `
  /restore /p:Platform=x64 /p:Configuration=Debug

# 2) 运行（脚本会自动截图；workerw 模式会自动最小化窗口并在结束时还原）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Mode topmost -Transparent on

powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Mode workerw -Transparent on

# 2b) WinUI 2 对照（-Probe 默认 IslandProbe）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode workerw -Transparent on

# 3) WinUI2Probe 打包成 MSIX 并安装（签名失败时自动回退开发模式注册）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/package-msix.ps1"

# 4) 以包身份运行（自动激活、传参、截图）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode topmost -Transparent on -Packaged

# 4b) 对照诊断：同一包身份，岛内容退回原判据布局
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode topmost -Transparent on -Content grid -Packaged

# 4c) Frame + Page 导航（自动 A→B→返回、分阶段采样；可加 -NavCache 做页面缓存对照）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode topmost -Transparent on -Content nav -Packaged

powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode workerw -Transparent on -Content nav -Packaged

# 5) WinUI2Demo（可交互 demo，含工作区）
pwsh -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/package-msix.ps1" `
  -ProjectName WinUI2Demo -PackageName WinUI2Demo

# 交互启动（默认 --fill work，即铺满当前显示器的工作区）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-demo.ps1" -Mode desktoppane -Transparent on -Fill work

# 6) 嵌入态输入回归（UIA 定位 + 真实 mouse_event；topmost / desktoppane / workerw）
pwsh -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/test-input-uia.ps1" -Mode desktoppane

# 7) 工作区铺满范围 + 任务栏遮挡（像素 + WindowFromPoint 双证据，四组组合）
pwsh -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/test-workspace.ps1"

探针参数：`--mode topmost|plain|workerw`、`--content winui2|grid|nav`、`--transparent 1|0`、`--log <path>`、`--hold <ms>`、`--capture <ms>`、`--navcache 1`（仅 nav，PageA 用 `NavigationCacheMode.Required`）。`run-probe.ps1` 的 `-Packaged` 表示按已注册包的 AUMID 启动，而不是直接执行 exe。

---

## 8. 证据文件

| 文件 | 内容 |
|---|---|
| `artifacts/topmost-on.log` / `topmost-off.log` | 阶段 2 的 IID QI、`hr`、读回值、像素采样 |
| `artifacts/workerw-on.log` | 第一次 WorkerW 失败（`ERROR_INVALID_PARAMETER 87`） |
| `artifacts/workerw-on2.log` / `workerw-off2.log` | 修正后的 WorkerW 成功复测 |
| `artifacts/*.png` | 4 组截图与 700x500 裁剪图 |
| `artifacts/WinUI2Probe-*.log` / `*.png` | WinUI 2 对照实验（未打包 4 组含 `0x80040154`；打包后 4 组 + `grid` 对照） |
| `artifacts/WinUI2Probe-workerw-*-desktop.png` | WorkerW 模式启动前的无遮挡桌面基准图（透明判定用） |
| `artifacts/WinUI2Probe-topmost-on-20261108-150218.log` | 首次失败证据：纯 C# Page（未进 `XamlTypeInfo`）在 `Navigating` 后进程退出，无托管异常 |
| `artifacts/WinUI2Probe-topmost-on-20261108-150810.log` | Frame/Page 导航（打包，默认 `NavigationCacheMode=Disabled`，透明开） |
| `artifacts/WinUI2Probe-topmost-off-20261108-151126.*` | 导航矩阵：topmost + 不透明（A→B→返回三阶段采样与状态） |
| `artifacts/WinUI2Probe-topmost-on-20261108-151144.*` | 导航矩阵：topmost + 透明开 + `NavigationCacheMode=Required` |
| `artifacts/WinUI2Probe-workerw-on-20261108-151203.*` / `-desktop.png` | 导航矩阵：WorkerW + 透明开 + 桌面基准 |
| `artifacts/WinUI2Probe-workerw-off-20261108-151224.*` / `-desktop.png` | 导航矩阵：WorkerW + 不透明 |
| `artifacts/WinUI2Probe-topmost-on-20261108-151238.*` | 未打包上下文下的 Frame/Page 导航对照（无包标识） |
| [src/WinUI2Probe/PageA.xaml](src/WinUI2Probe/PageA.xaml) / [PageB.xaml](src/WinUI2Probe/PageB.xaml) / [NavigationPages.cs](src/WinUI2Probe/NavigationPages.cs) | 导航验证页与代码（XAML 编译，顶部色带 + 透明判据 + 状态区） |
| [artifacts/msix-install-status.txt](artifacts/msix-install-status.txt) | 打包/安装结果：松散注册、包标识、签名失败原因 |
| [src/WinUI2Probe/Package.appxmanifest](src/WinUI2Probe/Package.appxmanifest) | MSIX 清单（含 `Microsoft.UI.Xaml.2.8` 框架包依赖） |
| [scripts/package-msix.ps1](scripts/package-msix.ps1) | 打包/签名/安装脚本 |
| `artifacts/WinUI2Demo-{topmost,desktoppane,workerw}-uia-20261108-1701*.result.txt` | 嵌入态输入回归：UIA + 真实点击，命中类名/pid 与 PASS/FAIL |
| `artifacts/workspace-20261108-170057.result.txt` / `-baseline.png` / `workspace-<mode>-<fill>-20261108-170057.*` | 工作区四组组合的日志、截图与判定（`failures=0`） |
| `artifacts/WinUI2Demo-*-transparent{on,off}-20261108-1703*.png/.log` + `desktop-visible-*.png` | 新布局下透明矩阵与桌面基线（10.5） |
| [scripts/test-input-uia.ps1](scripts/test-input-uia.ps1) / [scripts/test-workspace.ps1](scripts/test-workspace.ps1) | 输入回归与工作区双证据脚本 |
| [LEDGER.md](LEDGER.md) | 分阶段台账（含环境、行号、失败与结论） |

---

## 9. 局限与不确定项

- 结果基于本机 Windows 11 Build 26300 / 150% DPI / 单屏 / 交互式会话；Win10 的 WorkerW 层级不同，WorkerW 部分需按当地窗口树重测。
- 未使用任何 DWM 属性，且实测证明不需要；但未测试“有其他桌面工具（壁纸引擎、动态壁纸）同时挂 WorkerW”时的冲突。
- 输入/焦点/命中测试已在第 10 节用 UIA 真实点击实测：WorkerW 子窗的点击被 `SHELLDLL_DefView` 接管（4/4 失败）；改用 Progman 子窗 + DefView 之上 Z 序后 4/4 通过。
- `IsBackgroundTransparent` 按官方文档对**当前线程所有** `DesktopWindowXamlSource` 生效；工程中每个进程只有一个岛，未做多岛对照。
- WorkerW 中普通 GDI 子窗不参与合成的原因只做了现象级定位（DComp 内容可见、GDI 内容不可见），未进一步下钻 DWM/桌面合成内部实现。
- WinUI 2 对照实验最初只证明了"未打包宿主无法激活 WinUI 2 控件"；MSIX 打包后已补充验证激活与渲染（第 5 节）。仍未验证的是**已签名 MSIX 的正式安装通道**（受管理员/证书信任限制），以及把 `NavigationViewContentBackground` 改为透明后的纯透明观感。
- Frame/Page 导航未覆盖：无 `Window.Current` 的岛宿主、无效页面类型的失败码、`Frame` 的 `Enabled` 缓存策略与转场、页面 `OnNavigatingFrom` 取消、多岛/多 Frame 并发。页面内输入已由第 10 节的 UIA 真实点击覆盖；WorkerW 子窗的交互已实测为失效（10.3）。

---

## 10. 嵌入态输入修复（本轮新增）

> 目标：修“`SetParent` 进壁纸层后点不动”。先联网调研根因与成熟方案，再到本机 demo 用 **UIA 定位 + `mouse_event` 真实点击**逐条验证（不以代码自评“应该能点”为结论），同时用同一套像素采样确认透明未被破坏。

### 10.1 调研：输入失效的各种根因与对应修法

| # | 根因 | 机制 | 对应修法 | 依据性质 |
|---|---|---|---|---|
| 1 | 命中被桌面图标视图截获 | `SHELLDLL_DefView` 是全屏视图，通常位于 `WorkerW` 之上；挂在 `WorkerW` 里的子窗在 Z 序上永远低于它，`WindowFromPoint` 返回 `SHELLDLL_DefView`，宿主收不到鼠标消息 | 挂 `Progman` 子窗并把宿主 Z 序放到 `DefView` 之上；或改用置顶浮层不进入桌面树 | **本机实测**（本机枚举 + 命中诊断）+ 社区经验；Progman/WorkerW 层级无官方文档（Raymond Chen 的 *The Old New Thing* 有讨论） |
| 2 | 父窗否决激活 | 官方文档：`DefWindowProc` 在处理前把子窗的 `WM_MOUSEACTIVATE` 交给父窗，由父窗决定是否激活；桌面父窗通常不激活普通子窗 | 宿主自己处理 `WM_MOUSEACTIVATE` 并返回 `MA_ACTIVATE` | **官方文档**（WM_MOUSEACTIVATE 备注） |
| 3 | 跨线程/跨进程父子窗的输入状态 | 官方：`AttachThreadInput` 让两个线程共享输入状态（键盘状态、当前焦点窗）；跨线程 `SetParent` 合法，但不会自动共享输入队列 | `AttachThreadInput` 到 explorer UI 线程（社区做法）；本机改用路线 C，未使用它 | **官方文档**（AttachThreadInput）+ 官方博客（跨进程父子窗“合法但有代价”） |
| 4 | `WS_EX_NOACTIVATE` | 官方：窗口被点击时不会成为前台，但仍会收到鼠标消息；适合“永不激活”的浮层，不适合需要键盘/焦点的交互控件 | 交互组件不要加；若必须加，需自己转发激活/焦点 | **官方文档**（扩展窗口样式） |
| 5 | `WS_EX_TRANSPARENT` | 官方：普通窗口的 `WS_EX_TRANSPARENT` 只影响绘制顺序（要求先绘兄弟），**不影响命中测试**；只有 **分层窗口**（`WS_EX_LAYERED`）带该样式时才会忽略形状、把鼠标事件传给下层。要“点穿”应返回 `HTTRANSPARENT`（`WM_NCHITTEST`） | 静态壁纸才用点穿；交互 demo 必须接收点击，故禁用 | **官方文档**（分层窗口/扩展样式/WM_MOUSEACTIVATE 相关文档），与常见社区误传“WS_EX_TRANSPARENT 让点击穿透”不同 |
| 6 | Win11 桌面合成 / DWM | 桌面窗口树不是公开 API；本机阶段 3.5 实测：`WorkerW` 中普通 GDI 子窗不参与桌面合成 | 使用 DComp/XAML 宿主，并解决 Z 序（路线 C） | **本机实测 + 社区经验**（无官方文档） |
| 7 | 样式与 Z 序 | 官方：`SetParent` 不改变 `WS_CHILD/WS_POPUP`；改样式后要 `SetWindowPos(SWP_FRAMECHANGED)`；Z 序用 `SetWindowPos` 调整 | 路线 C 中每次挂父/切模式后重设样式并 `SetWindowPos(HWND_TOP, NOACTIVATE)` | **官方文档**（SetParent / SetWindowPos） |

成熟方案对照（用于判断哪些做法可复用）：

| 方案 | 做法 | 输入表现 | 依据性质 |
|---|---|---|---|
| Wallpaper Engine（闭源） | 社区广泛描述为把渲染窗口挂到 `WorkerW`，壁纸本身不要求交互 | 壁纸层不接收点击；桌面图标仍可点 | 社区经验（无源码/官方说明） |
| Lively Wallpaper（开源） | `WinDesktopCore.cs` 中 `SetParent(handle, workerW)`，并在“raised desktop”模式下用 `SetWindowPos` 调整层序（源码索引可查）；产品 FAQ 宣称交互式壁纸支持鼠标输入 | 说明“能否点”取决于该产品在桌面层的窗口/合成选择，而不是 `SetParent` 本身 | 开源代码索引 + 产品 FAQ |
| Rainmeter（桌面挂件） | 官方手册提供 “On Desktop” 位置语义（置于桌面、被普通窗口遮挡）；具体实现未在手册展开 | 桌面挂件可交互是其产品目标，但底层细节不可作为可复用文档 | 官方手册（语义）+ 社区经验（实现） |
| 微软官方示例 DesktopFlyouts | 不用 WorkerW，走**置顶透明浮层**（阶段 1/3.1 已考据） | 输入天然正常，代价是盖住普通窗口/任务栏 | **官方仓库** |

### 10.2 本机实测：UIA 真实点击

判定方式：UIA 按名称找控件 → 光标移到 `BoundingRectangle` 内 → `mouse_event` 左键按下/抬起 → 以 demo 日志里的真实事件（`[NAV]`/`[DEMO]`）判定。每一步同时记录点击坐标下方窗口的类名与 pid。

| 模式 | 导航 | 透明开关 | 铺满桌面开关 | 退出 | 点击下方窗口 | 结果文件 |
|---|---|---|---|---|---|---|
| topmost | PASS | PASS | PASS | PASS | 本进程 `WinUI2DemoHost` | `artifacts/WinUI2Demo-topmost-uia-20261108-171957.result.txt` |
| desktoppane（路线 C） | PASS | PASS | PASS | PASS | 本进程 `WinUI2DemoHost`，`root=Progman 0x1015C` | `artifacts/WinUI2Demo-desktoppane-uia-20261108-172121.result.txt` |
| workerw（旧路线） | FAIL | FAIL | FAIL | FAIL | `SHELLDLL_DefView 0x10160 pid=8812`（explorer） | `artifacts/WinUI2Demo-workerw-uia-20261108-170219.result.txt` |

- 路线 C 的宿主在 `Progman` 子窗 Z 序最前（`Progman → SHELLDLL_DefView / WorkerW / WinUI2DemoHost`），所以点击真实命中宿主；日志同时有 `[HITTEST]` 与 `[INPUT] WM_MOUSEACTIVATE => MA_ACTIVATE`。
- workerw 模式下 4 个采样点全部命中 `SHELLDLL_DefView`，demo 日志没有任何 `[NAV]`/`[DEMO]` 事件，证明“消息根本没到宿主线程”，而不是“到了但 XAML 没反应”。
- **首启时序（如实记录）**：重新注册包后的首次启动（17:17 轮）出现过 nav/quit 在 4 秒等待内没反应的失败，回看日志发现点击消息被延迟约 11 秒才由 XAML 处理（不是丢失）；同一包热启动复跑为 4/4，最终干净回归（17:19/17:21）为 4/4。脚本因此加了“先 `SetForegroundWindow` 并等待前台 + 首步等待 12 秒 + 失败补点一次”，并在每次点击前最小化其他普通窗口。未定位到首启延迟的系统侧确切原因，**仅记录现象，不作为修复结论**。

### 10.3 失效路线与现象（如实记录）

| 路线 | 现象 / 错误码 | 结论 |
|---|---|---|
| 挂错父窗（顶层同名 `WorkerW`） | `SetParent` 返回 `lastErr=87`（`ERROR_INVALID_PARAMETER`），窗口被改成无父的 `WS_CHILD` 后不再显示（阶段 3.3 历史证据） | 必须用 `Progman` 的子 `WorkerW`，否则连显示都不成立 |
| 挂对 `WorkerW`（壁纸层） | `SetParent` 成功（`lastErr=0`），但点击 4/4 失败；命中 `SHELLDLL_DefView` | 透明成立、输入不成立；该路线不适合交互 |
| `WS_EX_TRANSPARENT` / 返回 `HTTRANSPARENT` | 会把鼠标事件交给下层 | 与“要接收点击”目标相反，未采用（未实测，仅按官方语义排除） |
| `WH_MOUSE_LL` 低层鼠标钩子转发 | 社区备选路线 | 未实现、未实测；风险（UIPI/注入、坐标与 DPI、钩子超时）仅停留在文献层面，不作为结论 |
| `AttachThreadInput` 共享输入队列 | 社区常见做法 | 本机路线 C 不需要，未实测；不作为推荐 |
| 不挂父、仅置顶（路线 E） | 可点击，但铺满 `rcMonitor` 时盖住任务栏（10.2 的 topmost 行 + 11.4 的 `taskbarStrip`） | 可用但不符合“桌面层”定位；作为对照保留 |

### 10.4 推荐方案与理由

**路线 C：`SetParent` 到 `Progman`，宿主 Z 序放到 `SHELLDLL_DefView` 之上；保留 `WS_EX_NOREDIRECTIONBITMAP`；`WM_MOUSEACTIVATE` 返回 `MA_ACTIVATE`。**

理由：

1. 本机 UIA 4/4 通过，重复两轮稳定；透明像素与置顶模式一致（10.5）。
2. 不引入全局钩子/`AttachThreadInput`，跨进程副作用最小；只依赖“桌面窗口树 + Z 序”这一事实。
3. 失败可诊断：`[DESK]`/`[HITTEST]` 日志能直接指出命中落在 `SHELLDLL_DefView` 还是宿主。

代价（已实测/已记录）：

- 宿主矩形内的桌面图标不再能被点到（透明区域命中归宿主）。这是“可交互 + 在图标之上一层”的必然取舍。
- 每次切换“置顶/嵌入”后都要重新套用布局（子窗坐标是父客户区坐标，可能需一次偏移校正）。
- Win10 或其他 Build 的桌面窗口树可能不同，上线前需按本机方式重测（第 9 节限制不变）。

### 10.5 透明未被破坏（同一套像素复测）

采样点（实现修正后窗口铺满工作区 `(0,0,1920,1008)`，144 DPI）：`(200,300)` 左半透明区、`(1400,300)` 右侧 50% 红、`(700,20)` 顶部控制条（半透明 `#CC1F1F1F`，**不作为壁纸透出判据**，只记录）。

| 模式 | 透明 | (200,300) | (1400,300) | 桌面基线同点 | 判定 |
|---|---|---|---|---|---|
| topmost | on | `RGB(4,15,33)` | `RGB(193,5,12)` | `RGB(4,15,33)` / `RGB(131,11,24)` | 左点与基线逐通道一致；右点 `193 ≈ 0.5×255 + 0.5×131`、`5 ≈ 0.5×0 + 0.5×11`、`12 ≈ 0.5×0 + 0.5×24` |
| topmost | off | `RGB(0,0,0)` | `RGB(128,0,0)` | 同上 | 不透明对照成立 |
| desktoppane | on | `RGB(4,15,33)` | `RGB(193,5,12)` | 同上 | 与 topmost 完全一致 |
| desktoppane | off | `RGB(0,0,0)` | `RGB(128,0,0)` | 同上 | 不透明对照成立 |

证据：`artifacts/WinUI2Demo-topmost-transparenton-20261108-170330.*`、`...-transparentoff-20261108-170342.*`、`artifacts/WinUI2Demo-desktoppane-transparenton-20261108-170354.*`、`...-transparentoff-20261108-170410.*`（含截图与日志）；基线 `artifacts/desktop-visible-20261108-170330.png` 等。

> 说明（未完成项）：以上矩阵是在“工作区/输入修复后、最终 Release 重出包前”的构建上采集的。最终包只改了两处：`BuildWorkspaceText` 中“置顶态是否盖住任务栏”的文案改为按真实命中输出、测试脚本（不属于产物）。透明相关代码未改动。按用户要求停止新增测试，最终 Release 上**未再重跑**透明矩阵；一次尝试因截图被 DPI 缩放为 1280×720、采样点 `(1400,300)` 越界而中止，未采到完整数据，**不用于结论**。

---

## 11. 工作区检测与铺满范围（本轮新增）

> 目标：用 `MonitorFromWindow` + `GetMonitorInfo` 取 `rcMonitor`/`rcWork`，与 `SystemParametersInfo(SPI_GETWORKAREA)` 对照；界面可观测；并用截图/命中证明“铺满工作区还是整个桌面”，而不是只报窗口矩形。

### 11.1 实现

- 显示器：`EnumDisplayMonitors` 逐块取 `MONITORINFOEXW`（`rcMonitor`、`rcWork`、`dwFlags`、`szDevice`），`GetDpiForMonitor(MDT_EFFECTIVE_DPI)` 取每块屏 DPI；当前屏用 `MonitorFromWindow(host, MONITOR_DEFAULTTONEAREST)` 匹配。
- 对照：`SPI_GETWORKAREA` 取系统工作区；`ABM_GETTASKBARPOS` 取任务栏边与矩形；日志逐项比较 `equalCurrentRcWork`/`equalPrimaryRcWork`。
- 布局基准：默认 **当前显示器的 `rcWork`**，可用 `--fill desktop`（`rcMonitor`）/`--fill none`（固定小窗对照）切换；界面“铺满桌面”开关对应同一逻辑。
- 刷新：`WM_SETTINGCHANGE`（`wParam=SPI_SETWORKAREA`）、`WM_DISPLAYCHANGE`、`WM_DPICHANGED` 各自重新 `ApplyLayout` + `UpdateWorkspace`。
- 坐标校正：挂到 `Progman` 后 `SetWindowPos` 的 x/y 是父客户区坐标，写入后用 `GetWindowRect` 复核，按差值再校正一次（日志 `parentOffsetCorrection`）。
- 界面输出：底部 `WorkspaceText` 显示显示器名/主副屏/像素与 DIP、`rcMonitor`/`rcWork`、`SPI_GETWORKAREA` 对照、任务栏边、布局判定与可见范围说明。

### 11.2 为什么 `SPI_GETWORKAREA` 与 per-monitor `rcWork` 可能不一致（官方语义 + 推断）

1. **多显示器**：`SPI_GETWORKAREA` 只返回**一个** RECT（主屏工作区，屏幕坐标），无法表达副屏任务栏/停靠栏造成的每屏差异；`GetMonitorInfo` 的 `rcWork` 是**逐显示器**的，副屏有任务栏时二者必然不同。官方文档明确 `SPI_GETWORKAREA` 是“工作区”单矩形。
2. **任务栏/AppBar**：`rcWork` 扣除了该屏上的任务栏与桌面工具栏；`SPI_GETWORKAREA` 只反映主屏。任务栏换到左/上时，主屏 `rcWork` 的 Left/Top 会变，但副屏不会。
3. **DPI 缩放**：`rcMonitor`/`rcWork` 是物理像素；`SPI_GETWORKAREA` 对 **DPI-unaware** 调用方会做系统级虚拟化（返回虚拟坐标），对 PerMonitorV2 调用方为物理像素。本机 demo 是 PerMonitorV2、144 DPI，两者数值恰好一致；换到 DPI-unaware 宿主或混合 DPI 多屏时可能对不上。**（具体换算规则文档未逐字展开，此处标注为推断，但“DPI 虚拟化会让坐标口径不同”是官方明确的行为框架。）**
4. **结论**：布局基准取 `MonitorFromWindow` + `GetMonitorInfo` 的 `rcWork`——它跟随窗口所在屏、能表达副屏任务栏、且与 `GetDpiForMonitor` 的 DIP 换算同源；`SPI_GETWORKAREA` 仅作交叉校验与诊断输出。

### 11.3 本机实测数据

```
monitor[1/1] device=\\.\DISPLAY1 primary=True dpi=144x144
rcMonitor=(0,0,1920,1080)px  rcWork=(0,0,1920,1008)px
dipMonitor=(0,0,1280,720)    dipWork=(0,0,1280,672)
SPI_GETWORKAREA ok=True rect=(0,0,1920,1008)px
  equalCurrentRcWork=True  equalPrimaryRcWork=True
taskbar ABM_GETTASKBARPOS ok=True edge=下(3) rect=(0,1008,1920,1080)px
insets: left=0 top=0 right=0 bottom=72px(48dip)
```

- 本机只有 1 块显示器（`SM_CMONITORS=1`），且 DPI 150%（`144x144`），1920x1080 物理 → 1280x720 DIP。
- 日志还输出 `[WS] layout(...) basis=rcWork/rcMonitor target=... actual=... parent=0x1015C(Progman) parentOffsetCorrection=(0,0)`，证明布局落到目标矩形。

### 11.4 可见范围/遮挡实测（像素 + 命中，不只窗口矩形）

四组组合（`--transparent 0` 便于像素判定；`redHalf` 期望 `RGB(128,0,0)`）：

| 组合 | host 矩形 | redHalf | workBottom-6px | 任务栏带内 | 判定 |
|---|---|---|---|---|---|
| topmost + fill=work | `(0,0,1920,1008)` | `RGB(128,0,0)`，命中本进程 | `RGB(0,0,0)`，与基线差 252，命中本进程 | 命中 `Shell_TrayWnd`，差 232（宿主在下） | 铺满工作区；任务栏在宿主之上 |
| topmost + fill=desktop | `(0,0,1920,1080)` | 同上 | 同上 | 命中 `Shell_TrayWnd`（本次；早期一轮曾命中本进程），差 232 | 窗口矩形铺满整个桌面；**任务栏不一定被盖住** |
| desktoppane + fill=work | `(0,0,1920,1008)` | 同上 | 同上 | 命中 `Shell_TrayWnd`，差 232 | 铺满工作区；任务栏在上 |
| desktoppane + fill=desktop | `(0,0,1920,1080)` | 同上 | 同上 | 命中 `Shell_TrayWnd`，差 232 | 窗口矩形铺满桌面，但任务栏仍在上；像素被宿主影响（见 11.5） |

证据：`artifacts/workspace-20261108-172144.result.txt` + `workspace-<mode>-<fill>-20261108-172144.png/.log`、基线 `workspace-20261108-172144-baseline.png`；脚本 [scripts/test-workspace.ps1](scripts/test-workspace.ps1)。该轮 `RESULT failures=0`。

> 任务栏覆盖不是稳定成立：宿主与任务栏同为 topmost 时，谁在上取决于激活/Z 序（本轮两次 topmost+fill=desktop 实测：一次命中宿主、一次命中 `Shell_TrayWnd`）。因此 demo 不再断言“置顶态盖住任务栏”，状态文案改为按 `WindowFromPoint` 的实测结果输出。

### 11.5 壁纸层嵌入态下的特殊行为（如实记录）

1. **嵌入态任务栏永远在宿主之上**：即使 `fill=desktop` 把宿主做成 `rcMonitor`，任务栏带内命中仍是 `Shell_TrayWnd`。所以“铺满整个桌面”在嵌入态只是**窗口矩形**铺满，实际可见范围仍被任务栏裁掉 72px；判定文案因此区分“铺满工作区/铺满整个桌面”，并同时给出 `bottomOverWork`。置顶态则不一定：同为 topmost，Z 序可变（11.4）。
2. **任务栏是 Acrylic，透出下层**：desktoppane + fill=desktop 时任务栏像素由基线 `RGB(22,53,232)` 变为 `RGB(0,0,0)`（差 232，宿主黑底在下），但命中仍是任务栏。故遮挡判定以 `WindowFromPoint` 类名为准，像素差只作参考（此前曾因只看像素而误判）。
3. **父客户区坐标**：挂到 `Progman` 后 `SetWindowPos` 的 x/y 不是屏幕坐标；本机 `parentOffsetCorrection=(0,0)`，但代码保留复核/校正逻辑，避免在其他 Build 上偏位。
4. **DIP 基准跟随当前显示器 DPI**：本机 144 DPI 下 `1920x1008px → 1280x672dip`；换屏/改缩放后必须重新取 `GetDpiForMonitor`，不能缓存全局 DPI。
5. **图标可点性**：输入修复把宿主放到 `DefView` 之上，透明区域也会接收点击；桌面图标在该矩形内不可点（与 10.4 同一取舍）。

### 11.6 覆盖范围与限制

| 项目 | 状态 |
|---|---|
| 主屏默认任务栏（底部） | **已实测**（11.3/11.4） |
| 任务栏换到左/上 | **未实测**：Win11 不支持设置页直接改任务栏位置，需改注册表并重启 explorer，属修改系统设置，本轮不执行；代码按 `rcWork`/`ABM_GETTASKBARPOS` 通用处理 |
| 多显示器 / 副屏异 DPI | **未覆盖**：本机 `SM_CMONITORS=1`，只有 1 块屏 |
| 运行中改 DPI / 拔插显示器 | **未覆盖**：无法在不改系统设置的前提下安全触发；已接 `WM_DPICHANGED`/`WM_DISPLAYCHANGE`，未实测 |
| 任务栏自动隐藏 / 其他 AppBar | **未覆盖**；仅处理 `SPI_SETWORKAREA` 通知 |
| 与输入修复互不破坏 | **已实测**：desktoppane 模式同时通过 UIA 4/4 与工作区 4 组组合 |

## 12. 文件视图 Page（本轮新增）

### 12.1 调研依据与取舍

依据 `文档/文件显示性能调研/`（该目录本轮未改动）。调研结论中与本实现直接相关的几条：

1. **枚举不是瓶颈**：本机对 1 万/10 万项目录的 `FindFirstFileExW` 全量枚举在百毫秒量级；`FindExInfoBasic` 比 `FindExInfoStandard` 更快；`FIND_FIRST_EX_LARGE_FETCH` 是微软官方提供的大缓冲开关，但该数据集上不是数量级收益。具体数字与官方/社区来源见调研文档与 `PERF-RESEARCH.md` / `FILES-SOLUTION-REFERENCE.md`。
2. **成本主要在图标与 UI 喂入**：Shell 缩略图单张成本远高于一条枚举记录，所以图标必须“可见才取 + 限流 + 失败回退”，不能枚举时逐项取。
3. **喂入策略（Files 的做法，属社区源码经验）**：首批 25 ms（约 30–60 项）先上屏，之后每 500 ms 追加一批；ListView 用 `ItemsStackPanel` 虚拟化、`x:Bind` + `x:Phase` 降低绑定开销；1 万项以上不做即时排序，按枚举顺序显示。
4. **UWP/WinUI 2 没有“多条目插入”通知**：只能整体 `Reset`（`ObservableCollection` 的 Reset 映射为 `IObservableVector` 变更），因此本实现每批只发一次 Reset，不逐项 marshal。

官方明确说明的部分：`FindFirstFileExW`、`FindExInfoBasic`、`FIND_FIRST_EX_LARGE_FETCH`、`ItemsStackPanel`、`x:Phase` 的语义与限制来自微软 API 文档（调研文档已列出出处）。社区经验部分：Files 的具体时间片（25 ms / 500 ms）、8 路并发、首批条数，是源码阅读 + 本机复测得到的经验值，不是微软保证的行为。

### 12.2 实现架构与关键代码位置

| 层 | 文件 | 关键位置 |
|---|---|---|
| Win32 枚举后端 | [FileEnumeration.cs](src/WinUI2Demo/FileEnumeration.cs) | P/Invoke `FindFirstFileExW`/`FindNextFileW`/`FindClose` 在 158–176；首片规则 129–134、251–259；`BuildEntry` 324 |
| 批量集合 | [FileViewPage.xaml.cs](src/WinUI2Demo/FileViewPage.xaml.cs) | `BulkObservableCollection` 18–40：`AddRangeSilent` + 一次 `Reset` |
| 喂入/测量 | 同上 | `StartLoad` 140、`ApplyBatch` 245、`OnEnumDone` 284、滚动观测 483 |
| 图标两段式 | 同上 | `ContainerContentChanging` 354、`QueueIcon` 374、`LoadIconAsync` 387（信号量 8 路） |
| 页面 UI | [FileViewPage.xaml](src/WinUI2Demo/FileViewPage.xaml) | `ListView` 28、`ItemsStackPanel` 38–42、`x:Phase` 60/65/67/69/72 |
| 导航入口 | [PageA.xaml](src/WinUI2Demo/PageA.xaml)、[PageB.xaml](src/WinUI2Demo/PageB.xaml) | “→ 文件视图”按钮；回退用 `Frame.GoBack()` |
| 默认目录参数 | [Program.cs](src/WinUI2Demo/Program.cs) | `--dir` → `FileViewDefaultPath`；不传时页内回退 `Downloads`/用户目录 |

行为细节：

- **枚举**：后台线程一次 `FindFirstFileExW(path\*)`，参数 `FindExInfoBasic=1`、`FIND_FIRST_EX_LARGE_FETCH=0x2`。不递归，只列当前目录。
- **首批**：从“目录打开成功之后”开始计时（`FindFirstFileEx` 冷启动可能已超过 25 ms，若从线程启动计时首批会只剩 1 项，已实测踩到）。规则：累计 60 项立即发；或 25 ms 且至少 30 项；或硬上限 100 ms。10k 实测首批 60 项；100k 实测首批 30 项。
- **后续批**：每 500 ms 或累计 20 000 项发一批；每批在 UI 线程一次 `AddRangeSilent` + 一次 `Reset`。字符串（大小/时间/类型）全部在后台构造，UI 不做格式化。
- **进度**：250 ms 刷新一次状态栏“后台已扫描 / 已显示 / 批次”；完成后显示“枚举 ms / 首批 ms / 全部入列表 ms / 图标成功数 / 错误”。
- **图标**：模板里先用扩展名映射的 Segoe MDL2 字形（零 Shell 调用、立即可见）；容器实现（非回收队列）时按 8 路信号量请求 `StorageFile.GetThumbnailAsync(ListView, 32, ResizeThumbnail)`，成功后 UI 线程 `BitmapImage.SetSourceAsync` 覆盖到 `Image`，失败保留字形。**这是对调研建议（`IShellItemImageFactory`/Shell COM）的偏离**：demo 里用 WinRT API 代码量更小，实测能拿到 Shell 图标/缩略图；代价是无法精细控制 Shell 取消。
- **不阻塞导航**：`OnNavigatedFrom` 取消 CTS、停表；每次 `StartLoad` 有 generation，旧批次/旧图标结果不会污染新目录。

### 12.3 实测（UIA 真实点击 + 日志证据）

环境：本机 1 块屏 1920×1080、144 DPI。数据集为调研生成的真实 NTFS 目录：
`C:\Users\gold\perf-research\data\f10k`（10 000 项）、`C:\Users\gold\perf-research\data\f100k`（100 000 项），均为 1 KB `.dat` 文件。

| 场景 | 首屏 | 全量枚举 | 全部入列表 | 批次数 | UI 应用单批最大 | 喂入期 UI 心跳最大间隔 | 滚动到 50% | 容器数 |
|---|---|---|---|---|---|---|---|---|
| topmost + f10k | 首批 60 项 @111 ms；首次容器 @139 ms | 48 ms | 113 ms | 2 | 1 ms | 103 ms（1 个采样） | first=4996 | 79 |
| desktoppane + f100k | 首批 30 项 @126 ms；首次容器 @183 ms | 321 ms | 479 ms | 6 | 5 ms | 265 ms（3 个采样） | first=49996 | 99 |

- 滚动后图标：f10k 可见 79 项 → `requested=79 loaded=79 failed=0`；f100k 同样 `79/79/0`。
- 切到 `C:\Windows\Web\Wallpaper\Windows`（2 张 jpg）后重新枚举：`requested=2 loaded=2 failed=0`，证明真缩略图路径生效，不是只有字形。
- 点击链路（topmost 与 desktoppane 均通过）：真实点击“→ 文件视图”进入 → 页内真实点击透明开关关/开 → 返回 PageA → 再进文件页 → 退出；desktoppane 轮用 Ctrl+F4 宏键退出也通过。
- 导航进/出后透明仍正常：两轮都在文件页内完成 `[DEMO] transparency toggled` 两次（关→开），并在返回后继续可点。
- 证据：`artifacts/WinUI2Demo-FileView-topmost-20261108-175823.result.txt/.log`、`WinUI2Demo-FileView-desktoppane-20261108-175844.result.txt/.log`，脚本 [scripts/test-fileview-uia.ps1](scripts/test-fileview-uia.ps1)。

### 12.4 失败路线与限制（如实记录）

1. **workerw（壁纸层）点击失败，不是本页新增问题**：同脚本 `-Mode workerw` 一轮 `failures=11`：UIA 找到按钮但点击后无 `[NAV]`、无 `[FV]`、无 `[INPUT]`；`WindowFromPoint` 命中的是 `SHELLDLL_DefView`（桌面图标视图），鼠标消息根本到不了宿主。该层的定位就是“桌面图标之下”，与第 10 节结论一致；**可点击的嵌入路线是 `desktoppane`**（Progman 子窗口、Z 序在 `SHELLDLL_DefView` 之上）。
2. **喂入期有一次约 250 ms 的 UI 停顿**：100k 轮心跳最大 265 ms（3 个采样），`uiApplyMaxMs` 只有 5 ms，说明停顿主要在 XAML 对大批 Reset 的布局/自动化处理，而不是集合写入。之后真实点击透明开关、滚动、退出均成功，未出现持续卡死；但“喂入完全不影响交互”这一强断言不成立。
3. **首批条数受磁盘冷启动影响**：首轮 100k 冷读时首批只有 1 项（`FindFirstFileEx` + 首次 `FindNextFile` 的缓冲读占了 600+ ms），因此改为“从 open 之后计时 + 最少 30 项 / 硬上限 100 ms”；最终 100k 首批 30 项。10k 首批 60 项。
4. **图标并发 8 路是队列上限，不是在途取消**：已被回收的项在开始前会因 generation/CTS 跳过；已开始的 Shell 调用无法强制取消，只能完成后丢弃，失败计入 `failed`，字形仍在。
5. **本版未做**：排序、分组、右键菜单、拖放、搜索/过滤、缩略图大图质量（只取 32 px）、多目录并发枚举（调研建议可 8 路并发，本版单目录）、网络路径超时/断线重连、文件变更实时刷新、列头点击排序、键盘快捷键。
6. **覆盖限制**：本机只有 1 块屏，未覆盖多显示器/副屏异 DPI；未在 100k 上跑 topmost（10k 已覆盖 topmost），未在 10k 上跑 desktoppane（100k 已覆盖 desktoppane）；workerw 只做了“点击失败”对照。

## 13. NativeAOT 一级路线验证（2026-11-08）

目标：把 demo 宿主改成 NativeAOT 发布（.NET 10 + CsWinRT 2.2），验证能否编译/启动/功能等价，并给出内存对比。
细节数字与逐项证据见 [OPTIMIZATION.md](OPTIMIZATION.md) 第 3 节。

### 13.1 构建

```
msbuild ...\WinUI2Demo.csproj /restore /t:Publish /p:Platform=x64 /p:Configuration=Release `
  /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:PublishDir=...\artifacts\aot\
```

- 编译通过（ILCompiler 生成原生代码），产物 `WinUI2Demo.exe` 7.02 MB；`WinUI2Demo.dll` / `WinRT.Runtime.dll` / `Microsoft.Windows.SDK.NET.dll` 不再需要。
- **坑（已解决）**：AOT publish 不带 `*.xbf`，必须从普通构建补齐；否则页面 XAML 解析失败。

### 13.2 功能（包内运行）

- 通过：启动与包身份、WinUI2 控件激活、`DesktopWindowXamlSource.AttachToWindow`(hr=0)、`IXamlSourceTransparency`（set/readback=True）、Frame/Page 进入-返回-再进入、FileViewPage 100k 枚举与滚动、页内透明开关、Ctrl+F4 退出、透明像素采样四组全过。
- **未通过/差异**：AOT 下 `FindScrollViewer` 递归找不到 ListView 的 ScrollViewer（`Loaded`、`LayoutUpdated` 都触发过），`[FV] scrollviewer attached` 不出现，脚本的 scroll-settle 断言因此 FAIL。用独立 UIA 探针复核：滚动本身正常（`SetScrollPercent 0→100` 生效，`realized=38`）。属观测缺口，不影响交互。
- 失败路线（如实记录）：未打包直跑 AOT exe 时 `XamlControlsResources` / `InfoBar` 报 `hr=0x80040154`（REGDB_E_CLASSNOTREG）。这是 WinRT 激活需要包注册，塞进已注册的 loose 布局后全部正常，不是 AOT 限制。

### 13.3 内存（`--memgc 1`，100k，desktoppane）

| 场景 | CoreCLR WS/Private | AOT WS/Private |
| --- | --- | --- |
| 空载 | 94.1 / 24.2 MB | 56.0 / 15.9 MB |
| 100k feedDone | 184.7 / 89.5 | 148.9 / 82.8 |
| 图标 settle | 191.0 / 96.7 | 151.6 / 87.8 |
| 二次加载 feedDone | 233.4 / 138.9 | 189.3 / 125.4 |

- 托管 **42.76 MB（AOT）vs 42.80 MB（CoreCLR）**：AOT 不改变条目模型，自持仍 ≈42.4 MB，未达 <10 MB 目标。
- GC：pauseΔ 115 ms vs 204 ms。

### 13.4 判定与推荐

- 一级路线**未失败**（能跑 + 功能等价），按既定路线**不启动二级 C++/WinRT 宿主**。
- 但 AOT 只降低运行时开销，不能解决 100k 的自持内存；且打包需手工补 XBF、滚动观测 hook 在 AOT 下失效。
- **推荐**：交付继续用 CoreCLR；下一轮把优化放在条目模型瘦身（字符串驻留/前缀去重、图标缓存上限、枚举缓冲复用）。

## 14. 条目模型瘦身与遮挡降级（R2–R8，2026-11-08 晚）

逐轮数字与判定见 [OPTIMIZATION.md](OPTIMIZATION.md) 第 4、5 节；本节只记录行为变化与可复测入口。

### 14.1 行为变化

- **路径**：`FileEntry` 不再驻留 `FullPath`，只存共享父目录；`FullPath` 在需要时 `Path.Combine` 懒拼（点击/打开仍拿得到完整路径）。
- **显示文本**：SizeText/DateText/TypeText 在同一次枚举内共享同一实例（`DisplayTextPool`，每类 8192 上限）；`Size`/`Modified` 原始值字段已删除（模板与排序只用文本）。
- **图标缓存**：上限 160 + LRU，只淘汰离屏项；重新滚动到已淘汰区域会重新解码，`visibleMissing` 在观察窗内可回填到 0。
- **遮挡降级**：宿主被全屏/最大化窗口覆盖或隐藏时，按 tier1→tier4 丢图标缓存、收紧枚举批次、GC + `EmptyWorkingSet`；重新可见时懒加载回来。判定依据、边界与分级收益见 OPTIMIZATION.md 5.1–5.5。
- **新增开关 `--occ 0`**：关闭遮挡检测。仅在测性能/内存口径时使用（否则 IDE/终端盖住桌面会把枚举批次降到 1500，干扰 `feedDone`）；默认开启。
- **新日志**：`[MODEL]`（仅 `--memgc 1`，模型分解）、`[OCC]`/`[OCC][FV]`（遮挡状态与分级动作）、`[MEMPRESSURE]`（`MemoryManager` 级别）、`[FV] view(settle)` 追加 `evicted/cache/visibleMissing`。

### 14.2 实测结果（100k，`--memgc 1`，`--occ 0`）

| 指标 | 基线 | R2–R5 终态 |
| --- | --- | --- |
| managed | 42.80 MB | **15.35 MB** |
| 自持（managed − 空载） | 42.36 MB | **14.91 MB** |
| Private / WS | 89.5 / 184.7 MB | 64.4 / 159.5 MB |
| `[MODEL]` total / 去重 | 41.58 / 28.42 MB | 27.85 / 14.69 MB |
| enumMs / feedDone / firstPaint | 320–660 / 489–1360 / 88–139 | 328–499 / 800 / 209 |

- 结构性结论：**当前架构（每项一个 class + INPC + ObservableCollection）下 100k 自持下限 ≈14.9 MB，10 MB 不可达**；继续下降需要改池索引或虚拟 item source，见 OPTIMIZATION.md 4.7。
- 遮挡降级：全屏覆盖触发、700×500 部分遮挡不触发、二次触发、恢复 `restoreMs=380/770 ms`（≤1 s 判定划算）；tier4 `WS 165.1 → 1.3 MB` 而 `private 69.4 MB` 不变 —— **只降 WS，不算省内存**。
- 回归：`scripts/test-fileview-uia.ps1 -OccludeTest`（100k）`failures=0`，进入/返回/透明/滚动/退出全 PASS；`-Sweep 25,50,75,100,0` 用于图标 LRU（cache 封顶 160、累计 evicted 76、各点 `visibleMissing=0`）。

## 15. 正式版 v1.0（2026-11-08 深夜）

详细数字与逐项归因见 [OPTIMIZATION.md](OPTIMIZATION.md) 第 6 节；启动时序单页见 [启动时序.md](启动时序.md)。

### 15.1 形态变化（不再手动切模式）

- 启动参数默认 `--mode desktoppane --transparent 1`；进程启动即嵌入 `Progman`（可点击），透明默认开，并直接导航到 `FileViewPage`。
- 文件视图由 `ListView` 改为 `GridView` + `ItemsWrapGrid`：每项 36×36 图标 + 名称（最多 2 行）+ 类型/大小；枚举、批量喂入、两段式图标、LRU、模型瘦身保持。
- 顶栏只保留“透明”和“退出”；实验用的模式/铺满/InfoBar 开关已移除（`Ctrl+F2/F3/F4` 宏键仍可用，`Ctrl+F3` 仅作为调试留存）。
- 入场动画：隐藏创建 → 隐藏布局 → 显示（内容 `Opacity=0`）→ 5% 幽灵帧 → 等 2 个 Rendering tick → `DwmFlush` → Composition 淡入 0.05→1 / 上移 10→0（240 ms）。

### 15.2 真实操作验收（UIA + 真实 `mouse_event`，Release 包）

| # | 场景 | 结果 | 证据 |
| --- | --- | --- | --- |
| 1 | 启动自动进入桌面层 | PASS | `[REPARENT] newParent=Progman`、`[DESKTOPPANE] hostIsChild=True`；无 `[FALLBACK]` |
| 2 | 启动自动进入文件视图 | PASS | `autoEntry=True`、`[NAV] Navigated to WinUI2Demo.FileViewPage` |
| 3 | 透明默认开启 | PASS | `[XST] set_IsBackgroundTransparent(True) => hr=0x0 readback=True`；页内关/开各一次均生效 |
| 4 | 动画在内容就绪后才开始 | PASS | `firstFrame committed t=805ms` → `anim start t=814ms` → `anim complete t=1096ms`（100k） |
| 5 | 点击可用 | PASS | 透明开关、退出按钮的真实点击 `under=WinUI2DemoHost`；`foreground ok=False` 是 Progman 子窗的预期 |
| 6 | 退出正常 | PASS | 点击退出后 `process exited=True` |
| 7 | 网格虚拟化 | PASS | 100k：50% 处 `realized=409`；增量滚动 `realized=528`，均远小于 100000 |
| 8 | 万级滚动/首屏 | PASS | 10k：`firstPaint=205 feedDone=212`、滚动停顿 313 ms；100k：`firstPaint=129 feedDone=1017`、滚动 180–311 ms、增量 165 ms |

- 命令：`scripts/test-fileview-uia.ps1 -Mode desktoppane -Dir ...\f10k -Expect 10000`；100k 加 `-Wheel`；
  内存跑 `-MemGc -NoOcc`。两次 `RESULT ... failures=0`。
- 日志：`artifacts/WinUI2Demo-FileView-desktoppane-20261108-231223.log`（10k）、`...-231248.log`（100k）、`...-231356.log`（100k 内存）。

### 15.3 首屏时间（实测）

| 口径 | 10k | 100k |
| --- | --- | --- |
| 进程启动 → 窗口创建（隐藏） | 331 ms | 278–284 ms |
| → 页面 Loaded | 744 ms | 516–533 ms |
| → 显示宿主（`Opacity=0`） | 770 ms | 541–545 ms |
| → 首帧提交（DwmFlush=0） | 864 ms | 805–827 ms |
| → 动画完成（完全可见） | **1158 ms** | **1096–1118 ms** |

- 冷启动会更慢（同晚记录 `anim complete 1863 ms`）；上表为热缓存复测，单次极值不作结论。
- 动画期间首批 30 项已布局；100k 剩余批次在动画后继续喂入（`feedDone 994–1017 ms`），不阻塞交互。

### 15.4 降级与未验证项

- 透明失败 → 取消透明并切深色不透明背景（`SetOpaqueFallback`），日志 `[FALLBACK] transparency-unavailable`。
- 嵌入失败 → `FallbackToTopmost` 恢复 `WS_POPUP + WS_EX_TOPMOST`、`SetParent(null)`、工作区定位，保持可交互。
- 首帧超时（`StartupShownFrameWaitMs=1200`）→ 记录 `[FALLBACK] first-frame-timeout ... => animate anyway`，从 0.05 起播，不闪白。
- **本轮未做故障注入**：三条降级路径只有代码路径和日志文案，没有人为制造失败环境；正常路径已全部实测通过。
- 其他未完成项：GridView Private +14.2 MB（相对 ListView）、喂入期 UI 停顿 131–143 ms 未达 50 ms 预算、首屏图标先显示类型字形（缩略图去抖后补）。
