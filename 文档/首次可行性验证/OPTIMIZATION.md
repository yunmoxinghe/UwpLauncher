# 文件视图性能/内存优化台账

本文件记录「高性能文件显示」优化循环的每一轮：单变量、改法、前后数字、判定。
代码基线：`4f21a3c`（FileViewPage 第一版）+ 本轮只加了测量探针（`[MEM]`、`--memgc`、脚本 `-Repeats`）。

## 0. 测量口径（固定不变）

- 机器/环境：本机单屏 144 DPI，Windows 11；宿主参数 `--mode desktoppane --transparent 1 --fill work`，
  UIA 定位 + `mouse_event` 真实点击进入文件视图；desktoppane 模式下先最小化其他窗口。
- 目录：`C:\Users\gold\perf-research\data\f100k`（100 000 个 0 字节 `file_000000.dat`…，单层、无子目录），
  `f10k` 同构 10 000 个；文件元数据已在系统缓存中（非冷盘）。
- 时间指标（均在 demo 日志中打点，单位 ms）：
  - `firstPaint` = 页面 `StartLoad` → 首批 items 进入 ListView（`[FV] firstBatch atMs`）
  - `feedDone` = `StartLoad` → 枚举完成回调在 UI 线程应用完（`[FV] feed done`）
  - `enumMs` = 后台 `FindFirstFileExW` 线程内计时（`[FV] enum done`）
  - `hbGap` = UI 线程 50 ms `DispatcherTimer` 心跳的最大间隔（喂入期间）
  - 虚拟化 = 滚到 100% 后 `ItemsStackPanel` 实际 realized 的容器数
- 内存指标（`[MEM]`，`--memgc 1` 时先强制 GC，读到的是存活占用）：
  - 整进程：`ws`（WorkingSet64）/ `private`（PrivateMemorySize64）/ `peakWs`（PeakWorkingSet64）
  - 托管：`managed`（GC.GetTotalMemory）
  - GC：`gcDelta` = 本次加载新增 gen0/1/2 次数；`pauseDelta` = `GC.GetTotalPauseDuration` 增量（探针自身触发的 GC 不计入）
  - 自持估算：`estModel` = 每项（FileEntry 112 B + 集合槽 8 B + 6 个字符串头 6×20 B + UTF-16 内容）+ 采样 2000 项外推；
    `estIcons` = 已加载图标数 × 32×32×4 B；`bufferPeak` = 单批最大条目数（枚举缓冲峰值，B 量级按项数记）
- 可重复性：同一进程内“重新枚举”连跑 3 次（`-Repeats 3`）用于同缓存对照；跨会话同配置仍有约 1.5–2× 波动
  （枚举 320–660 ms），因此每轮判定以同会话多跑 + 跨会话复现两方面看，单次极值不作为结论。

## 1. 基线（未优化）

### 1.1 时间（100k，desktoppane）

| # | 场景 | firstPaint | feedDone | enumMs | hbGap | batches | uiApplyMax |
| --- | --- | --- | --- | --- | --- | --- | --- |
| B1 | 单跑 20:50 | 88 | 504 | 348 | 335 | 6 | 20 |
| B2 | 单跑 20:52 | 139 | 847 | 659 | 523 | 6 | 30 |
| B3 | 会话A run1 | 128 | 763 | 638 | 596 | 6 | 13 |
| B4 | 会话A run2（reload） | 892 | 897 | 371 | 314 | 6 | 0 |
| B5 | 会话A run3（reload） | 502 | 510 | 359 | 169 | 6 | 1 |
| B6 | 单跑 21:00（探针版） | 658 | 1360 | 1225 | 378 | 6 | 74 |
| B7 | 单跑 21:02（memgc） | 94 | 489 | 444 | 344 | 6 | 12 |

- 目标：firstPaint < 100、feedDone < 300、hbGap < 50。
- 结论（基线）：feedDone 稳定超过 300（489–1360），hbGap 稳定超过 50（169–596）；firstPaint 在 88–139 波动，靠近目标但不可靠。
- 初步定位（待单变量验证）：每批 20 000 项 + 一次 `Reset` 会让 UI 线程在布局阶段停顿 300–500 ms；
  reload 时 `Items.Clear + Reset`（清空 10 万项）本身还能再停顿 ~400 ms；枚举本身 320–660 ms 也高于 300 ms 预算。
