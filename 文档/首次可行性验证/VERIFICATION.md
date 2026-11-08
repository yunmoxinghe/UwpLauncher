# UWP XAML Island 透明背景：DesktopFlyouts 考据 + 本机实测验证

> 日期：2026-11-08（Asia/Shanghai）
> 机器：Windows 11 Build 26300.0，VS 2026 18.11，Windows SDK 26100/28000，.NET SDK 10.0.403，DPI 150%（144），1280x720 单屏
> 被测仓库：`https://github.com/0x5bfa/DesktopFlyouts` @ `6cdcef5538db19e044ac20ba6fb010f322ef2881`（`git describe` = `v1.6.0-1-g6cdcef5`）

## 一句话结论

1. DesktopFlyouts 的 UWP 透明**不靠 DWM/分层窗口**，而是用 `Window.Current` 上未进入常规 SDK 投影的 WinRT 接口 `IXamlSourceTransparency`（IID `06636C29-5A17-458D-8EA2-2422D997A922`，内部即 `IWindowPrivate`）设置 `IsBackgroundTransparent = true`，配合 `WS_EX_NOREDIRECTIONBITMAP` 宿主窗口。
2. 本机最小工程实测：**透明成立**。`true` 时岛背景完全透出后方纯绿对照窗，`false` 时为不透明白底。
3. 嵌入桌面：DesktopFlyouts 原方案（置顶透明浮层）透明成立；额外用 `SetParent` 挂到 `Progman` 的子 `WorkerW` 后，**透明仍然成立**（左半透出壁纸，右半为 50% 红与壁纸混色）。
4. WinUI 2 对照：**宿主层透明仍成立，但 WinUI 2 内容未渲染**。`Microsoft.UI.Xaml.Controls.NavigationView` 在未打包进程中激活失败（`0x80040154 REGDB_E_CLASSNOTREG`），岛内容未设置；该结果不是透明失败，WinUI 2 内容透明度无法判定（详见第 4 节）。

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

### 4.1 实测：WinUI 2 内容激活失败

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
- **WinUI 2 内容从未渲染**：右半始终没有出现任何红色区域，因此“WinUI 2 内容自身的透明是否成立”**无法作答**。这**不是透明失败**，而是 WinUI 2 控件在未打包进程中无法激活；不能把空岛透出背景当作 WinUI 2 透明成功。

### 4.3 失败根因（基于实测证据）

- 本机**已安装** WinUI 2 框架包 `Microsoft.UI.Xaml.2.8 8.2511.26001.0 (x64)`，但本工程是**未打包 Win32 进程**，没有 MSIX 包标识与框架包依赖图，WinRT 激活无法解析该框架包内的可激活类 → `REGDB_E_CLASSNOTREG (0x80040154)`。
- 输出目录只有 `Microsoft.UI.Xaml.Projection.dll`，没有原生 `Microsoft.UI.Xaml.dll`（NuGet 不负责部署框架包）。
- 要让 WinUI 2 内容真正进入这个岛，需将宿主改为 MSIX 打包并对 `Microsoft.UI.Xaml.2.8` 声明依赖（属另一种承载方式）。本次**未采用**，以免用打包手段绕开"未打包宿主 + WinUI 2"这一前提本身不成立的事实。
- 附带事实：`NavigationView` 与 `Windows.UI.Xaml.UIElement` 的类型兼容性在编译期已验证通过，因此失败点不是类型系统，而是 WinRT 激活。

**证据文件**：`artifacts/WinUI2Probe-topmost-on-20261108-135302.log`、`WinUI2Probe-topmost-off-20261108-135332.log`、`WinUI2Probe-workerw-on-20261108-135357.log`、`WinUI2Probe-workerw-off-20261108-135412.log` 及同名 `.png`。

---

## 5. 复现方式

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
```

探针参数：`--mode topmost|plain|workerw`、`--transparent 1|0`、`--log <path>`、`--hold <ms>`、`--capture <ms>`。

---

## 6. 证据文件

| 文件 | 内容 |
|---|---|
| `artifacts/topmost-on.log` / `topmost-off.log` | 阶段 2 的 IID QI、`hr`、读回值、像素采样 |
| `artifacts/workerw-on.log` | 第一次 WorkerW 失败（`ERROR_INVALID_PARAMETER 87`） |
| `artifacts/workerw-on2.log` / `workerw-off2.log` | 修正后的 WorkerW 成功复测 |
| `artifacts/*.png` | 4 组截图与 700x500 裁剪图 |
| `artifacts/WinUI2Probe-*.log` / `*.png` | WinUI 2 对照实验 4 组（含 `0x80040154` 激活失败证据） |
| [LEDGER.md](LEDGER.md) | 分阶段台账（含环境、行号、失败与结论） |

---

## 7. 局限与不确定项

- 结果基于本机 Windows 11 Build 26300 / 150% DPI / 单屏 / 交互式会话；Win10 的 WorkerW 层级不同，WorkerW 部分需按当地窗口树重测。
- 未使用任何 DWM 属性，且实测证明不需要；但未测试“有其他桌面工具（壁纸引擎、动态壁纸）同时挂 WorkerW”时的冲突。
- 未验证输入/焦点/命中测试在 WorkerW 子窗中的行为（超出透明判据范围）。
- `IsBackgroundTransparent` 按官方文档对**当前线程所有** `DesktopWindowXamlSource` 生效；工程中每个进程只有一个岛，未做多岛对照。
- WorkerW 中普通 GDI 子窗不参与合成的原因只做了现象级定位（DComp 内容可见、GDI 内容不可见），未进一步下钻 DWM/桌面合成内部实现。
- WinUI 2 对照实验只证明了"未打包宿主无法激活 WinUI 2 控件"；未验证 MSIX 打包 + 框架包依赖场景下 WinUI 2 内容能否成立（该场景属另一实验，本次有意未做）。
