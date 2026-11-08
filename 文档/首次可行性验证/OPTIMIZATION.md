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
| R2 | 测量口径（不改行为） | 新增 `[MODEL]` 分解；`estModel` 改为与之一致（每项 5 个字符串头 + 逐项字符内容） | managed 42.80 不变；`[MODEL]` obj+slot 11.44 / strHead 11.44 / content 18.69 / total 41.58、去重感知 28.42 MB；firstPaint 109、feedDone 591、enum 537、hbGap 421 | 只量化。确认 `FullPath` 单独 5.4M 字符 ≈10.3 MB，是最大单项 |
| R3 | 路径驻留 | 去掉每项 `FullPath`，改存共享父目录 `_dir`（一份）+ `FullPath` 懒拼 | managed 42.80 → 29.83；private 91.1 → 83.5；[MODEL] content 18.69 → 8.39、total 41.58 → 28.61、去重 28.42 → 15.45；enum 537 → 409、feedDone 591 → 542、hbGap 421 → 400、firstPaint 109 | 达成主要降幅（−12.97 MB），无耗时退化；排序/点击/滚动回归正常 |
| R4 | 显示字符串 | `DisplayTextPool`：单次枚举内去重 SizeText/DateText/TypeText（各上限 8192） | managed 29.83 → 16.87；private 83.5 → 65.9；enum 409 → 356、feedDone 542 → 418、hbGap 400 → 307、firstPaint 87 | 达成（−12.96 MB）。代价是枚举线程多 3 个字典（<0.2 MB 峰值），耗时反而更低 |
| R5 | 未消费字段 | 删除 `FileEntry.Size`/`Modified`（模板只用 SizeText/DateText/TypeText） | managed 16.87 → 15.34；private 65.9 → 67.8（池噪声）；obj+slot 10.68 → 9.92；enum 356 → 328、feedDone 418 → 409、hbGap 363、firstPaint 66 | 小幅达成（−1.53 MB）。private 略升属进程池波动，不作为回归 |
| R6 | 图标缓存 | 上限 160 + LRU，只淘汰离屏项；滚动时回填可见项 | cache 封顶 160；6 个停靠点累计 loaded 236 / evicted 76，各点 `visibleMissing=0`；`estIcons` 上限 0.62 MB | 达成：稳态常驻不再随滚动增长。重解码代价见 4.5，与基线同量级 |
| R7 | 枚举缓冲 | 评估后不改代码：现有实现是“单批 List + 时间片发批”，批次 List 用完即弃，无整表快照 | bufferPeak 20000（= 单批上限，仅是引用数组 ≈160 KB 瞬时）、10k 时 9970（一次发完） | 无收益：条目对象本来就直接进集合，不做复制；池化最多省 ~0.16 MB 瞬时并引入跨线程归还竞态 → 不做（如实记为“未实施”） |
| R8 | 遮挡降级 | 见第 5 节 | 见 5.2/5.3 | 部分达成：WS 可降到 1–7 MB，private 基本不动（只省下 tier1 的托管部分） |

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

## 4. 条目模型瘦身（R2–R7，2026-11-08 晚）

口径同第 0 节：100k = `C:\Users\gold\perf-research\data\f100k`，`--memgc 1`，desktoppane，强制 GC 后存活。

### 4.1 R2：先量清楚再动手

新增 `[MODEL]` 分解（`--memgc` 时全量扫描集合一次），把模型拆成四块：

| 组成 | 基线（FullPath 驻留） | 说明 |
| --- | --- | --- |
| obj+slot | 11.44 MB | `FileEntry` 对象 + `ObservableCollection` 槽，按 104 B/项 计 |
| strHead | 11.44 MB | 每个字符串引用 20 B 头 ×5（Name/SizeText/DateText/TypeText/Glyph）+ FullPath |
| content | 18.69 MB | 9.8M 字符 ×2 B；其中 **FullPath 5.4M 字符 ≈10.3 MB**（58% 的内容） |
| total / 去重感知 | 41.58 / 28.42 MB | 去重感知版假设 SizeText(1 种)/DateText(4 种)/TypeText(1 种) 共享 |

- 结论：路径是最大单项；显示文本原始 2.8M 字符但只有 6 个不同值（`distinct size=1 date=4 type=1 glyph=1`），共享空间约 5.5 MB；Name 1.5M 字符（100k 个不同文件名）不可去重，是硬底。
- 本项不改行为，只是把后续每轮的收益可归因。

### 4.2 R3：父目录前缀共享