- `workerw`（壁纸层）不可点击，性能测量只用 `desktoppane`/`topmost`；不影响本条结论。

### 1.2 内存（强制 GC 后存活；`--memgc 1`，滚到 100%）

| 场景 | 空载 idle | 首批 | 全量 feedDone | 图标 settle | 滚动到底 |
| --- | --- | --- | --- | --- | --- |
| 100k WS / Private | 94.1 / 24.2 MB | 127.9 / 41.7 | 184.7 / 89.5 | 191.0 / 96.7 | 191.1 / 96.1 |
| 100k managed / estModel | 0.44 / 0 | 4.85 / 0.01 | 42.80 / 28.61 | 42.98 / 28.61 | 43.04 / 28.61 |
| 100k GC delta / pauseΔ | — | — | 13/8/5 / 204 ms | 16/11/8 / 304 ms | 18/13/10 / 364 ms |
| 10k WS / Private | 95.2 / 25.2 | 128.5 / 43.3 | 144.9 / 49.7 | 153.5 / 58.4 | 149.1 / 53.2 |
| 10k managed / estModel | 0.44 / 0 | 4.86 / 0.01 | 4.78 / 2.84 | 4.96 / 2.84 | 4.95 / 2.84 |
| 10k GC delta / pauseΔ | — | — | 5/4/4 / 53.5 ms | 7/6/6 / 83.0 ms | 10/9/9 / 132.0 ms |

- 100k 第二次加载（同进程 reload）后：WS 233.4 / Private 138.9 / managed 42.91 → 存活托管稳定，WS/Private 偏高是未归还的保留段。
- 自持实测 ≈ `managed - 空载`：100k ≈ 42.4 MB（估算 28.6 MB 低估了字符串头）；10k ≈ 4.3 MB。
- 图标：滚动到底只加载约 76 个真图标（≈0.30 MB），因为只对“已实现容器”补图，未做全量预取；尚无 LRU 上限。
- 枚举缓冲峰值：`bufferPeak=20000`（一批 20k 项与已入列表项短暂共存）。
- 目标：自持 < 10 MB（10 万项）。基线 42.4 MB，**不达标**；主要占用是每项的 `FullPath`/`SizeText`/`DateText`/`TypeText` 字符串。
- GC：枚举+喂入全程 pauseΔ 204 ms（100k），gen2 发生 5 次；这是 hbGap 之外的另一处延迟来源。

## 2. 优化轮次（每轮只改一处）

| 轮次 | 变量 | 改法 | before → after | 判定 |
| --- | --- | --- | --- | --- |
| R1 | 批大小 | `MaxBatchItems` 20 000 → 5 000（同会话连跑 3 次） | feedDone 489–1360 → 818/564/826 ms；hbGap 169–596 → 529/251/138 ms；batches 6 → 21 | 未降低最大停顿（hbGap 仍 138–529），反而批次数变多；不能单独达标。改回 20 000 后进入 AOT 对比 |

（后续每轮在此追加一行；保留每轮可回退的最小 diff，行为（排序/路径/点击/透明/嵌入）每次回归。）

## 3. NativeAOT 路线（2026-11-08，一级路线）

### 3.1 构建与打包

- 编译命令（与普通构建同一套 VS MSBuild，只加三个属性）：

  ```
  msbuild WinUI2Demo.csproj /restore /t:Publish /p:Platform=x64 /p:Configuration=Release \
    /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:PublishDir=...\artifacts\aot\
  ```

- 结果：**编译通过**（ILCompiler 生成原生代码，无 AOT 专属错误）。产物只有 `WinUI2Demo.exe`（7.02 MB）+ `WinUI2Demo.pdb` + `resources.pri` + WebView2 两个 DLL；
  原生 exe 不再需要 `WinUI2Demo.dll` / `Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll`（已编进 exe）。
- 打包注意：AOT publish **不会**自动带上 `*.xbf`，需从普通构建输出补齐后放进已注册的 loose 布局；否则 XAML 页解析失败。本轮的验证就是把补好 XBF 的 AOT exe 替换进 `artifacts/msix/WinUI2Demo/layout` 后跑的。

