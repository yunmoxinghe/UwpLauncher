# Windows 文件列表显示：内存调研与「要不要改用 C++」

> 本文回答三个问题：官方对 UWP/XAML/.NET 内存有哪些明确建议；C#（.NET + WinRT 投影）与 C++/WinRT 宿主的常驻内存差多少、差在哪；按优先级该做哪些省内存改造。
> 配套文档：[文件显示性能调研](./PERF-RESEARCH.md)（枚举/缩略图/XAML 性能与 C#/C++ 耗时对照）、[Files 上游方案分析](./FILES-SOLUTION-REFERENCE.md)。
> 测量时间：2026-11-08，Windows 11 build 26300（Insider），i7-8550U / 7.9 GB / SATA SSD。
> 临时工程位于 `C:\Users\gold\perf-research\`（仓库外，未提交）。

---

## 0. 结论摘要

1. **换语言只省「宿主固定开销」，不省「数据规模开销」。** 同一台机、同一口径的空载 XAML 岛宿主：C# 版 **private bytes 22.2 MB / working set 87.7 MB**，C++/WinRT 版 **private bytes 12.6 MB / working set 51.2 MB**。差值约 **9.6 MB private、36.5 MB working set**，其中 ~7.4 MB private 是 CoreCLR 运行时本身，~2.2 MB 是托管 XAML 投影与应用层，其余是 .NET 程序集的共享页。
2. **10 万条目模型本身，C# 与 C++ 几乎相同。** 完整字段模型每项 private 增量：C# **343.6 B**、C++ **358.2 B**；精简模型：C# **158.9 B**、C++ **110.8 B**。10 万项约 11–36 MB —— 换语言至多省 ~4.8 MB（精简模型），远小于位图与 WinRT 对象的开销。
3. **最贵的不是语言，是「每项一个 WinRT 对象」。** 每项一个 `PropertySet`：C# **约 2.56 KB/项**、C++ **约 1.59 KB/项**；10 万项分别是 **+245 MB / +152 MB** private。C# 的投影（RCW + GC 头）比原生贵约 **0.98 KB/项**，但两者都远高于纯 CLR/C++ 模型的 0.1–0.4 KB/项。
4. **位图必须按显示尺寸解码。** 保留 200 张已解码 `SoftwareBitmap`：64 px 每张 **43.2 KB**、256 px **327.1 KB**、512 px **1.12 MB**（BGRA8 理论值分别 16/256/1024 KB，差额是解码器固定开销）。512 px 是 64 px 的 **26 倍**，100 张 512 px 就要 ~110 MB。
5. **整进程压到 20 MB working set 不可达。** C++ 空载岛宿主 working set 已 **51.2 MB**，其中 **42.3 MB 是系统 DLL 的共享页**（系统 XAML、D3D/DComp、Shell 代理等），只要用系统 XAML 岛就绕不开。**private bytes 有机会低于 20 MB**：C++ 版已实测 12.6 MB；C# 版 22.2 MB，改用 NativeAOT 有望降到 ~18 MB，但「NativeAOT + UWP XAML 岛」本次未构建成功，属推断而非实测（见 §6）。
6. **优先级结论**：先把「模型别用 WinRT 对象、位图按显示尺寸解码、只保留可见项」做掉，收益比换语言大一到两个数量级；换 C++/WinRT 只在内存极紧（例如硬指标 <20 MB private）时才值得。

---

## 1. 测量环境与口径

| 项 | 值 |
|---|---|
| CPU / 内存 | Intel Core i7-8550U（4C/8T）/ 7.9 GB |
| 系统 | Windows 11 专业版 10.0.26300（Insider） |
| 磁盘 | SanDisk SD8SN8U512G1002 SATA SSD，NTFS |
| 运行时 | .NET 10.0.403 SDK；CoreCLR 与 NativeAOT 各一套 |
| C++ 工具链 | VS 18 Community（MSVC 14.x），`/std:c++20 /O2 /MD`，C++/WinRT 头来自 Windows SDK 10.0.28000 |
| 实时防护 | Microsoft Defender 默认开启（未加排除项） |

**口径**：

- 每个场景**单独起一个进程**，进程内先打印 `BASE`，再分配，再 `AFTER`；`PERITEM_*` / `PERIMAGE_*` 为 `(AFTER − BASE) / N`。
- **working set（WS）** `Process.WorkingSet64` / `GetProcessMemoryInfo.WorkingSetSize`，**含共享页**；**private bytes（PVT）** `PrivateMemorySize64` / `PrivateUsage`，只含私有提交。
- C# 侧托管堆用 `GC.GetTotalMemory(true)` 与 `GC.GetGCMemoryInfo().HeapSizeBytes` 分开记；C++ 侧用 `HeapWalk` 累加 `PROCESS_HEAP_ENTRY_BUSY` 的 `cbData` 记原生堆忙碌字节。
- **"共享页 / 私有页"拆分**用 `QueryWorkingSet`（返回每页 `Shared` 位与 `VirtualPage`）按地址区间归因到模块，见 `MemMap.exe`。
- 所有数字来自**同一次会话、同一台机**；不跨窗口比较绝对值。测量期间系统负载中等（Trae IDE 常驻）。
- **未做**：OS 文件缓存清理；完整 UWP 应用包；真实 XAML 列表渲染。见 §6。

---

## 2. 官方最佳实践（区分「官方明确」与「社区经验」）

### 2.1 内存生命周期与用量 API

| 结论 | 性质 | 出处 |
|---|---|---|
| UWP 应用在进入后台时系统会**下调内存上限**；应在 `MemoryManager.AppMemoryUsageLimitChanging`（2 秒内）和 `AppMemoryUsageIncreased`（`High`/`OverLimit` 时）主动释放资源 | 官方明确建议 | [Free memory when your app moves to the background](https://learn.microsoft.com/en-us/windows/uwp/launch-resume/reduce-memory-usage) |
| 官方给出的释放手段：进入后台后把 `Window.Current.Content = null`、解绑 UI 事件、移除引用，然后 `GC.Collect()`（文档明确说「平时不要强制 GC，只在这个特定场景用」） | 官方明确建议 | 同上 |
| `MemoryManager` 提供 `AppMemoryUsage`、`AppMemoryUsageLevel`、`AppMemoryUsageLimit`、`GetAppMemoryReport()`、`GetProcessMemoryReport()`，以及上述事件 | 官方 API 文档 | [MemoryManager 类](https://learn.microsoft.com/en-us/uwp/api/windows.system.memorymanager) |
| `AppMemoryUsage` 的精确构成（是否含共享页/提交口径）在该页没有展开说明；报告内存时应同时给 WS 与 private bytes，别只报一个数 | 官方文档的事实限制 | 同上（页面未定义口径） |
| 桌面 Win32 + XAML 岛**没有** UWP 的挂起/剪裁生命周期；`MemoryManager` 虽可调用（本次探针读到了 `AppMemoryUsage`/`AppMemoryUsageLimit`），但系统不会因你"进入后台"而自动剪裁，必须自己做缓存上限与释放策略 | 社区经验 + 本次实测可调用 | 本文 §3.1；官方页面只描述 UWP 生命周期 |

### 2.2 XAML 性能与内存

| 结论 | 性质 | 出处 |
|---|---|---|
| 用 `x:Load` 把非首屏元素延迟创建/延迟卸载，可降低启动时间与常驻内存；`x:Load` 元素在不需要时会被拆掉 | 官方明确建议 | [x:Load](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-load-attribute) |
| 用 `x:Phase` 把绑定分阶段执行，避免首屏一次创建全部子元素 | 官方明确建议 | [Optimize your XAML layout](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-your-xaml-layout) |
| `ItemsRepeater` 是为大数据集设计的轻量列表控件，靠布局 + 元素回收（recycling）实现虚拟化 | 官方 API/指南 | [ItemsRepeater](https://learn.microsoft.com/en-us/windows/uwp/design/controls-and-patterns/items-repeater) |
| `ListView`/`GridView` 默认使用虚拟化面板（`ItemsStackPanel`/`ItemsWrapGrid`），但**只有直接绑定到项集合、模板足够简单时才生效**；嵌套 `ScrollViewer`、过复杂模板会破坏虚拟化 | 官方明确建议 | [Optimize ListView and GridView](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-listview-and-gridview) |
| `BitmapImage.DecodePixelWidth` 让解码器按目标显示宽度解码，而不是先解出原始尺寸再缩小；这是**降内存**的关键开关 | 官方明确建议 | [BitmapImage.DecodePixelWidth](https://learn.microsoft.com/en-us/windows/uwp/api/windows.ui.xaml.media.imaging.bitmapimage.decodepixelwidth) |
| 需要更细粒度控制时用 `BitmapDecoder` + `BitmapTransform`（`ScaledWidth`/`ScaledHeight`）解出 `SoftwareBitmap`，只保留可见项 | 官方 API | [BitmapDecoder](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapdecoder)、[BitmapTransform](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform) |
| `BitmapImage` 会缓存按 URI 加载的图片；需要绕过缓存/强制重解码时用 `CreateOptions = BitmapCreateOptions.IgnoreImageCache`。这条更多是「行为说明」，不是「省内存建议」 | 官方 API 行为 + 社区经验 | [BitmapCreateOptions](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.media.imaging.bitmapcreateoptions) |

> 注：原计划的 `debug-test-perf/optimize-images` 页面当前返回 404，本文改为引用 `BitmapImage.DecodePixelWidth` 与 `BitmapDecoder` 两个可用页面；相关结论仍是官方的，只是入口换了。

### 2.3 .NET GC 配置

| 结论 | 性质 | 出处 |
|---|---|---|
| 工作站 GC（默认）堆更小、暂停更长；服务器 GC（`DOTNET_gcServer=1`）吞吐更好但**堆与常驻内存更大**。桌面客户端应优先工作站 GC | 官方明确说明 | [Workstation vs server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)、[GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector) |
| 可用 `DOTNET_GCHeapHardLimit`（十六进制字节）/`System.GC.HeapHardLimit` 设 GC 堆硬上限；`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 会反映该值 | 官方明确 | [GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)、[GC.GetGCMemoryInfo](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getgcmemoryinfo) |
| 大对象堆（LOH）用于 ≥85 000 字节对象，默认不压缩（可用 `GCLargeObjectHeapCompactionMode` 强制一次压缩）；POH（ pinned object heap）用于固定对象 | 官方明确 | [Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap) |
| `DOTNET_GCConserveMemory`（0–9）让 GC 更保守地保留内存，适合常驻桌面进程 | 官方配置项 | [GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector) |
| 本次实测：服务器 GC 让空载 private bytes 从 8.8 → 13.3 MB（+4.5 MB），10 万项每项成本几乎不变（351.8 vs 343.6 B）。**server GC 不省内存，反而更占** | 本文实测 | §3.4 |
| `GCHeapHardLimit` 只是**上限护栏**，不会让堆变小；本次设 64 MB 后 `AVAIL=64.0`，但空载用量不变 | 本文实测 | §3.4 |

