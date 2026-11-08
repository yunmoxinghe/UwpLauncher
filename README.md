# UwpLauncher

UWP XAML 岛透明背景的可行性验证工程，也是后续桌面启动器方向的实验底座。

本仓库针对 [DesktopFlyouts](https://github.com/0x5bfa/DesktopFlyouts) 的 UWP 版 XAML Island 透明实现做了源码考据，并在本机从零搭建最小宿主工程复现、实测，随后额外验证了把该岛挂到桌面 `WorkerW` 层后透明是否仍然成立。另含一个 WinUI 2 对照实验（`Microsoft.UI.Xaml 2.x` 控件作为岛内容），并按正式路线把它改为 MSIX 打包应用（声明 `Microsoft.UI.Xaml.2.8` 框架包依赖）后复测。结论以实际运行的像素采样与日志为准，不以静态阅读代码代替验证。

## 已验证结论

- **透明机制**：DesktopFlyouts 不依赖 DWM 属性、分层窗口或 `SetLayeredWindowAttributes`。它把 `Window.Current` 查询接口到未进入常规 SDK 投影的 WinRT 接口 `IXamlSourceTransparency`（IID `06636C29-5A17-458D-8EA2-2422D997A922`，内部即 `IWindowPrivate`），调用 `set_IsBackgroundTransparent(true)`；宿主窗口使用 `WS_POPUP` + `WS_EX_NOREDIRECTIONBITMAP`（不创建 GDI 重定向表面）。
- **最小岛实测**：透明开关为 `true` 时，岛的无背景区域透出后方纯绿对照窗（RGB 0,255,0），50% 红区域与绿底混色为 RGB(128,127,0)；开关为 `false` 时，左半为不透明白底 RGB(255,255,255)，右半为 RGB(255,127,127)。两次调用的 `hr` 均为 `0x00000000`，读回值与设置一致。
- **嵌入桌面实测**：用 `SetParent` 挂到 `Progman` 的子 `WorkerW`（本机为 `0xC0908`）后透明仍生效——左半透出壁纸 RGB(200,207,217)，右半红与壁纸混色 RGB(228,103,108)，与混色算式自洽；关闭开关后恢复白底。
- **首轮失败已留证**：按“枚举顶层 WorkerW”的经典写法会选到不可用句柄，`SetParent` 返回 `ERROR_INVALID_PARAMETER (87)`；根因是本机壁纸 `WorkerW` 为 `Progman` 的子窗口，而非顶层窗口。失败日志保留在 `artifacts/`。

**嵌入桌面阶段的局限**：上述 WorkerW 结论只代表本机 Windows 11 Build 26300、150% DPI、单个进程单个岛的观测；同一 WorkerW 层下普通 GDI 子窗口不参与桌面合成（只有 DComp/XAML 内容与系统壁纸可见），且该场景下的输入、焦点与命中测试行为未做验证，换机器或换系统版本需要按当地窗口树重测。

**WinUI 2 对照实验**：同一宿主下把 `Microsoft.UI.Xaml.Controls.NavigationView` 作为岛内容时，未打包进程激活失败（`0x80040154 REGDB_E_CLASSNOTREG`）——这是阶段结论；改为 MSIX 打包（声明 `Microsoft.UI.Xaml.2.8` 框架包依赖、开发模式注册）后，`XamlControlsResources` / `NavigationView` / `InfoBar` 均正常创建并真实渲染。透明开关在包标识下仍生效（对照内容严格透出绿底/壁纸）；但 `NavigationView` 模板自带 `#80FFFFFF`（50% 白）内容底色，画面是“50% 白 + 背景”的混色，**不能**据此声称 WinUI 2 内容完全透明。详见报告第 4、5 节。

> MSIX 说明：本机以开发模式 `Add-AppxPackage -Register` 完成注册（签名安装因非管理员无法信任自签名证书而失败，错误 `0x800B0109`，已记录）。包标识与框架包依赖解析真实生效。

## 交互演示包：WinUI2Demo

把已验证的机制（`DesktopWindowXamlSource` + `IXamlSourceTransparency` + `WS_EX_NOREDIRECTIONBITMAP` + WorkerW）做成可直接在桌面运行的交互 demo：
PageA/PageB 按钮往返导航（可观察返回栈与页面重建）、透明开/关、置顶浮层 ↔ 嵌入桌面切换，并含一个 WinUI 2 `InfoBar` 证明 `Microsoft.UI.Xaml` 主题生效。

交付物在 [`文档/首次可行性验证/dist/`](文档/首次可行性验证/dist/)：

- `WinUI2Demo_1.0.0.0_x64.msix`（10,055,215 字节）与 `WinUI2DemoDev.cer`
- `WinUI2Demo-layout.zip`（开发者模式松散注册用）
- `使用说明.md`（安装/启动/操作/正常现象/透明失效判定，含 `0x800B0109` 限制说明）

本机实测：签名 MSIX 安装因非管理员返回 `0x800B0109`，开发模式 `Add-AppxPackage -Register` 成功；随后用包身份启动并逐项验证了按钮导航、返回栈、透明开关读回、WorkerW 切换与退出。

## 目录结构

```
.
├── README.md
├── LICENSE
└── 文档/
    └── 首次可行性验证/
        ├── VERIFICATION.md              # 完整验证报告（考据、实测数据、失败证据、局限）
        ├── LEDGER.md                     # 分阶段台账（环境、行号、结论）
        ├── artifacts/                    # 运行日志与屏幕截图证据
        ├── dist/                         # 交互演示的可安装交付物（MSIX / 证书 / layout zip / 使用说明）
        ├── scripts/
        │   ├── run-probe.ps1             # 一键运行探针 + 截图（-Probe 选择工程）
        │   ├── run-demo.ps1              # 启动交互 demo（可选 --selftest 冒烟与截图）
        │   └── package-msix.ps1          # 打包/签名/安装（-ProjectName 选择工程）
        └── src/
            ├── IslandProbe/              # 最小 UWP XAML Island 宿主工程
            │   ├── IslandProbe.csproj
            │   ├── Program.cs            # 岛创建、透明开关、WorkerW 挂载、像素采样
            │   ├── IXamlSourceTransparency.cs  # 按 IID 手工定义的透明接口
            │   ├── ManualDefinitions.cs
            │   ├── NativeMethods.txt/.json    # CsWin32 配置与所需 API 清单
            │   └── App.xaml(.cs), app.manifest
            ├── WinUI2Probe/              # WinUI 2 对照工程（同样的宿主，内容改为 WinUI 2 控件）
            │   └── Package.appxmanifest  # MSIX 清单（Microsoft.UI.Xaml.2.8 框架包依赖）
            └── WinUI2Demo/               # 交互演示工程（控制条 + Frame/Page + 透明/模式切换）
```

完整报告见 [文档/首次可行性验证/VERIFICATION.md](文档/首次可行性验证/VERIFICATION.md)。

## 复跑实验

在仓库根目录执行（需已安装 Visual Studio 与 .NET SDK，见下方环境要求）：

```powershell
# 1) 构建最小工程
& "<VS 安装目录>\MSBuild\Current\Bin\MSBuild.exe" `
  "文档/首次可行性验证/src/IslandProbe/IslandProbe.csproj" `
  /restore /p:Platform=x64 /p:Configuration=Debug

# 2) 运行探针（自动截图到 artifacts/）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Mode topmost -Transparent on

# 3) 嵌入桌面模式（自动最小化窗口，结束前还原）
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Mode workerw -Transparent on

# 4) WinUI 2 对照工程（构建后加 -Probe WinUI2Probe）
& "<VS 安装目录>\MSBuild\Current\Bin\MSBuild.exe" `
  "文档/首次可行性验证/src/WinUI2Probe/WinUI2Probe.csproj" `
  /restore /p:Platform=x64 /p:Configuration=Debug

powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode topmost -Transparent on

# 5) WinUI 2 的 MSIX 路线：打包 + 安装/注册，再以包身份运行
powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/package-msix.ps1"

powershell -ExecutionPolicy Bypass -File `
  "文档/首次可行性验证/scripts/run-probe.ps1" `
  -Probe WinUI2Probe -Mode topmost -Transparent on -Packaged
```

探针参数：`-Probe IslandProbe|WinUI2Probe`、`--mode topmost|plain|workerw`、`--content winui2|grid`、`--transparent 1|0`、`--log <path>`、`--hold <ms>`、`--capture <ms>`；`run-probe.ps1 -Packaged` 表示按已注册 MSIX 包的 AUMID 启动。

判定方式：探针在屏幕坐标固定采样点用 `GetPixel` 读取合成后的真实像素，同时脚本保存整屏截图；`topmost` 模式以纯绿对照窗为后方参照，`workerw` 模式以桌面壁纸为参照。

## 环境要求

- Windows 11（本项目实测环境为 Build 26300，DPI 150%；Windows 10 需自行重测 WorkerW 部分）
- Visual Studio 2026（含 MSBuild）与 Windows SDK 10.0.26100（最低目标 10.0.17763）
- .NET SDK 10
- NuGet 依赖自动还原：`Microsoft.UI.Xaml 2.8.7`、`Microsoft.Windows.CsWinRT 2.2.0`、`Microsoft.Windows.CsWin32 0.3.275`
- 走 UWP XAML Islands（`<UseUwp>true</UseUwp>`，非 WinUI 3）路径，需 VS 自带的 UWP XAML 编译目标
- MSIX 路线另需：Windows SDK 的 `makeappx.exe`/`signtool.exe`、已安装 `Microsoft.UI.Xaml.2.8` 框架包、开启开发者模式（松散注册）

## 许可

[MIT](LICENSE)
