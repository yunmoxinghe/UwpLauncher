# Windows 文件列表显示性能调研

> 范围：在 Windows 上把一个文件夹的内容（名称、图标/缩略图、大小、时间等）快速显示到 UWP / XAML Island 界面。
> 本机实测时间：2026-11-08，Windows 11 build 26300（Insider），i7-8550U / 8 GB / SATA SSD（NTFS）。
> 配套文档：[Files 上游文件视图方案分析](./FILES-SOLUTION-REFERENCE.md)（本项目主解决方案参考）。
> 临时工程位于 `C:\Users\gold\perf-research\`（仓库外，未提交）。

---

## 0. 结论摘要

1. **枚举不是瓶颈。** 10 万个文件，热目录索引下 `FindFirstFileExW`（`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`）中位数 **145.6 ms**（约 1.5 µs/项）；1 万个文件约 **16 ms**。首屏只需要 30–60 项，枚举本身耗时**不足 1 ms**（不含目录打开与业务对象构造）。
2. **真正的成本在图标/缩略图。** 同一个 10 万文件目录，`IShellItemImageFactory::GetImage` 单项 **8–41 ms**，冷路径 400 张 256 px 需 **14.0 s**，热路径仍需 **3.2–3.4 s**（约 8 ms/张），8 路并发热路径约 **3.1 ms/张**。**图标成本比枚举高 3–4 个数量级**。
3. **Shell 元数据查询是第二个陷阱。** `SHGetFileInfoW` 取属性/类型名约 **3.7–4.1 ms/次**；10 万项就要 6–7 分钟。类型名、属性列应来自扩展名映射表或按扩展名缓存，不能逐项调 Shell。
4. **`Windows.Storage` 不适合做真实目录视图的批量数据源。** `StorageFolder.GetFilesAsync` 1000 项约 **3.4–3.7 s**（3.5 ms/项），再逐个 `GetBasicPropertiesAsync` 约 **7.6–9.6 ms/项**；比 Win32 枚举慢 3 个数量级。
5. **不需要 C++/WinRT。** 同一份数据、同一口径、交替运行、进程优先级对齐后，逐轮差异为 **−12% ~ +31%**，三轮中位数差异在 **±12%** 内且方向反转；缩略图吞吐两者同为约 5 s/400 张。**在本机测量分辨率下无法判定谁更快，也没有观察到 C# 的系统性劣势**。
6. **多目录并发比单句柄异步更值。** 8 个子目录共 8 万文件：1 线程 62.5 ms、2 线程 45.3 ms、4 线程 27.7 ms、8 线程 15.6 ms（≈4×）；而单目录 `NtQueryDirectoryFileEx` 重叠 I/O 深度 8 为 182.8 ms，**比同步 `FindFirstFileExW` Basic（145.6 ms）更慢**（见 §5）。

### 推荐组合（一句话版）

> **Win32 枚举（`FindFirstFileExW` + `FindExInfoBasic` + `LARGE_FETCH`，后台线程、按子目录并发）+ 枚举结果直接带出名称/大小/时间 + 类型图标按扩展名缓存 + 缩略图按显示尺寸异步限流（8 路）+ ListView/GridView 默认虚拟化 + `x:Bind` / `x:Phase` / `x:Load` + 分批喂入（首屏尽快、后续 300–500 ms 一批）。**

不建议把精力放在"换 C++"或"换更底层的枚举 API"上；同样的投入放在缩略图管线与分批喂入上收益大 2–3 个数量级。

---

## 1. 测试环境与数据集

### 1.1 机器与系统

| 项 | 值 |
|---|---|
| CPU | Intel Core i7-8550U（4C/8T，1.8 GHz 基准） |
| 内存 | 7.9 GB |
| 系统 | Windows 11 专业版 10.0.26300（Insider build） |
| 系统盘 | SanDisk SD8SN8U512G1002 SATA SSD，NTFS |
| 实时防护 | Microsoft Defender 默认开启（未加排除项） |
| 测试期间负载 | **高**：CPU 86%–100%，磁盘活动时间约 95%（Trae IDE 与其它进程持续占用） |

> 负载对绝对数字影响很大：同一份 10 万项枚举，在较安静窗口测得 145.6 ms，在高负载窗口同一二进制测得 180–200 ms（约 +25%）。文中所有数字均标注来源窗口；**只做同窗口内的相对比较，不跨窗口比较绝对值**。

### 1.2 数据集

| 目录 | 内容 | 用途 |
|---|---|---|
| `data\f1k` | 1000 个 1 KB `.dat` | `Windows.Storage` 对照 |
| `data\f10k` | 10000 个 1 KB `.dat` | 小规模枚举/属性 |
| `data\f100k` | 100000 个 1 KB `.dat` | 主枚举对照 |
| `data\multi` | `sub1`…`sub8`，每目录 1 万，共 8 万 | 多目录并发扩展性 |
| `data\thumbs` | 400 张 512×512 PNG | 缩略图热路径 |
| `data\thumbs_cold` | 400 张 256×256 PNG（**新路径**） | 缩略图冷路径 |
| `data\thumbs_cold64` | 400 张 128×128 PNG（**新路径**） | 64 px 缩略图冷路径 |

文件均为 1 KB 内容、名字为定长数字编号（`file_000123.dat`），避免名字长度带来的额外差异。

### 1.3 测量口径

- 每个测量项先做 **1 次不计时预热**，再计时 `N` 次（默认 5 次，重 I/O 项 2–3 次），取**中位数**；同时保留每次原始值以观察抖动。
- C# 实现每次计时前执行 `GC.Collect()` + `WaitForPendingFinalizers()`（固定开销 <1 ms，对 10 万项可忽略，对 1 万项需注意），C++ 无此步。
- 进程优先级：C# 启动后自设 `ABOVE_NORMAL_PRIORITY_CLASS`；C++ 在公平对照轮次中由外部设为 `AboveNormal`（原始 C++ 早期数据未设，已在文中标注为不公平/不可比）。
- 计时只覆盖被测调用本身：枚举项只做计数与最小解析，**不含业务对象的分配、字符串格式化、XAML 绑定**。
- **"冷"的定义**：仅指**该文件路径从未进入 Shell 缩略图缓存**（使用新生成目录、首次访问）。**未做** OS 文件缓存/备用内存清除，因此不能声称"磁盘冷缓存"。文中"热"指同一路径第二次及以后访问。

### 1.4 测量精度（重要）

同一进程内、两个等价实现（都走 `FindExInfoStandard`）的差值可达 **18.6 vs 24.7 ms**（1 万项）——即 **±25%** 量级的组内抖动；10 万项约 **±10–15%**。因此：

- 任何小于约 15% 的差异都**不应**被解读为"某语言/某 API 更快"；
- 本文的所有排序结论都只建立在**差异大于抖动下限**的比较上；
- 高负载窗口下的 A/B 数据用于"验证没有系统性差异"，不用于给出绝对排名。

---

## 2. 枚举 API 逐层分析

### 2.1 各层 API 的开销来源

| API | 返回内容 | 调用模型 | 主要开销来源 | 适用场景 |
|---|---|---|---|---|
| `FindFirstFileExW(FindExInfoStandard)` | `WIN32_FIND_DATAW`（含 8.3 短名） | 同步句柄 + 循环 | 内核返回每条记录 592 B；短名查询额外元数据 | 需要短名/兼容老代码 |
| `FindFirstFileExW(FindExInfoBasic)` | 同上但 `cAlternateFileName` 为空 | 同上 | **跳过短名查询**，记录更小 | **默认推荐** |
| `FindFirstFileExW(+FIND_FIRST_EX_LARGE_FETCH)` | 同上 | 同上 | 官方说明："Uses a larger buffer for directory queries, which can increase performance of the find operation." | 大目录批量枚举 |
| `NtQueryDirectoryFile`（`FileDirectoryInformation`, class 1） | 文件名/时间/大小/属性 | 同步，需自己解析变长记录 | 与 Basic 同量级；无短名；需手写解析 | 想避开 Win32 包装、只要基础信息 |
| `NtQueryDirectoryFileEx`（`FILE_ID_BOTH_DIR_INFORMATION`） | 额外含 FileId、短名 | 同步或重叠 I/O | **记录更大**（每条多 100+ B），解析成本更高 | 需要 FileId、或需要 async 重叠 |
| `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` | 同上 | 同步批量缓冲 | 同上；`RestartInfo` 首次调用有额外状态 | 已持有目录句柄、需要 FileId |
| `.NET Directory.EnumerateFiles` | 路径字符串 | 同步迭代器 | 每项分配字符串；内部 Win32 调用 | 通用 .NET 代码，10 万项也可接受 |
| `.NET Directory.EnumerateFileSystemInfos` | `FileSystemInfo`（懒加载属性） | 同步迭代器 | 每项对象分配；首次访问 `Length/LastWriteTime` 才查询 | 需要对象模型且不想 P/Invoke |
| `StorageFolder.GetFilesAsync` | `StorageFile` 对象 | WinRT 异步、逐项 | broker/COM 激活、属性存储、逐项异步往返 | 沙箱/库访问、少量文件 |
| `SHGetFileInfoW` | 显示名/类型名/图标索引 | 同步 | 加载 Shell 扩展、属性系统、图标服务 | 单次取显示名/图标，**不可批量** |

### 2.2 10 万文件实测（C#，5 次中位数，较安静窗口）

| 方法 | 中位数 (ms) | 观测范围 (ms) | 相对 Basic+LF |
|---|---|---|---|
| `FindFirstFileExW` Basic + `LARGE_FETCH` | **145.6** | 130.7–165.6 | 1.00× |
| `FindFirstFileExW` Basic | 145.8 | 131.7–148.0 | 1.00× |
| `FindFirstFileExW` Standard（默认） | 239.8 | 226.3–280.6 | 1.65× |
| `FindFirstFileExW` Standard（另一等价实现） | 230.8 | 208.4–254.0 | 1.58× |
| `NtQueryDirectoryFile`（class 1） | 146.7 | 143.5–151.3 | 1.01× |
| `GetFileInformationByHandleEx`（FileIdBoth） | 205.6 | 184.0–249.8 | 1.41× |
| `NtQueryDirectoryFileEx` 同步（FileIdBoth） | 215.1 | 180.9–248.2 | 1.48× |
| `NtQueryDirectoryFileEx` 异步，深度 8 | 182.8（n=2） | 181.9–183.7 | 1.26× |
| `.NET Directory.EnumerateFiles` | 162.8 | 145.9–179.9 | 1.12× |
| `.NET Directory.EnumerateFileSystemInfos` | 148.3 | 139.1–171.9 | 1.02× |

要点：

- **`FindExInfoBasic` 相对 `FindExInfoStandard` 约快 40%**（145.8 vs 239.8），这是本机最确定的一条优化。
- `LARGE_FETCH` 在本数据集上与 Basic 无统计差异（145.6 vs 145.8）。官方文档只承诺"更大的目录查询缓冲可提升性能"，没有具体数字；对超大目录/网络路径才更可能显现。**建议加上，成本为零。**
- `NtQueryDirectoryFile`（class 1）与 Basic 持平，说明"更底层"本身不带来收益；`NtQueryDirectoryFileEx`/`GetFileInformationByHandleEx` 用的是 `FILE_ID_BOTH_DIR_INFORMATION`，**单条记录更大**，反而慢 26%–48%。
- .NET 的 `EnumerateFileSystemInfos` 与 Win32 Basic 持平（148.3 ms），说明**托管层包装本身不是瓶颈**；`.NET EnumerateFiles` 略慢是因为仅返回字符串但仍走完整查找。

### 2.3 1 万文件实测（C#，5 次中位数）

| 方法 | 中位数 (ms) |
|---|---|
| `FindFirstFileExW` Basic + `LARGE_FETCH` | 15.9 |
| `FindFirstFileExW` Basic | 13.1 |
| `FindFirstFileExW` Standard（默认） | 24.7 |
| `FindFirstFileExW` Standard（重复） | 18.6 |
| `GetFileInformationByHandleEx` | 19.3 |
| `NtQueryDirectoryFileEx` 同步 | 25.4 |
| `NtQueryDirectoryFileEx` 异步深度 8 | 22.5（n=2） |
| `NtQueryDirectoryFile`（class 1） | 12.7 |
| `.NET EnumerateFiles` | 13.5 |
| `.NET EnumerateFileSystemInfos` | 14.2 |

1 万规模下抖动更大（同参数两次测量 24.7 vs 18.6），**不要用这一列做精细排名**。

---

## 3. C# vs C++/WinRT：同口径对照

### 3.1 为什么必须重测

早期 C++ 数据是在机器较安静时采集的，C# 数据则跨越高低负载窗口；且 C# 二进制启动即自设 `ABOVE_NORMAL`，C++ 没有——在 CPU 100% 时这会让 C++ 被系统性饿死（曾观察到 C++ 的 `SHCreateItemFromParsingName` 单项 17 ms vs C# 5–6 ms，属**不公平对照**，已废弃）。

因此做了一轮**公平对照**：两者都由外部以 `AboveNormal` 启动，交替运行，共 3 轮（第 1、3 轮 C++ 先跑，第 2 轮 C# 先跑），同一目录 `f100k`、同一测量代码路径。

### 3.2 结果（10 万文件，中位数）

| 轮次（先跑方） | C# Basic+LF | C++ Basic+LF | C# NtQuery | C++ NtQuery | C# Standard | C++ Standard |
|---|---|---|---|---|---|---|
| R1（C++） | 153.9 | 201.5 | 176.7 | 142.2 | 256.1 | 278.7 |
| R2（C#） | 195.0 | 179.6 | 153.7 | 150.6 | 326.2 | 302.7 |
| R3（C++） | 179.4 | 202.9 | 158.4 | 174.7 | 307.5 | 333.5 |
| 三轮中位数 | 179.4 | 201.5 | 158.4 | 150.6 | 307.5 | 302.7 |

- 差异幅度 **−12% ~ +31%**，且**方向在轮次间反转**（R1 C++ 慢 31%，R2 C++ 快 8%，R3 C++ 慢 13%）。
- 因此结论只能是：**在本机当前测量分辨率下，C# 与 C++/WinRT 在枚举路径上没有可判定的系统性差异**。这一轮所有数字比 §2.2 高 20%–30%，是负载而非语言造成。

### 3.3 缩略图吞吐对照（400 张 256 px，热路径，公平优先级）

| 轮次 | C++ 创建项 | C# 创建项 | C++ 串行 GetImage | C# 串行 GetImage | C++ 并行 8 | C# 并行 8 |
|---|---|---|---|---|---|---|
| R1 | 4.46 s | 6.13 s | 5.17 s | 5.30 s | 1.87 s（抖动大） | 1.88 s |
| R2 | 4.94 s | 4.01 s | 5.67 s | 5.88 s | 1.63 s | 1.53 s |

两语言在**同一量级**，差异同样被负载抖动吞没。图标链路的瓶颈是 Shell 缩略图提供程序 + 进程/COM 边界，与托管/原生无关。

### 3.4 结论与适用条件

- **C# 完全够用**的场景：目录 ≤ 10 万项、列表使用虚拟化、图标/缩略图异步加载、分批喂入。这也是本项目当前形态。
- 只有以下情况才值得考虑 C++/WinRT 或 C++ 助手：
  1. 每帧/每项需要创建大量 WinRT 对象并跨 ABI 传递（例如 10^5–10^6 级别的属性写入或对象构造），且 profiler 证实投影/GC 占 UI 帧时间 >5%；
  2. 对 GC 停顿极度敏感（硬实时式 16 ms 帧预算且不能容忍 GC）；
  3. 内存极受限，需要显著压缩每项常驻对象。
- 注意：若把枚举放到 C++ 助手，**跨 ABI 编组本身可能吃掉收益**；应返回扁平结构（预分配 `struct` 数组/大缓冲），而不是逐项 WinRT 对象。
- 本机在 10 万项规模**无法**复现"原生更快"的常见说法；不要把 C++ 当成枚举性能优化手段。

---

## 4. 单文件属性查询与 Shell 元数据

同一批 1 万个已枚举文件名，逐项再次查询（C# / C++ 各一组，单位 µs/项）：

| 方法 | C# | C++ | 说明 |
|---|---|---|---|
| `GetFileAttributesExW` | 106.6 | 89.6 | 只取属性，仍要一次内核往返 |
| `FindFirstFileExW`（单文件通配） | 194.1 | 140.7 | 打开/关闭查找句柄，比 `GetFileAttributesExW` 更重 |
| `SHGetFileInfoW`（属性 + 类型名） | **4072.6** | **3693.2** | 触发 Shell 属性系统与扩展，毫秒级 |
| `SHGetFileInfoW` + `SHGFI_ICON`（1000 项） | 7303–10921 | 6186–6292 | 单图标 6–11 ms |

结论：

- **不要在枚举之后再逐项 `GetFileAttributesExW`/`FindFirstFileExW`**。10 万项 × 0.1 ms = 10 s，而枚举时顺手取这些字段是"免费"的。
- **类型名、属性、图标绝不要逐项走 Shell**。10 万项 × 4 ms ≈ 400 s。正确做法：扩展名 → 类型名/图标映射表（几十项），按扩展名缓存。
- 每项图标（`SHGFI_ICON`）也在此量级，必须异步 + 缓存 + 只加载可见项。

---

## 5. 多目录并发 vs 单句柄异步

数据集 `data\multi`（8 个子目录 × 1 万 = 8 万文件），`FindFirstFileExW` Basic + `LARGE_FETCH`：

| 并发线程数 | 总耗时 (ms) | 相对 1 线程 | 每万项耗时 (ms) |
|---|---|---|---|
| 1 | 62.5（61.9 / 63.0） | 1.00× | 7.8 |
| 2 | 45.3（44.3 / 46.2） | 0.72× | 5.7 |
| 4 | 27.7（27.4 / 27.9） | 0.44× | 3.5 |
| 8 | 15.6（15.4 / 15.9） | 0.25× | 2.0 |

> 注：这一组是在较早（较安静）窗口测的；与 §2.3 的单目录 1 万项 13.1 ms 不是同一目录/同一时刻，**绝对值不可互推**，只用来看线程扩展趋势。

对比单目录异步：`NtQueryDirectoryFileEx` + 重叠 I/O + 深度 8、256 KB 缓冲，10 万项 **182.8 ms**，比同步 `FindFirstFileExW` Basic（145.6 ms）**更慢**。原因：

- 单目录的元数据量很小（10 万条 × ~100 B ≈ 10 MB），瓶颈是句柄串行查询与记录解析，不是"没打满队列"；
- `FILE_ID_BOTH_DIR_INFORMATION` 记录更大，解析/拷贝成本上升；
- 真正的并行收益来自**多个独立目录/多个句柄**同时向存储栈发请求，而不是同一句柄的重叠 I/O。

结论：**后端应做成"按目录分片的 worker 池"（4–8 线程），而不是复杂的单句柄异步引擎。** 异步重叠只在**网络盘（SMB 高延迟）**上可能有价值——此项本机未测，见 §10。

---

## 6. 图标 / 缩略图成本

### 6.1 调用链与测量口径

`SHCreateItemFromParsingName(path)` 得到 `IShellItemImageFactory`，再 `GetImage({size,size}, SIIGBF_THUMBNAILONLY | SIIGBF_RESIZETOFIT)`，取到 `HBITMAP` 后立即 `DeleteObject`（只测取图，不含解码到 XAML 图像）。`create_item_only` 只做第一步，用来分离"解析项"与"取图"成本。

### 6.2 实测（C#，400 张，单位 ms；括号内为每张）

| 场景 | 创建项（仅解析） | 串行 GetImage | 并行 8 路 |
|---|---|---|---|
| 热路径 256 px | 1978.5 / 2388.3（4.9–6.0） | 3215.9 / 3349.9（8.0–8.4） | 1247.5 / 1272.2（3.1–3.2） |
| 热路径 64 px | 2831.4 / 3076.0（7.1–7.7） | 3371.5 / 3812.3（8.4–9.5） | 1496.0 / 1703.4（3.7–4.3） |
| 冷路径 256 px（新路径） | — | **14003.3（35.0）** | — |
| 冷路径同一路径第二次 | — | 3086.7（7.7） | — |
| 冷路径 64 px（新路径） | — | **16605.2（41.5）** | — |
| 冷路径同一路径第二次 | — | 4317.8（10.8） | — |
| 单文件热调用（100 次） | — | 256 px：990.6 / 100 = **9.9**；64 px：1088.3 / 100 = **10.9** | — |

高负载窗口下的公平 A/B（400 张热 256 px）：串行 **5.17–5.88 s**（13–15 ms/张），并行 8 路 **1.5–1.9 s**（4 ms/张）。可见负载会显著抬高绝对值，但**序关系不变**。

> `create_item_only` 与请求尺寸无关（同一路径解析，不取图），因此 64 px 那列的创建成本高于 256 px 属噪声，不代表尺寸影响。

### 6.3 解读

- **冷缩略图 35–41 ms/张**；400 张 ≈ 14–17 s。任何"一打开就全部出图"的 UI 都会卡死。
- **热缩略图 8–11 ms/张**；即便如此，100 项可见列表 = 0.8–1.1 s，必须异步 + 分批。
- **`SHCreateItemFromParsingName` 本身就占 5–12 ms/项**（随负载升高），是热路径的主要成本之一，且无法通过换语言消除。
- **8 路并行约 3–4 ms/张**，比串行快 2–3 倍；再往上加线程本机未测，但 Shell 缩略图提供程序有全局并发限制，盲目加线程容易互相阻塞。
- 由此验证判断：**"枚举不是瓶颈，图标才是"成立**——差距 2–4 个数量级。

### 6.4 建议的图标管线

1. **类型图标（按扩展名）**：枚举时批量预取，键 = (扩展名, 是否目录, 尺寸, DPI)，进程内 LRU；成本被几十个扩展名摊薄。
2. **首帧**：只请求 `SIIGBF_INCACHEONLY`（Shell 已缓存）或"内存缓存命中"，命中立即上屏；未命中先用类型图标占位。
3. **后台补齐**：`SemaphoreSlim(8)` 限流，按可见顺序排队；滚动中暂停/取消非可见项（复用容器时取消）。
4. **尺寸**：只请求显示尺寸 × DPI（可用 `SIIGBF_BIGGERSIZEOK`），不要取原图再缩放；256² 解码后约 262 KB，64² 约 16 KB，**不能把全目录解码图留在内存**。
5. **缓存分层**：内存 LRU（可见 ±1 屏）+ 磁盘缓存（可选，自己写 PNG/BGRA）+ Shell 自带缩略图缓存（第三层，免费但不可控）。
6. **失败回退**：`GetImage` 失败 → 类型图标 → 通用文件字形。

---

## 7. `Windows.Storage` 实测

宿主为**未打包的 Win32 控制台进程**（与 XAML Island 未打包宿主接近）。`f1k`，1000 项：

| 操作 | C# (ms) | C++/WinRT (ms) | 每项 |
|---|---|---|---|
| `StorageFolder.GetFilesAsync()` | 3687.0 | 3771.9 / 3438.2 | ≈3.5–3.8 ms |
| 上述 + 逐项 `GetBasicPropertiesAsync()` | 9575.2 | 7675.4 / 7947.7 | ≈7.7–9.6 ms |

- 1000 项要 3.7 s，是 Win32 枚举（1000 项 ≈1.6 ms）的**约 2000 倍**。
- 逐项取基本属性再翻 2–2.6 倍。
- 早期在 1 万项上曾观察到 75–89 s（C#），但当时机器处于高负载，**该数字不可作为结论**；按 3.5 ms/项线性外推 10 万项 ≈ 350 s——**外推值，未实测**，仅用于说明不可行。
- 原因：WinRT 异步逐项往返 + `StorageFile` 对象构造 + 属性存储/ broker 激活；它不是为批量元数据设计的。
- **结论**：`StorageFolder` 只适合"少量、需要沙箱权限/库"的场景；真实目录视图的批量喂入必须用 Win32（或自带缓存的等价后端）。

---

## 8. XAML 侧的"快"

> 本节结论来自微软官方文档与 [Files 上游实现](./FILES-SOLUTION-REFERENCE.md) 的结构分析；**本机未搭 XAML Island UI 做帧率实测**（见 §10），因此属于"有依据的工程判断"，不是实测数据。

### 8.1 容器与虚拟化

| 选择 | 说明 | 适用 |
|---|---|---|
| `ListView`（`ItemsStackPanel`） | 默认虚拟化、容器回收；行式详情视图首选 | 详情/列表模式 |
| `GridView`（`ItemsWrapGrid`） | 默认虚拟化、按行列回收；图标网格首选 | 大图标/缩略图模式 |
| `ItemsRepeater` | WinUI 2/3 控件，需配虚拟化 `Layout`（`StackLayout`/`UniformGridLayout`）；**不自带选择、容器回收语义**，需自己实现 | 自定义布局、非标准项 |
| `SemanticZoom` | 分组缩放（只影响分组导航，不解决大数据虚拟化） | 按字母/日期分组 |

反模式：

- 把 `ListView`/`GridView` 放进**外层 `ScrollViewer`**，或给列表**不约束高度**（会一次性实现全部项，虚拟化失效）。
- 自定义 `ItemsPanel` 为普通 `StackPanel`/`WrapPanel`（不虚拟化）。
- 在项模板里再套 `ScrollViewer`。
- 用 `GridView` 显示 256 px 缩略图却按原图尺寸解码。

### 8.2 数据喂入

- **不要逐项 `Add`**：每次 `CollectionChanged` 都会触发一次布局/容器更新，n 项就是 n 次，整体趋近 O(n²)。10 万项时 UI 线程会长时间无响应。
- WinUI 的 `ListView`/`GridView` **不支持多条目 Add 通知**；批量修改通常只能 `Reset`（清空 + 重建）。因此正确做法是**降频批处理**：
  - 首批尽快（例如 25 ms 内或前 30–60 项）先上屏；
  - 其后每 **300–500 ms** 合并一次，`Reset` 一次；
  - 不要在每一批都全量排序（10 万项排序会把批次成本放大）。
- 实测支撑：本机 10 万项枚举 0.15 s，而 `Reset` 一次的开销主要在 UI 线程重建可见容器（约几毫秒到几十毫秒），500 ms 一批是安全的；如果 100 ms 一批，UI 线程会被持续占用。
- 参考实现：Files 用 `IntervalSampler(25)` 做首批、`IntervalSampler(500)` 做常规批，且仅在 ≤10 000 项时才在批次内排序（见配套文档）。

### 8.3 绑定

- **`x:Bind` 优于 `Binding`**：编译期生成强类型绑定代码，避免运行时反射/字符串路径解析；项模板里尤其明显。
- 在项模板热路径中避免：
  - `Binding` + `Converter`（每次实现都调用，且装箱）；
  - 绑定到"每次 get 都分配字符串"的属性（如 `Size` 格式化、`LastWriteTime` 格式化）；
  - 绑定到需要跨线程/异步的属性。
- **预计算显示字符串**：在枚举阶段就生成 `SizeText`、`DateText`、`TypeText`，UI 只读。
- **`x:Phase`**：把项模板拆成多阶段，先出名称/图标，再出次要徽标/浮层；对 `GridView` 尤其有效（Files 的网格模板用 `x:Phase="1"` 放图标、`x:Phase="2"` 放徽标）。
- **`x:Load`**：对"多数项不需要"的元素延迟创建（快捷方式角标、云同步状态、Git 状态等），避免每个容器都构造一整套视觉树。
- **`ItemContainerTransitions="{x:Null}"`**：关闭容器出现动画，滚动时不再逐项播放（Files 详情视图即如此）。

### 8.4 图标/缩略图的 XAML 策略

- `Image.Source` 只在 UI 线程赋值；`BitmapImage` 有线程亲和性，**不能在后台线程创建**（可在后台解码成 `SoftwareBitmap`，再在 UI 线程包装，但要额外拷贝）。
- 只给**可见项**取图：`ContainerContentChanging` 里判断 `args.InRecycleQueue`（回收时取消/释放），并用 `args.RegisterUpdateCallback(phase, ...)` 把取图/扩展属性推迟到后续阶段。
- 加载完成的回调里要**再次确认容器仍绑定到同一项**（滚动很快时会错位）。
- 占位策略：先用类型图标（廉价、缓存命中），再异步替换为缩略图；不要闪烁空白。
- 限流：全局 `SemaphoreSlim(8)`（Files 用 8），并在 `ScrollViewer.ViewChanged` 期间暂停"非缓存"请求，滚动停止后再补齐。

### 8.5 线程分工

| 工作 | 线程 | 原因 |
|---|---|---|
| 目录枚举、`GetFileAttributesEx`、Shell 取图 | 线程池/后台 | 阻塞 I/O，可能数百 ms（尤其 SMB 超时） |
| 业务对象构造、字符串格式化、排序 | 线程池 | 纯 CPU，避免占 UI |
| `BitmapImage`/`Image.Source` 赋值、集合修改 | UI 线程（`DispatcherQueue`，批处理、Low 优先级） | XAML 对象有线程亲和性 |
| 缩略图解码（可选） | 后台 | 解码 256² 约数 ms，避免 UI 卡顿 |

反模式：UI 线程上 `.Result`/`.Wait()`；UI 线程直接 `FindFirstFileEx`（SMB 不可达时会卡死数秒，Files 专门把它移到后台，并在注释里说明原因）；UI 线程逐项 `SHGetFileInfo`。

### 8.6 反模式清单（汇总）

1. 非虚拟化容器 / 外层 `ScrollViewer` / 无高度约束。
2. 逐项 `ObservableCollection.Add`，或在每次 `PropertyChanged` 里做重活。
3. 项模板里 `Binding` + `Converter` + 现算字符串。
4. 每个容器都创建全部可选元素（不用 `x:Load`）。
5. 逐项调 Shell（`SHGetFileInfo`、`SHGFI_ICON`）。
6. 同步等待异步 API；UI 线程做 I/O。
7. 一打开目录就请求全部缩略图（无并发限制、无取消）。
8. 用原图尺寸解码/缓存缩略图；无上限的内存缓存。
9. 用 `StorageFolder` 做大批量元数据源。
10. 每批数据都全量排序 + 立即 `Reset`（频率过高）。

---

## 9. 推荐方案组合（落地版）

### 9.1 分层选择

| 层 | 推荐 | 备选 | 明确不推荐 |
|---|---|---|---|
| 枚举 | `FindFirstFileExW(FindExInfoBasic \| FIND_FIRST_EX_LARGE_FETCH)`，每目录一个句柄，4–8 线程分片 | `NtQueryDirectoryFile`(class 1) | `NtQueryDirectoryFileEx`/`GFIBE`（记录更大）；`StorageFolder` |
| 元数据 | 枚举记录直接映射名称/大小/时间/属性 | `.NET EnumerateFileSystemInfos` | 枚举后逐项再查；逐项 Shell |
| 类型名/图标 | 扩展名映射 + 按扩展名缓存 | 注册表 `Content Type`（若有） | `SHGetFileInfo` 逐项 |
| 缩略图 | `IShellItemImageFactory::GetImage`，只取显示尺寸 × DPI，8 路限流 | `SIIGBF_INCACHEONLY` 首帧 + 异步补齐 | 同步批量、原图尺寸 |
| 容器 | `ListView`/`GridView` 默认虚拟化 | `ItemsRepeater` + 虚拟化 `Layout` | 非虚拟化面板 |
| 绑定 | `x:Bind` + `x:Phase` + `x:Load` | 预格式化字符串 | `Binding`+`Converter` 热路径 |
| 喂入 | 首批 25 ms/前 30–60 项，之后 300–500 ms 一批 `Reset` | 增量插入（需自管排序） | 逐项 `Add` |
| 语言 | C#（P/Invoke 或 CsWinRT） | C++/WinRT 助手（极端场景） | 为枚举而全面改 C++ |

### 9.2 首屏时序（建议）

```
[后台线程] CreateFile/FindFirstFileEx 打开目录
   ├─ 解析前 30~60 项 → UI（枚举本身 <1 ms；首屏延迟主要由打开目录与对象构造决定）
   ├─ 类型图标按扩展名批量预取（几十次 Shell 调用，可缓存）
   ├─ 继续枚举，每 300~500 ms 合并一批 → UI Reset
   └─ 全部完成：一次最终排序 + 一次 Reset