### 2.4 其它官方口径

| 结论 | 性质 | 出处 |
|---|---|---|
| 后台任务/多进程会把内存记到不同进程；评估「整进程」时要按进程分别量 | 官方说明 | [MemoryManager](https://learn.microsoft.com/en-us/uwp/api/windows.system.memorymanager) |
| `Process.WorkingSet64` 是含共享页的工作集；`PrivateMemorySize64` 是私有提交。两者不能互相替代 | 官方 API | [Process.PrivateMemorySize64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64) |

### 2.5 明确标为「社区经验」的说法

- 「C++/WinRT 比 C# 省很多内存」——常见但含糊；本次实测确认空载省 ~9.6 MB private / ~36.5 MB WS，但**每项数据成本并不省**（§3.2）。
- 「位图缓存会自动按显示尺寸缩放」——**错**；不设 `DecodePixelWidth`/`DecodePixelHeight` 就按原图解码。
- 「`GC.Collect` 能省内存」——平时反而伤性能；仅官方给出的后台释放场景值得调用。

---

## 3. 实测：C# 与 C++/WinRT 的内存占用

### 3.1 空载宿主（XAML 岛，空 Grid）

用现有 `IslandProbe`（Win32 + `DesktopWindowXamlSource` + .NET 岛宿主）复制一份到临时目录，加 `--empty 1` 把内容换成空 `Grid`；C++/WinRT 版是同一结构的最小重写（`WindowsXamlManager::InitializeForCurrentThread()` + `DesktopWindowXamlSource` + 空 `Grid`）。

| 宿主 | working set | private bytes | 其中私有 WS | 其中共享 WS | 采样时机 |
|---|---|---|---|---|---|
| C# 岛宿主（CoreCLR） | **87.7 MB**（88.4–90.3） | **22.2 MB**（23.1–23.3） | 15.2 MB | 72.5 MB | 首帧后 3–5 s |
| C++/WinRT 岛宿主 | **51.2 MB**（51.2–56.6） | **12.6 MB**（12.6–13.0） | 8.8 MB | 42.3 MB | 同样时机 |
| 差值（C# − C++） | **+36.5 MB** | **+9.6 MB** | +6.4 MB | +30.2 MB | — |

C++ 版在首帧前的 `BASE` 是 WS 41.2 MB / PVT 8.2 MB，说明 **XAML 初始化 + 窗口/合成本身就要 ~41 MB WS / ~7 MB private**，与语言无关。

**按模块归因**（`MemMap.exe`，同一时机采样；单位 MB，`priv`/`shared` 为驻留页拆分）：

| 模块 | C# 宿主 | C++ 宿主 | 说明 |
|---|---|---|---|
| Windows.UI.Xaml.dll（系统 XAML） | 0.1 / 6.0 | 0.1 / 5.7 | 两版共用，基本相同 |
| OneCoreUAPCommonProxyStub.dll | 0.0 / 4.2 | 0.0 / 4.2 | XAML/合成代理，完全相同 |
| igc64.dll / igd10um64gen11 / igdgmm64（核显驱动） | 0.5+0.1+0.0 / 3.4+1.6+0.9 | 同 | 完全相同 |
| d2d1 / dcomp / d3d11 / dwrite | — / 1.3+1.1+1.0+0.8 | 同 | 完全相同 |
| System.Private.CoreLib.dll | 0.2 / **5.8** | — | 仅 C# |
| Microsoft.Windows.SDK.NET.dll | 0.0 / **4.5** | — | 仅 C#（WinRT 投影） |
| Microsoft.Windows.UI.Xaml.dll | 0.0 / **2.4** | — | 仅 C#（托管 XAML 投影） |
| coreclr.dll | 0.1 / **2.0** | — | 仅 C# |
| clrjit.dll | 0.0 / **1.6** | — | 仅 C# |
| WinRT.Runtime.dll / hostfxr / hostpolicy | — / 0.4+0.3+0.3 | — | 仅 C# |
| icu.dll | 0.0 / 0.9 | — | 仅 C#（全球化） |
| **未归因到模块**（堆/栈/运行时分配/共享节） | **12.3 / 11.8** | **6.5 / 2.0** | C# 多 +5.8 私有、+9.8 共享 |

**这十几 MB 的差距来自哪一层**：

1. **CoreCLR 运行时层：~7.4 MB private。** 用无 UI 的探针量：CoreCLR 控制台空载 PVT **8.8 MB**，同结构 C++ 控制台 **1.4 MB**。差额即 `coreclr.dll` + JIT + GC 结构 + 托管堆底噪。
2. **托管 XAML 投影 + 应用层：~2.2 MB private。** C# 宿主 22.2 − (8.8 + XAML 公共 12.6−1.4=11.2) ≈ 2.2 MB；对应 `Microsoft.Windows.SDK.NET.dll`、`Microsoft.Windows.UI.Xaml.dll`、`WinRT.Runtime.dll` 与每个 XAML 对象的 RCW/CCW。
3. **共享页层：~30 MB working set。** 主要是 `System.Private.CoreLib` 5.8 + `Microsoft.Windows.SDK.NET` 4.5 + 托管 XAML 2.4 + `coreclr` 2.0 + `clrjit` 1.6 + `icu` 0.9 + host 0.6 + 若干 `System.*`，以及 ~9.8 MB 未归因共享页（.NET 运行时映射节）。**这部分不占 private bytes，但计入每个进程的 working set**。
4. **XAML 框架 + 宿主窗口 + 合成层：两版相同。** 系统 XAML、D3D/DComp、核显驱动、Shell 代理合计约 42 MB 共享 WS / 7–8 MB private —— 这层换语言动不了。

### 3.2 10 万条目模型

纯数据模型，不绑定 UI。C# 的 `FullItem`：`Name`、`Path`、`Size`、`Modified`、`Type`；C++ 的 `FullItem`：`hstring name`、`hstring path`、`hstring type`、`int64_t size`、`DateTime modified`。`Lean` 去掉 `Path` 与 `Type`。字符串均运行时构造，避免驻留。

| 场景 | working set 增量 | private 增量 | 每项 private | 托管堆 / 原生堆 |
|---|---|---|---|---|
| C# `full` 100k（CoreCLR） | +35.0 MB | **+33.1 MB** | **343.6 B** | GC 堆 28.1 MB（256 B/项） |
| C++ `full` 100k | +33.3 MB | **+34.2 MB** | **358.2 B** | 原生堆忙碌 27.4 MB |
| C# `lean` 100k（CoreCLR） | +17.7 MB | **+15.5 MB** | **158.9 B** | GC 堆 10.2 MB（104 B/项） |
| C++ `lean` 100k | +10.2 MB | **+10.6 MB** | **110.8 B** | 原生堆忙碌 8.4 MB |
| C# `full` 100k（NativeAOT） | +30.1 MB | +28.5 MB | **297.9 B** | GC 堆 28.0 MB |
| C# `lean` 100k（NativeAOT） | +13.2 MB | +11.5 MB | **118.8 B** | GC 堆 11.0 MB |

要点：

- **完整模型两种语言基本相同（344 vs 358 B/项）**。10 万项 33–36 MB，主要被三个字符串吃掉；C++ 的 `hstring` 是引用计数堆串，同样要单独分配，所以**并不省**。
- **精简模型 C++ 省 ~48 B/项（约 4.8 MB/10 万）**，因为 `std::vector<LeanItem>` 的槽位没有 GC 对象头/引用开销。这是换语言在「数据模型」上唯一可量化的收益，量级 <5 MB。
- **每项一个 WinRT 对象是灾难**。同一测量语义下：C# `PropertySet` **2563.8 B/项**（+245 MB @100k），C++ `PropertySet` **1588.4 B/项**（+152 MB @100k）；C++ 的 `Uri` 为 1122.9 B/项（类型不同，仅供量级参考）。**C# 投影每项比原生贵约 0.98 KB**，但两者都比「纯模型」贵 4–15 倍。文件列表千万不要用 `StorageFile`/`PropertySet` 之类 WinRT 对象做项模型。

### 3.3 位图（已解码 `SoftwareBitmap`）

`BitmapDecoder.CreateAsync` → `BitmapTransform{ScaledWidth/ScaledHeight = size, InterpolationMode = Fant}` → `GetSoftwareBitmapAsync(Bgra8, Premultiplied)`，保持 200 张引用，测 `(AFTER − BASE) / 200`。

| 解码尺寸 | working set（AFTER） | private（AFTER） | 每张 private | 每张托管堆 | BGRA8 理论像素字节 |
|---|---|---|---|---|---|
| 64×64（源 512×512 PNG） | 65.7 MB | 17.2 MB | **43 233 B** | 769 B | 16 384 B |
| 256×256 | 119.7 MB | 71.4 MB | **327 066 B** | 732 B | 262 144 B |
| 512×512 | 269.8 MB | 221.9 MB | **1 116 508 B** | 769 B | 1 048 576 B |

- 像素缓冲是**原生内存**（WIC/解码器），不落在托管堆；托管堆每张只 ~0.75 KB。
- 小尺寸有一笔固定开销（64 px 实际 43 KB vs 理论 16 KB），但**尺寸越大越接近线性**：512→256 是 3.4 倍，256→64 是 7.6 倍。
- 结论：**保留 200 张 512 px 就是 ~220 MB private**；同一批按 64 px 只 ~8.6 MB。若列表只显示 48–64 px 缩略图，必须按 64/96/128 解码，且只保留可见区 ± 一屏。

### 3.4 GC 配置对照（C#）

| 配置 | 空载 WS | 空载 PVT | `full` 100k 每项 PVT | `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` |
|---|---|---|---|---|
| 工作站 GC（默认） | 34.8 | **8.8** | 343.6 B | 8052.6 MB |
| 服务器 GC（`DOTNET_gcServer=1`） | 35.2 | **13.3**（+4.5） | 351.8 B | 8052.6 MB |
| 工作站 + `DOTNET_GCHeapHardLimit=0x4000000` | 34.0 | 8.7 | — | **64.0 MB**（生效） |

- 服务器 GC **增加**固定内存，且不改善每项成本 → 桌面端保持工作站 GC。
- 硬上限只改变「可用预算」报告值，不会主动把堆压小 → 只能当护栏，不能当优化手段。

### 3.5 NativeAOT 对照（同一探针）

| 场景 | CoreCLR WS / PVT | NativeAOT WS / PVT | 差额 |
|---|---|---|---|
| 空载 | 34.8 / 8.8 | **11.5 / 4.4** | −23.3 / **−4.4 MB** |
| `full` 100k | 68.9 / 41.7 | 40.9 / 32.9 | −28.0 / **−8.8 MB** |
| `full` 每项 | 343.6 B | **297.9 B** | −45.7 B（−13%） |
| `lean` 100k | 51.7 / 24.1 | 24.0 / 15.8 | −27.7 / **−8.3 MB** |
| `lean` 每项 | 158.9 B | **118.8 B** | −40.1 B（−25%） |

NativeAOT 能显著削掉「运行时固定开销」与一部分每项开销（主要是 GC 头/托管结构），但**位图、XAML 框架、Shell 缩略图完全不受影响**。注意：AOT 版 `MemProbe` 可构建运行，但**「NativeAOT + UWP XAML 岛宿主」本次没有构建成功**（`PublishAot=true` 对 `UseUwp` 工程未产出 AOT 文件，见 §6）。

### 3.6 换语言到底省什么、不省什么

| 部分 | 换 C++/WinRT 能否省 | 本机量化 |
|---|---|---|
| CoreCLR 运行时固定开销 | ✅ 省 | ~7.4 MB private（8.8 → 1.4） |
| 托管 XAML 投影/应用层 | ✅ 省 | ~2.2 MB private + ~30 MB 共享 WS |
| 每项**纯数据模型** | ❌ 不省（完整模型甚至略差） | 343.6 → 358.2 B/项 |
| 每项**精简模型** | ⚠️ 小幅省 | 158.9 → 110.8 B/项（−4.8 MB/10 万） |
| 每项 **WinRT 对象** | ⚠️ 省约 38%，但仍很贵 | 2563.8 → 1588.4 B/项（仍 >1.5 KB） |
| 位图解码/像素缓冲 | ❌ 不省（同为 WIC） | 43 KB@64px、327 KB@256px、1.12 MB@512px |
| XAML 元素树/系统 XAML/合成 | ❌ 不省 | 两版共享页几乎逐模块相同 |
| Shell 图标/缩略图 | ❌ 不省 | 详见 PERF-RESEARCH §6 |

**一句话**：换语言买的是「一个更小的空壳」，大约 **9–10 MB private / 36 MB WS**；**买不到**「更省的数据和图片」。数据量越大，换语言的相对收益越小。

---

## 4. 「整进程 20 MB」是否可达

分两种口径回答，避免把 WS 与 private bytes 混为一谈：

| 目标 | 结论 | 依据 |
|---|---|---|
| **working set < 20 MB** | **不可达** | C++ 空载岛宿主 WS 已 51.2 MB，其中 42.3 MB 是系统 XAML/D3D/DComp/Shell 代理等共享页；裸 Win32 进程也要 ~9 MB。只要使用系统 XAML 岛，WS 的下限就在 40 MB 量级 |
| **private bytes < 20 MB** | **可达，但取决于宿主语言** | C++/WinRT 版**实测 12.6 MB**（已达标）；C# 版**实测 22.2 MB**（略超）；NativeAOT 若可用于 XAML 岛，按 §3.5 的 −4.4 MB 推算约 18 MB，**未实测** |

补充参照（均为本机实测，同口径）：

- 裸 C++ 控制台：WS 8.9 / PVT 1.4 MB
- 裸 CoreCLR 控制台：WS 34.8 / PVT 8.8 MB
- 裸 NativeAOT 控制台：WS 11.5 / PVT 4.4 MB

**未测**：真正的「最小 UWP 空白应用」。本机没有安装 UWP 项目模板/扩展 SDK（VS 仅有 WindowsXaml Markup 目标，无 `UWP` 扩展 SDK 与项目模板），无法构建可注册运行的 UWP 空白包。从构成推断，它至少包含系统 XAML + CoreWindow + 合成，WS 不会低于 C++ 岛宿主的 51 MB，private bytes 预计与 C++ 岛宿主同量级或更高；**这是推断，不是实测，不能当结论引用**。

---

## 5. 按优先级排序的省内存手段

> 收益量级均来自本文实测；「改造成本」按本项目当前形态（Win32 后端 + UWP XAML 岛）估计。

### P0 必须做（收益 ≥ 10 MB 或与数据量线性相关）

| # | 手段 | 预期收益 | 代价 | 副作用 |
|---|---|---|---|---|
| 1 | **列表项不要用 WinRT 对象**（不用 `StorageFile`/`PropertySet`/`StorageFolder` 做项模型，用纯 CLR / 扁平结构） | 每项省 **1–2.5 KB**；10 万项省 **150–250 MB** | 低：改数据模型 | 无；反而更快（见 PERF §7） |
| 2 | **位图按显示尺寸解码**（`BitmapImage.DecodePixelWidth` 或 `BitmapDecoder`+`BitmapTransform`），只保留可见项 ± 一屏 | 512→64 px 每张省 **1.07 MB**（26×）；200 张省 **~210 MB** | 中：缩略图管线改造 | 放大到全屏会略糊；需按 DPI/显示尺寸算目标像素 |
| 3 | **项模型精简**（只留显示字段；类型名用扩展名映射表；路径按需拼） | `full`→`lean` 10 万项省 **10.6 MB**（C++）/ **17.6 MB**（C#，PVT 33.1→15.5） | 低：删字段 | 需要时再补全字段（按需查询） |
| 4 | **列表虚拟化 + 元素回收**（`ListView/GridView` 默认面板或 `ItemsRepeater`；简单 ItemTemplate，避免嵌套 `ScrollViewer`） | 决定「实际创建多少个元素」；元素树是列表侧唯一随可见行数增长的部分 | 中：模板/布局审查 | 无；过度简化模板可能影响体验 |

### P1 应当做（收益中等或取决于峰值）

| # | 手段 | 预期收益 | 代价 | 副作用 |
|---|---|---|---|---|
| 5 | **`x:Load` / `x:Phase`** 延迟非首屏元素与次级绑定 | 首屏元素数下降，启动内存与时间改善；量级取决于模板复杂度（本次未量化） | 中：XAML 改造 | 需要处理加载时序（`x:Load` 的 `Loaded`/`Unloaded`） |
| 6 | **缓存上限 + `MemoryManager` 订阅**：图片/缩略图缓存设 LRU 上限；订阅 `AppMemoryUsageIncreased`，`High`/`OverLimit` 时清缓存 | 把峰值内存从「无上限」压到「上限 + 可见集」；避免 OOM | 中 | 清缓存后需要重取，可能短暂变慢 |
| 7 | **进入后台/窗口最小化时释放视图与缓存**（官方 `ReduceMemoryUsage` 模式） | 后台常驻内存可降到接近空载；桌面应用需自己触发（如窗口最小化） | 低-中 | 恢复时需要重建视图 |

### P2 视情况做（收益小或代价高）

| # | 手段 | 预期收益 | 代价 | 副作用 / 结论 |
|---|---|---|---|---|
| 8 | **换 C++/WinRT 宿主** | 空载省 **9.6 MB private / 36.5 MB WS**；数据越多相对收益越小 | **高**：重写宿主、调试 COM/XAML 互操作 | 只在硬指标 <20 MB private 时值得；不解决位图/数据成本 |
| 9 | **NativeAOT** | 空载省 **4.4 MB**，10 万项省 **8.8 MB**（每项 −13%~−25%） | 高：XAML 岛 AOT 未验证成功 | 可能不可用；即便可用也只省运行时层 |
| 10 | **`GCConserveMemory` / 调 `Gen0` 大小** | 让 GC 更保守，通常只影响堆的「保留量」而非峰值数据占用；本次未单独量化 | 低 | 可能增加 GC 次数/CPU |
| 11 | **`GCHeapHardLimit`** | **不省内存**，只限制上限；本次实测仅改变 `AVAIL` 报告值 | 低 | 设太小会 OOM/频繁 GC；只当护栏 |

### 明确「不值得」

- **为了省内存换更底层的枚举 API**（`NtQueryDirectoryFileEx` 等）：枚举不是内存问题，且更底层 API 的记录更大（见 PERF §2）。
- **给字符串做自定义压缩/池化**：10 万项完整模型才 33–36 MB，而位图 200 张 512 px 就 220 MB；投入产出不成比例（除非项数上百万）。
- **用 `GC.Collect` 当常规优化**：官方明确说平时不要强制 GC。
- **为了省内存把整个宿主换成 C++ 但保留每项 WinRT 对象**：每项 1.6 KB 的开销与语言无关，换了也白换。

---

## 6. 不确定项与未完成的测量

1. **最小 UWP 空白应用未测**：本机无 UWP 项目模板/扩展 SDK，无法构建可注册的 UWP 空白包。§4 中对它的判断是**推断**。
2. **NativeAOT + UWP XAML 岛未验证**：对 `UseUwp` 工程传 `PublishAot=true` 未产出 AOT 输出（目录为空）。§3.5 的「AOT 省 4.4 MB」是在**无 XAML 的同探针**上实测的；套到 XAML 宿主上属推断。
3. **XAML 列表渲染内存未测**：没有搭完整的 10 万项 `ListView`/`ItemsRepeater` 界面；本文只测了「模型常驻」与「位图常驻」。`x:Load`/`x:Phase`/元素回收的收益按官方文档引用，未在本机量化。
4. **未清 OS 文件缓存**：位图解码只保证「新路径首次解码」，不保证磁盘冷缓存；本文不声称磁盘冷热。
5. **C++ 的 `Uri` 与 C# 的 `PropertySet` 不是同一类型**：§3.2 的跨语言 WinRT 对象对比以 `PropertySet` 为准（两语言同类型）；`Uri` 仅作量级参考。
6. **共享页归因有 ~9.8 MB 未落到模块**：`MemMap` 只能归因到已枚举模块，.NET 运行时的内存映射节可能不在模块列表内；已标为「未归因」。
7. **测量抖动**：空载 WS 在同一配置三次内 51.2–56.6 MB（±5 MB）；private bytes 波动 <1 MB。**小于 5 MB 的 WS 差异不应解读为语言差异。**

---

## 7. 复现方法

```powershell
# C# 内存探针（CoreCLR）
C:\Users\gold\perf-research\MemProbe\bin\Release\net10.0-windows10.0.26100.0\win-x64\MemProbe.exe baseline
C:\Users\gold\perf-research\MemProbe\bin\Release\net10.0-windows10.0.26100.0\win-x64\MemProbe.exe full 100000
C:\Users\gold\perf-research\MemProbe\bin\Release\net10.0-windows10.0.26100.0\win-x64\MemProbe.exe lean 100000
C:\Users\gold\perf-research\MemProbe\bin\Release\net10.0-windows10.0.26100.0\win-x64\MemProbe.exe uri 100000
C:\Users\gold\perf-research\MemProbe\bin\Release\net10.0-windows10.0.26100.0\win-x64\MemProbe.exe decode C:\Users\gold\perf-research\data\thumbs 200 256

# C# NativeAOT 版
dotnet publish -c Release -p:PublishAot=true -p:Platform=x64 -o C:\Users\gold\perf-research\MemProbeAot
C:\Users\gold\perf-research\MemProbeAot\MemProbe.exe full 100000

# C++/WinRT 探针（需先进入 VS Developer PowerShell）
C:\Users\gold\perf-research\MemProbeCpp\MemProbeCpp.exe baseline
C:\Users\gold\perf-research\MemProbeCpp\MemProbeCpp.exe full 100000
C:\Users\gold\perf-research\MemProbeCpp\MemProbeCpp.exe lean 100000
C:\Users\gold\perf-research\MemProbeCpp\MemProbeCpp.exe pset 100000

# 空载岛宿主（C#）
C:\Users\gold\perf-research\IslandHostCs\bin\x64\Release\net10.0-windows10.0.26100.0\IslandProbe.exe --empty 1 --hold 6000 --capture 3000
# 空载岛宿主（C++/WinRT）
C:\Users\gold\perf-research\IslandHostCpp\IslandHostCpp.exe 3000

# 按模块归因工作集（先让宿主进程跑起来，再对 PID 采样）
C:\Users\gold\perf-research\MemMap\MemMap.exe <pid>
```

构建命令（PowerShell，避免 `cmd /c`）：

```powershell
Import-Module 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:\Program Files\Microsoft Visual Studio\18\Community' -SkipAutomaticLocation -DevCmdArguments '-arch=x64'
cl /nologo /std:c++20 /EHsc /O2 /MD /permissive- /Zc:twoPhase- main.cpp /Fe:xxx.exe /link WindowsApp.lib shell32.lib ole32.lib user32.lib Psapi.lib
```

---

## 8. 参考链接

官方（本文引用）：

- [Free memory when your app moves to the background](https://learn.microsoft.com/en-us/windows/uwp/launch-resume/reduce-memory-usage)
- [MemoryManager 类](https://learn.microsoft.com/en-us/uwp/api/windows.system.memorymanager)
- [GC configuration（DOTNET_gcServer / GCHeapHardLimit / GCConserveMemory）](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- [Workstation vs server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)
- [Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
- [GC.GetGCMemoryInfo](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getgcmemoryinfo)
- [BitmapImage.DecodePixelWidth](https://learn.microsoft.com/en-us/windows/uwp/api/windows.ui.xaml.media.imaging.bitmapimage.decodepixelwidth)
- [BitmapDecoder](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapdecoder) / [BitmapTransform](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform)
- [x:Load](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-load-attribute)
- [Optimize your XAML layout](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-your-xaml-layout)
- [Optimize ListView and GridView](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-listview-and-gridview)
- [ItemsRepeater](https://learn.microsoft.com/en-us/windows/uwp/design/controls-and-patterns/items-repeater)
- [Process.PrivateMemorySize64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64)

待核实：原计划的 `debug-test-perf/optimize-images` 页面当前 404，已用上面两个 Bitmap API 页替代；若后续恢复可补回。