- 改法：`FileEntry` 只存共享的 `_dir`（同一次枚举内一份），`FullPath` 改为 `Path.Combine(_dir, Name)` 懒拼；枚举侧把 `dir` 传进去一次。
- 结果：managed 42.80 → 29.83 MB（−12.97），private 91.1 → 83.5，`[MODEL]` content 18.69 → 8.39、去重感知 28.42 → 15.45；enum 537 → 409、feedDone 591 → 542、hbGap 421 → 400、firstPaint 109。无耗时退化。
- 判定：这一轮拿走了一半以上，且没有用速度换内存。

### 4.3 R4：显示字符串去重池

- 改法：`DisplayTextPool` 在单次枚举内共享 SizeText/DateText/TypeText（三类各 8192 上限的字典，超出后不再共享、只影响内存不影响正确性）。
- 结果：managed 29.83 → 16.87（−12.96），private 83.5 → 65.9；enum 409 → 356、feedDone 542 → 418、hbGap 400 → 307、firstPaint 87。
- 判定：达成。100k 实测 distinct 只有 1/4/1，全部命中；字典本身峰值 <0.2 MB。

### 4.4 R5：删掉未消费字段

- 改法：删除 `FileEntry.Size`/`Modified`（模板与排序只用 `SizeText`/`DateText`/`TypeText`）。
- 结果：managed 16.87 → 15.34（−1.53），obj+slot 10.68 → 9.92；enum 356 → 328、feedDone 418 → 409、hbGap 363、firstPaint 66。
- 判定：小幅达成。private 65.9 → 67.8 的 +1.9 MB 是进程池/保留段波动，不是回归。

### 4.5 R6：图标缓存上限 + LRU

- 改法：`MaxCachedIcons=160`，`LinkedList` + `Dictionary` 做 LRU；只淘汰离屏项（`_realizedEntries` 在屏项移到表尾跳过）；容器回收（`InRecycleQueue`）时从“在屏集合”移除，重新可见时 `QueueIcon` 回填。
- 实测（一次滚动扫描 50→25→50→75→100→0%）：

| 停靠点 | loaded 累计 | evicted 累计 | cache | visibleMissing |
| --- | --- | --- | --- | --- |
| 50% | 79 | 0 | 79 | 0 |
| 25% | 119 | 0 | 119 | 0 |
| 50% | 119 | 0 | 119 | 0 |
| 75% | 159 | 0 | 159 | 0 |
| 100% | 198 | 38 | 160 | 0 |
| 0% | 236 | 76 | 160 | 0 |

- 重解码代价：每停靠到一个新位置要补 ~40 个图标，`view(moving)` → `view(settle)` 约 1.4–2.1 s（含 Shell 缩略图往返）；与无 LRU 基线（79/79 也约 1.5 s）同量级，没有明显退化。
- 常驻上限：160 × 32×32×4 = 0.62 MB 位图（`estIcons` 0.62 MB 封顶，托管侧只有引用）；滚动到底不再无限增长。
- 判定：达成。代价是来回滚动会重复解码，但只对可见区域，且 `visibleMissing` 每次都能在观察窗内回填到 0。

### 4.6 R7：枚举缓冲评估（未实施）

- 现状：`FindFileEnumerator` 边扫边发，每批一个 `new List<FileEntry>`，`FileBatch.Items` 直接交给 UI 线程把引用搬进集合后用弃；**不存在整表快照，也没有复制条目**。
- `bufferPeak=20000` 只是“单批上限”，折算瞬时内存 ≈160 KB（20000 × 8 B 引用）；10k 目录 `bufferPeak=9970`（一次发完）。
- 评估结论：复用缓冲最多省这 ~0.16 MB，且要跨线程归还（UI 线程消费完才能还）会引入竞态；R1 已证明把批次从 20k 降到 5k 只会让批次数变多、不降最大停顿。**判定不划算，本轮不做**（如实记录为“未实施”，不报收益）。

### 4.7 终态数字与结构性下限

100k 终态（log 220623，`--occ 0` 隔离遮挡干扰）：

| 场景 | WS / Private | managed | estModel | bufferPeak |
| --- | --- | --- | --- | --- |
| 空载 idle | 94.0 / 24.1 MB | 0.44 MB | 0 | 0 |
| feedDone | 159.5 / 64.4 | 15.35 | 14.69 | 20000 |
| iconsSettle | 175.4 / 79.5 | 15.54 | 14.69 | 20000 |
| scrollSettle | 172.8 / 76.7 | 15.56 | 14.69 | 20000 |
| 同进程二次 feedDone | 174.0 / 78.7 | 15.50 | 14.69 | 20000 |