### 3.2 功能清单（包内运行，UIA + 像素采样实测）

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 启动（有包身份） | PASS | `[PKG] Package.Current fullName=WinUI2Demo_1.0.0.0_x64__p06pf12qzbwg4` |
| WinRT 类激活（WinUI2 控件） | PASS | `InfoBar`/`XamlControlsResources` 创建成功（未打包直跑才报 `0x80040154`，属缺包注册） |
| `DesktopWindowXamlSource` | PASS | `AttachToWindow => 0x00000000` |
| `IXamlSourceTransparency` | PASS | set/readback=True；四组（topmost/desktoppane × work/desktop）像素采样 `failures=0` |
| Frame/Page 导航 | PASS | 真实点击进入 FileViewPage → 返回 → 再进入 |
| FileViewPage（100k） | PASS | `enum done count=100000`；滚动 UIA `pct 0→100`、realized=38（O(视口)）；图标 `33/39` 成功 |
| 点击/宏键 | PASS | 页内透明开关、返回、Ctrl+F4 退出均成功 |
| 透明语义 | PASS | `redHalf=RGB(128,0,0)`、任务栏遮挡判定与 CoreCLR 一致（4/4） |
| 一处差异 | 观察缺口 | AOT 下 `FindScrollViewer` 递归返回 null（`Loaded`/`LayoutUpdated` 都触发过），`[FV] scrollviewer attached` 缺失 → 测试脚本的 scroll-settle 断言 FAIL；独立 UIA 探针证明**滚动本身正常**（pct 0→100、realized 38） |

### 3.3 内存对比（`--memgc 1`，100k，desktoppane，强制 GC 后存活）

| 场景 | CoreCLR WS / Private | AOT WS / Private | 变化 |
| --- | --- | --- | --- |
| 空载 idle（managed 0.37–0.44 MB） | 94.1 / 24.2 MB | 56.0 / 15.9 MB | **−38.1 / −8.3 MB** |
| 首批（冷启动） | 127.9 / 41.7 | 88.2 / 31.8 | −39.7 / −9.9 |
| 100k 全量 feedDone（managed 42.8） | 184.7 / 89.5 | 148.9 / 82.8 | −35.8 / −6.7 |
| 图标 settle | 191.0 / 96.7 | 151.6 / 87.8 | −39.4 / −8.9 |
| 同进程二次加载 feedDone | 233.4 / 138.9 | 189.3 / 125.4 | −44.1 / −13.5 |

- `managed` 两侧一致（42.76 vs 42.80 MB），`estModel` 一致（41.58 MB）：**AOT 省下的只是运行时/投影开销，不改变条目模型**。
- 自持实测 ≈ `managed − 空载`：AOT 42.39 MB，CoreCLR 42.4 MB → 距 <10 MB 目标仍差 4 倍。
- GC：AOT pauseΔ 115 ms（gen 11/5/3），CoreCLR 204 ms（13/8/5）——AOT 的 GC 压力略低，但仍存在（GC 是运行时库行为，不是 JIT 行为）。

### 3.4 时间（同口径 100k，仅供参考，单次波动大）

| 轮次 | firstPaint | feedDone | enumMs | hbGap |
| --- | --- | --- | --- | --- |
| AOT run1（冷缓存） | 688 | 991 | 953 | 174 |
| AOT run2（缓存已热） | 62 | 427 | 383 | 342 |
| CoreCLR 对照（同晚，5k 批次） | 716 | 1463 | 1299 | 333 |

- 首屏/全量的波动主要来自 `FindFirstFileExW` 冷启动（670 ms），两种运行时都会遇到；AOT 没有变慢，热缓存下还更快。

### 3.5 结论

- 一级路线**没有失败**：能编译、能启动、功能等价（除滚动观测日志这一处非交互缺口）。
- 但它**不是内存目标的解**：整进程省 ~35–44 MB WS / 7–14 MB Private，自持模型仍是 42.4 MB。
- 按既定路线，一级未失败，**不启动二级 C++/WinRT 宿主**。
- 推荐：交付继续用 CoreCLR 构建（打包链路简单、行为已验收）；把下一轮优化放在条目模型（路径/时间/类型字符串驻留与共享、路径前缀去重、图标缓存上限），AOT 只在需要压低常驻/启动时再启用。