[UI 线程] ContainerContentChanging
   ├─ 命中内存/Shell 缓存的缩略图 → 立即可见
   ├─ 未命中 → 类型图标占位，入队（信号量 8）
   └─ 回收容器 → 取消该请求
```

### 9.3 最终推荐排序

按本机 10 万文件、热目录索引的实测（单线程；分组/排序不参与）：

| 排名 | 枚举方案 | 中位数 | 结论 |
|---|---|---|---|
| 1 | `FindFirstFileExW` Basic（±`LARGE_FETCH`） | 145.6–145.8 ms | **首选**，通用性最好 |
| 2 | `NtQueryDirectoryFile`（class 1） | 146.7 ms | 与 1 等价；仅在需要自己控缓冲时选 |
| 3 | `.NET EnumerateFileSystemInfos` | 148.3 ms | 不想 P/Invoke 时的等价替代 |
| 4 | `.NET Directory.EnumerateFiles` | 162.8 ms | 只取路径名时够用 |
| 5 | `NtQueryDirectoryFileEx` 异步（单句柄深度 8） | 182.8 ms | 本地盘不划算；仅 SMB 待验证 |
| 6 | `GetFileInformationByHandleEx`（FileIdBoth） | 205.6 ms | 只在需要 FileId 时 |
| 7 | `NtQueryDirectoryFileEx` 同步 | 215.1 ms | 不推荐 |
| 8 | `FindFirstFileExW` Standard | 230.8–239.8 ms | 避免（8.3 短名开销） |
| 9 | `StorageFolder.GetFilesAsync` | ≈3.5 s/1000 项 | 只用于小规模/库访问 |

图标/缩略图排序（成本从低到高）：**Shell 缓存命中（`SIIGBF_INCACHEONLY`）→ 自己的内存缓存 → 类型图标（按扩展名）→ `IShellItemImageFactory` 冷取图 → `SHGetFileInfo`+`SHGFI_ICON`（仅作回退）**。

UI 容器排序：**`ListView`/`GridView` 默认虚拟化 → `ItemsRepeater` + 虚拟化 `Layout` → 非虚拟化面板（不可接受）**。

### 9.4 何时值得上"Native 后端"

"Native 后端"指 C++/WinRT 或 C++ 助手负责枚举/元数据。判据（按优先级）：

1. **枚举耗时超过首屏预算且无法用分批掩盖**：例如单目录 >50 万项、要求 100 ms 内完成首批。当前 10 万项 0.15 s，按线性外推 50 万项约 0.75 s（**外推值，未实测**），C++ 也无法把 50 万项压到 100 ms，所以这条更多是"结构/硬件问题"。
2. **Profiler 证明投影/GC 是瓶颈**：UI 帧时间中 >5% 来自 P/Invoke/RCW/GC，且已经排除图标与布局。必须先有测量，不能凭感觉。
3. **需要 Win32 之外的信息**：FileId、短名、备用数据流、硬链接、USN 等；此时不是因为"快"，而是因为"托管层没暴露"。
4. **网络盘高延迟**：希望用重叠 I/O 掩盖 RTT。本机未测 SMB，属于待验证项；即使上，也应先试"多目录并发"。
5. **进程隔离**：Shell 扩展/解码器不稳定，需要独立进程（这属于稳定性收益，不是性能收益）。

反之，如果目标是"更快的目录列表"，**先做 §9.1 的组合**；实测表明语言与"更底层 API"都不是有效杠杆。

---

## 10. 不确定项与局限

1. **未做 XAML Island UI 实测**：本机未构建 XAML Island 宿主做帧率/首屏测量；§8 的结论来自官方文档 + Files 源码结构，**不是**本机 UI 数据。虚拟化的具体收益需在真实宿主上验证。
2. **绝对数受环境影响**：测试期间 CPU 86%–100%、磁盘 95%，绝对数字比安静环境高 20%–30%；文中已尽量用同窗口相对比较。组内抖动：10 万 ±10–15%，1 万 ±25%。
3. **"冷"只冷了 Shell 缩略图缓存**：使用新路径保证未命中缩略图缓存；**未清 OS 文件缓存/备用内存**，因此不能声称磁盘 I/O 冷。冷路径的 35–41 ms/张应视为"缩略图缓存冷 + 文件可能在内存缓存"。
4. **Defender 未加排除**：真实产品环境可能不同；本机数字包含实时扫描影响。
5. **未测 SMB/网络盘、ReFS、加密目录、OneDrive 占位文件**：这些场景的开销结构可能完全不同（尤其 SMB 超时与云端按需下载）。
6. **`NtQueryDirectoryFileEx` 异步只测了单句柄、深度 8、256 KB 缓冲**；未测更高队列深度、多句柄、不同缓冲大小，也未测真正高延迟介质。结论"单句柄异步不如多目录并发"仅限本机本地 SSD。
7. **未测"从父目录枚举获得 `IShellItem`（`SHCreateItemWithParent`）"**：理论上可省掉 `SHCreateItemFromParsingName` 的路径解析（热路径 5–12 ms/项），但未实现、未测量，不能作为结论。
8. **未测 `ItemsRepeater`、`x:Phase` 的实际帧收益**：仅结构分析。
9. **未测业务对象分配/格式化成本**：本文枚举只做计数与最小解析；真实列表还要构造对象、格式化字符串、绑定，10 万项下这部分可能占几十 ms 到上百 ms（未量化）。
10. **Files 上游结论基于固定 commit `3836c9c`（2026-11-06）**；上游仍在重构（如 `Files.Storage` 新抽象），后续 commit 可能改变实现。

---

## 11. 复现方法

临时工程（仓库外，未提交）：

- `C:\Users\gold\perf-research\BenchCSharp\`（.NET 10，`net10.0-windows10.0.26100.0`，x64 Release）
- `C:\Users\gold\perf-research\BenchCpp\`（C++20，`cl /std:c++20 /EHsc /O2 /MD`，链接 `WindowsApp.lib shell32 ole32 gdi32 user32 ntdll`）
- 数据：`C:\Users\gold\perf-research\data\`
- 原始结果：`out_cs_*.txt`、`out_cpp_*.txt`、`out_ab*.txt`

主要命令（在各自 `bin` 目录下）：

```powershell
# C#：10 万 / 1 万枚举（含 10 种方法，每项 5 次）
BenchCSharp.exe enum C:\Users\gold\perf-research\data\f100k 100000
BenchCSharp.exe enum C:\Users\gold\perf-research\data\f10k  10000