基线与终态对照：

| 指标（100k feedDone） | 基线 | 终态 | 变化 |
| --- | --- | --- | --- |
| managed | 42.80 MB | 15.35 MB | **−27.45** |
| 自持（managed − idle） | 42.36 MB | 14.91 MB | **−27.45** |
| Private | 89.5 MB | 64.4 MB | −25.1 |
| WS | 184.7 MB | 159.5 MB | −25.2 |
| `[MODEL]` | total 41.58 / 去重 28.42 | total 27.85 / 去重 14.69 | −13.73 / −13.73 |
| 枚举 enumMs | 320–660 | 328–499（热）/ 924–1361（冷） | 未退化 |
| 喂入 feedDone | 489–1360 | 800 / 760 | 落在基线区间 |
| firstPaint | 88–139 | 66–209 | 未退化 |
| hbGap | 169–596 | 548 / 460 | **未达标（预算 50）**；属既有短板，本轮未恶化 |

10k 终态（log 220833）：idle 95.2 / 25.2 / managed 0.44；feedDone WS 127.0 / Private 41.5 / managed 2.03 / `[MODEL]` total 2.78、去重 1.47；firstPaint 125、feedDone 160、enum 86、hbGap 113、realized 79。基线对照：Private 49.7 → 41.5，managed 4.78 → 2.03。

**结构性下限（重要）**

- 终态 `[MODEL]`：obj+slot **9.92 MB**（100k × 104 B，`FileEntry` 对象 + 集合槽，跟架构绑定）、strHead 9.54、content 8.39。
- 其中不可再压的部分：对象+槽 9.92 MB + Name 字符串（头 1.9 + 内容 3.0）≈ **14.9 MB**，与实测自持 14.91 MB 吻合。
- 结论：**在“每项一个 class + INotifyPropertyChanged + ObservableCollection<FileEntry>”这个架构下，10 万项自持最低就是 ~14.9 MB，10 MB 目标不可达**。这不是没试够，是对象头/引用/集合槽的固定成本。
- 若一定要继续往 10 MB 以下走，只有两条结构性改法（本轮不做，需要产品决策）：
  1) SizeText/DateText/TypeText/Glyph 改存池索引（int）而不是字符串引用：可省约 6 MB（→ ~9 MB 自持），代价是模板绑定要改成“索引→字符串”的间接访问；
  2) 换虚拟 item source（按页/按窗口物化 + 淘汰）或值类型条目数组：可到 ~6–7 MB，代价是排序/筛选/滚动条总数语义都要重写。

## 5. 遮挡 / 不可见时降级（R8，独立单变量）

前提：只丢缓存与数据，**保留 XAML 绘制系统**（不销毁 `DesktopWindowXamlSource`、不卸载页面）。

### 5.1 遮挡判定（写清依据）

600 ms 一次 `System.Threading.Timer` 轮询，状态变化才打日志并触发事件；判定顺序：

1. 宿主根窗口（`GetAncestor(host, GA_ROOT)`）不可见 → `host-hidden`；最小化（`IsIconic`）→ `host-minimized`。
2. **前景窗口**：`GetForegroundWindow()` 的矩形与宿主矩形求交，覆盖 ≥95% 宿主面积 → 遮挡。排除 shell 桌面类（`Progman`/`WorkerW`/`Shell_TrayWnd`/`Shell_SecondaryTrayWnd`）。
3. **Z 序**：从宿主按 `GetWindow(root, GW_HWNDPREV)` 逐层向上走（最多 64 层），每个可见非 shell 窗口的矩形与宿主求交，覆盖 ≥95% → 遮挡。
4. 为什么前景要单独判：TopMost 窗口与普通窗口处在不同 Z 序带，`GW_HWNDPREV` 只会在同一带内游走，TopMost 覆盖窗必须直接从前景句柄看（这一条是实测踩坑后补的）。

显示器 `rcMonitor` 的用法：desktoppane 模式下宿主铺满工作区，最大化/全屏窗口的 `GetWindowRect` 就等于 `rcWork`/`rcMonitor` 区域，因此“盖住所在显示器”退化成“矩形覆盖宿主 ≥95%”，无需再单独取 `MONITORINFO.rcMonitor`。不做逐像素、不做 DWM cloaked（虚拟桌面）、不做多窗拼合覆盖。