# C++：10 万枚举（3 种方法）
BenchCpp.exe enum C:\Users\gold\perf-research\data\f100k 100000

# 单文件属性（1 万）
BenchCSharp.exe attr C:\Users\gold\perf-research\data\f10k
BenchCpp.exe    attr C:\Users\gold\perf-research\data\f10k

# 缩略图（400 张，256 px）
BenchCSharp.exe thumbs C:\Users\gold\perf-research\data\thumbs 256
BenchCpp.exe    thumbs C:\Users\gold\perf-research\data\thumbs

# 冷路径（新目录）
BenchCSharp.exe cold C:\Users\gold\perf-research\data\thumbs_cold 256

# 多目录并发（8 万，1/2/4/8 线程）
BenchCSharp.exe conc C:\Users\gold\perf-research\data\multi 10000

# Windows.Storage（1000 项）
BenchCSharp.exe storage C:\Users\gold\perf-research\data\f1k 1000
BenchCpp.exe    storage C:\Users\gold\perf-research\data\f1k 1000
```

公平 A/B：用 `Start-Process -PassThru` 启动后立即设置 `$p.PriorityClass='AboveNormal'`（C++ 二进制未自设优先级，C# 会自设），交替运行并把输出追加到同一日志。

---

## 12. 参考

**微软官方文档（均已验证可访问）：**

- [FindFirstFileExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw)（`FindExInfoBasic`、`FIND_FIRST_EX_LARGE_FETCH` 说明）
- [FindNextFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findnextfilew)
- [GetFileAttributesExW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileattributesexw)
- [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex)
- [FILE_ID_BOTH_DIR_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info)
- [IShellItemImageFactory](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellitemimagefactory) / [GetImage](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage)（`SIIGBF_*`）
- [SHGetFileInfoW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfow)
- [StorageFolder.GetFilesAsync](https://learn.microsoft.com/en-us/uwp/api/windows.storage.storagefolder.getfilesasync)
- [Enumerate and query files and folders（UWP）](https://learn.microsoft.com/en-us/windows/uwp/files/quickstart-listing-files-and-folders)
- [Optimize ListView and GridView](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-gridview-and-listview)
- [Optimize your XAML layout](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-your-xaml-layout)
- [{x:Bind} markup extension](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-bind-markup-extension)
- [x:Phase attribute](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-phase-attribute)
- [x:Load attribute](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-load-attribute)
- [ItemsRepeater](https://learn.microsoft.com/en-us/windows/apps/design/controls/items-repeater)
- [C++/WinRT](https://learn.microsoft.com/en-us/windows/uwp/cpp-and-winrt-apis/)

**上游参考实现：**

- [files-community/Files @ 3836c9c](https://github.com/files-community/Files/tree/3836c9cd2cbab56248df63c8ad8bedfc8243bec0) —— 详见 [FILES-SOLUTION-REFERENCE.md](./FILES-SOLUTION-REFERENCE.md)。