实测边界（`scripts/test-fileview-uia.ps1 -OccludeTest`，自建无边框窗体）：

| 场景 | 结果 | 日志证据 |
| --- | --- | --- |
| 无覆盖（桌面/终端在后面） | 不触发 | `occluded=0 reason=clear` |
| 100% 全屏覆盖（Bounds=PrimaryScreen） | 触发 | `occluded=1 reason=foreground-covers[WindowsForms10...] cover=100%` |
| 700×500 @ (120,120) 部分遮挡 | 不触发 | `PASS occlude-boundary` |
| 二次全屏 | 再次触发 | `occluded=1 ...` |
| 关闭覆盖窗 | 恢复 | `exit hiddenMs=602`、`restoreMs=770 verdict=OK(<1s)` |

- 排查记录：最初误判 `Progman` 为 100% 覆盖（shell 桌面窗口在 Z 序里在宿主之上），导致枚举期 `MaxBatchItems` 被降到 1500（batches 6 → 68、feedDone 1465 ms）；排除 shell 桌面类后恢复 batches=6、feedDone 1009 ms。

### 5.2 分级降级（各自省了多少）

| 级别 | 动作 | 实测收益 | 备注 |
| --- | --- | --- | --- |
| tier1 | 清图标位图缓存（LRU 全清，`Thumbnail=null`、`ThumbnailState=0`） | 释放 39 个图标，managed −13 KB（第二次 −76 KB） | 位图在原生/显存，不计入托管差值；托管只省引用 |
| tier2 | 非当前 Page 内容不缓存（`Frame.NavigationCacheMode=Disabled`） | **0**（本 demo 只有 PageA/FileViewPage 两页且未设缓存，无可释放页） | 记为“未产生收益” |
| tier3 | 枚举缓冲收紧（`MaxBatchItems` 20000 → 1500） | **本次 0**：遮挡发生时枚举已完成，`bufferPeak` 仍 20000 | 只在“枚举尚未完成”时有意义 |
| tier4 | 强制 GC + `EmptyWorkingSet` | WS 165.1 → 165.1 → **1.3 MB**；private 69.4 不动 | 仅降 WS，**private 基本不变，不等于省内存** |

- 恢复路径：重新可见时把 `MaxBatchItems` 复原为 20000，对在屏且图标缺失的项重新 `QueueIcon`，100 ms 轮询等回填完成。
- 实测恢复耗时：`restoreMs=380 verdict=OK(<1s)`（另一次 `770 ms`）；两次都 ≤1 s，**判定划算**。恢复期间 `view(restore)` 的 `visibleMissing=0`。
- 计时修正：再次进入遮挡时必须停掉恢复计时，否则会得到假的 `restoreMs=1378 SLOW`（已修）。

### 5.3 内存压力自适应（MemoryManager）

- 接入 `Windows.System.MemoryManager.AppMemoryUsageLevel` + `AppMemoryUsageIncreased`（打包上下文有效）。
- 行为：级别为非 `Low` 时清图标缓存（保守做法，宁可多丢）。
- 观测事实（如实记录）：启动后 `[MEMPRESSURE] level=OverLimit usageMB=47.1`，喂入期 `level=OverLimit usageMB=94.5`。即本机在大目录喂入时系统上报一直是 `OverLimit`，按“非 Low 就清”会导致频繁丢缓存——这不是理想语义，属于当前 API 在本场景下的实际表现，本轮不加更细的阈值。

### 5.4 指标口径（必须说清）

- `EmptyWorkingSet`/工作集修剪**只降 working set**：`WS 165.1 → 1.3 MB` 而 `private 69.4 MB` 完全不变。这不算“省了内存”，只是把页面挤出物理内存，后续访问会再缺页拉回。
- tier1 的托管收益是 KB 量级；真正的图标位图在原生/显存，任务管理器口径看不到托管差值。
- 因此 R8 的判定是：**WS 显著可降、private 基本不降、恢复 ≤1 s**；对“整进程 private 跟着降”的目标**没有贡献**。

### 5.5 开关与回归

- `--occ 0` 关闭遮挡检测（避免 IDE/终端盖住桌面时把枚举批次降到 1500 干扰性能口径）；内存/耗时对比全部用 `--occ 0` 重跑。
- 功能回归（`-OccludeTest` 完整脚本）：进入/返回、透明开关、100k 枚举、滚动到 50%（realized 95）、宏键退出全部 PASS，`RESULT failures=0`。

