# Files（files-community/Files）文件视图方案分析

> 上游快照：`files-community/Files`，commit [`3836c9cd2cbab56248df63c8ad8bedfc8243bec0`](https://github.com/files-community/Files/tree/3836c9cd2cbab56248df63c8ad8bedfc8243bec0)（2026-11-06）。
> 工程形态：WinUI 3（`Microsoft.WindowsAppSDK`，`<UseWinUI>true`，见 [Files.App.csproj#L28](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Files.App.csproj#L28-L28)）。
> 主文档：[PERF-RESEARCH.md](./PERF-RESEARCH.md)；本文的实测背景与术语沿用该文档。

---

## 0. 一句话结论

Files 的列表不是靠"更快的枚举"取胜，而是靠三件事：**默认虚拟化的 ListView/GridView、按时间片批量 `Reset` 的喂入、以及"先给缓存/类型图标、再异步补缩略图"的两段式图片管线**。这三条都能直接搬到我们的 XAML Island 宿主里；**不能照搬的是它依赖 MSIX 包身份的部分**（`StorageFolder` 回退、跨进程 WinRT 图标服务器），这些需要换实现。

---

## 1. 列表控件与虚拟化（已核实）

### 1.1 详情模式

- 容器是 **`ListView`**，外面套 **`SemanticZoom`**：
  - [`DetailsLayoutPage.xaml#L219-L226`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L219-L226)（SemanticZoom）
  - [`DetailsLayoutPage.xaml#L227`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L227)（ListView）
- `ItemsSource="{x:Bind CollectionViewSource.View, Mode=OneWay}"`，即绑定的是 `CollectionViewSource.View`，不是集合本身：
  - [`#L248`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L248)
- 容器动画关闭：`ItemContainerTransitions="{x:Null}"`（[`#L247`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L247)，样式里还有一处 [`#L1346`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L1346)）。
- `ItemsPanel` 显式使用 **`ItemsStackPanel Orientation="Vertical"`**（[`#L1463-L1466`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L1463-L1466)）——这是 UWP/WinUI 默认的虚拟化面板；列表**没有**被塞进外层 `ScrollViewer`，也没有不约束高度。
- 分组用 `ListView.GroupStyle`（[`#L1418-L1421`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L1418-L1421)），缩放后的分组视图用 `CollectionViewSource.View.CollectionGroups`（[`#L1476`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L1476)）。

### 1.2 图标/磁贴模式

- 容器是 **`GridView`**，`ItemsWrapGrid` 分垂直/水平两种样式（[`GridLayoutPage.xaml#L33`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L33)、[`#L50`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L50)）。
- 绑定方式同上：`ItemsSource="{x:Bind CollectionViewSource.View, Mode=OneWay}"`，动画关闭（[`#L1009-L1010`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L1009-L1010)）。
- **`x:Load` + `x:Phase` 的组合**是本文件最值得学的部分：
  - 图像 `x:Load="{x:Bind LoadFileIcon, Mode=OneWay}"`、`x:Phase="1"`（[`#L111-L112`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L111-L112)）；
  - 快捷方式角标、提权角标同属 Phase 1（[`#L140-L141`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L140-L141)、[`#L155-L156`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L155-L156)）；
  - 云同步状态、标签等次要信息放 **Phase 2**，并且用 `x:Load` 按状态延迟创建（[`#L177-L178`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/GridLayoutPage.xaml#L177-L178)）。
- 详情模板（[`#L919-L920`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/DetailsLayoutPage.xaml#L919-L920) 起）**没有**用 `x:Phase`，只对快捷方式/提权/Git/回收站/标签等可选元素用 `x:Load`（例如 `#L951`、`#L975`、`#L984`、`#L1044`）。这是合理的：详情行模板本身很轻，分阶段收益不如网格明显。

### 1.3 为什么不用 `ItemsRepeater`（推测）

代码里没有 `ItemsRepeater` 的任何使用。合理推断：Files 需要的是**现成的选择模型、拖放、右键菜单、键盘导航、分组（SemanticZoom）和容器回收**，这些 `ListView`/`GridView` 直接提供；`ItemsRepeater` 需要自建选择与容器回收语义，改造成本远大于收益。**这是推断，不是上游声明。**

---

## 2. 数据源与增量喂入（核心）

### 2.1 两条集合

- 绑定用的是 `FilesAndFolders`（`GroupableBulkConcurrentObservableCollection<ListedItem>`），并且**只能通过 `ApplyFilesAndFoldersChangesAsync` 修改**：
  - 说明性注释在 [ShellViewModel.cs#L1119-L1129](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1119-L1129)：
    > "CollectionChanged will cause UI update, which may cause significant performance degradation… both DataGrid and GridView don't support multi-items changes notification, so here we have to call BeginBulkOperation to suppress CollectionChanged and call EndBulkOperation in the end to fire a CollectionChanged event with NotifyCollectionChangedAction.Reset"（译文：多条目变更不支持，只能抑制通知并最终发一次 `Reset`。）
- 后台枚举写入的是另一条**普通** `ConcurrentCollection<ListedItem> filesAndFolders`，排序时整条替换：
  - [`#L1226`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1226)（`filesAndFolders = new ConcurrentCollection<ListedItem>(SortingHelper.OrderFileList(...))`）。

`ApplyFilesAndFoldersChangesAsync` 的实际动作（[`#L1120-L1181`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1120-L1181)）：

1. `await bulkOperationSemaphore.WaitAsync`（全局串行化，1 个许可，[`#L755`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L755)）；
2. 在后台把 `filesAndFolders` 快照成 `List`（过滤也在这里做）；
3. 切到 UI 线程：`FilesAndFolders.BeginBulkOperation()` → `Clear()` → `AddRange(...)` → `OrderGroups()` → `EndBulkOperation()`（发出一次 `Reset`）。

### 2.2 枚举线程与打开目录

- 打开目录放在 `Task.Run`，并**加 5 秒超时**，注释明确说明原因：
  - [`#L2317-L2335`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L2317-L2335)：
    > "Off the UI thread: FindFirstFileEx blocks until the SMB timeout on an unreachable share."
- 枚举调用本身也是 `Task.Run`（[`#L2486-L2497`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L2486-L2497)），并且**一次枚举完整目录**（`countLimit: -1`），没有按页数截断。

### 2.3 喂入节奏：时间驱动，不是分页驱动

[`Win32StorageEnumerator.cs#L21-L33`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Utils/Storage/Enumerators/Win32StorageEnumerator.cs#L21-L33)：

```csharp
var sampler = new IntervalSampler(500);
// Intermediate flushes merge in sorted, so an early first paint costs no reshuffle later
var firstBatchSampler = new IntervalSampler(25);
```

- **首批 25 ms**：尽快让前若干项上屏；
- **之后每 500 ms** 一次中间刷新（[`#L107-L117`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Utils/Storage/Enumerators/Win32StorageEnumerator.cs#L107-L117)）；
- 每次中间刷新回调到 `ShellViewModel`：`filesAndFolders.AddRange` → **仅在总数 ≤ 10 000 时**做稳定排序 → `ApplyFilesAndFoldersChangesAsync`（[`#L2488-L2497`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L2488-L2497)）：
  ```csharp
  // The stable sort lands new items in final position without reordering visible ones; capped for huge listings
  if (filesAndFolders.Count <= 10_000)
      await OrderFilesAndFoldersAsync();
  ```
- 枚举结束再整体排序 + 一次 `Apply`（[`#L2499-L2502`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L2499-L2502)）。

**这是"时间片批处理"而非"分页"**：没有"第 N 页"的概念，只有"距上次刷新超过 500 ms 就刷一次"。好处是首屏快、代码简单；代价是大目录下每 500 ms 一次全量 `Reset`。

### 2.4 取消、复用、内存回收

- 每次导航有 `addFilesCTS`（用于枚举/应用变更的取消）；`ApplyFilesAndFoldersChangesAsync` 的入口就 `WaitAsync(addFilesCTS.Token)`（[`#L1130`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1130)）。
- `enumFolderSemaphore = new SemaphoreSlim(1, 1)`（[`#L753`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L753)）：同一时间只允许一个目录枚举，切换目录会取消前一个。
- 应用变更时做**整表替换**（`Clear` + `AddRange`），旧列表对象随 GC 回收；可见容器由 `ListView` 自己回收。
- 容器回收时取消扩展属性加载并释放：
  - [`BaseLayoutPage.cs#L1373-L1384`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1373-L1384)：
    ```csharp
    if (inRecycleQueue)
    {
        UpdateItemToolTip(container, null);
        if (container.Content is ListedItem recycledItem)
        {
            var shellViewModel = ParentShellPageInstance.GetRequiredShellViewModel();
            shellViewModel.CancelExtendedPropertiesLoadingForItem(recycledItem);
            shellViewModel.ReleaseExtendedProperties(recycledItem);
        }
        return;
    }
    ```

---

## 3. 图标与缩略图链路

### 3.1 枚举时就预取"通用图标"

每个文件在枚举循环里 `await iconCacheService.GetIconAsync(...)`，并把结果存到 `ListedItem.PreloadedIconData`：

- [`Win32StorageEnumerator.cs#L62-L67`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Utils/Storage/Enumerators/Win32StorageEnumerator.cs#L62-L67)
- 尺寸由当前布局决定：`GetPreloadIconSize()`（[`#L1439-L1440`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1439-L1440)）。

`IconCacheService`（客户端）**只按扩展名缓存通用图标**，键 = `扩展名/是否目录 : size : DPI`：

- [`IconCacheService.cs#L19-L27`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Services/Storage/IconCacheService.cs#L19-L27)
- 尺寸会向上取整到 `16/24/32/48/64/96/128/256`（[`#L27`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Services/Storage/IconCacheService.cs#L27)），避免每个像素尺寸都请求一次。
- 另有一层 `BitmapImage` 缓存，但注释写明 **`BitmapImage` 有线程亲和性，只允许主窗口 dispatcher 使用**（[`#L44-L48`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Services/Storage/IconCacheService.cs#L44-L48)）。

### 3.2 两段式上屏

**第一段（realization 时）**：`ApplyCachedThumbnailOrPreloadedIconAsync`

- 图片类文件且缩略图尺寸 ≥48：先请求 **`ReturnThumbnailOnly | ReturnOnlyIfCached`**（只拿 Shell 已缓存的），命中就直接上屏；
- 否则用枚举时预取的 `PreloadedIconData` 作为占位；
- 两者都通过 `DispatcherQueue.EnqueueOrInvokeAsync` 切回 UI 线程创建 `BitmapImage`。
- 代码：[`ShellViewModel.cs#L1394-L1437`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1394-L1437)。

**第二段（异步补真图）**：`LoadThumbnailAsync`

- 同样先查 `ReturnOnlyIfCached`，再决定是否走"非缓存"路径（[`#L1450-L1464`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1450-L1464)）；
- 非缓存请求放到 `Task.Run`，并受 **`loadThumbnailSemaphore = new SemaphoreSlim(8, 8)`** 限流（信号量定义 [`#L756`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L756)，用法 [`#L1533-L1550`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1533-L1550)）；
- 失败时标 `NeedsDelayedThumbnailLoad`，并注册 **2 秒定时重试**，同时保留文件变更事件驱动的重试（[`#L1554-L1595`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1554-L1595)）；
- 若枚举时预取的通用图标已经"等于最终图标"（非图片、非快捷方式、无逐文件图标），就跳过逐项取图（[`#L1466-L1482`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1466-L1482)）。

### 3.3 realization 时机与滚动抑制

- `ContainerContentChanging` → `RefreshItem`：
  - [`BaseLayoutPage.cs#L1244-L1248`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1244-L1248)
- 扩展属性（类型、大小等）推迟到 **Phase 3**，并且滚动中直接跳过，等滚动停止后的 settle pass 再补：
  - [`#L1395-L1405`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1395-L1405)（`RegisterUpdateCallback(3, ...)` + `IsScrollInFlight` 判断）
- 滚动停止判定：`ViewChanged` 后 **200 ms 去抖**，停止才恢复加载（[`#L1325-L1345`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1325-L1345)）。
- 所有加载入口 `EnqueueItemLoad` 都走 `DispatcherQueuePriority.Low`，并在回调里**再确认容器仍绑定同一项**（[`#L1410-L1425`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1410-L1425)）。

### 3.4 图标服务在独立进程里（重要）

- `Files.App.Server` 被明确标注为 **out-of-process COM server**，不作为项目引用：
  - [`Files.App.csproj#L179-L183`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Files.App.csproj#L179-L183)
  - 包清单注册 `OutOfProcessServer`（[`Package.appxmanifest#L200-L201`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Package.appxmanifest#L200-L201)）
- 因此 `Server.IconService.GetGenericIconAsync(...)` 是**跨进程调用**。这解释了为什么 `IconCacheService` 要在客户端再做一层按扩展名的内存缓存。
- 服务端缓存 5 分钟 TTL（[`IconCache.cs#L12-L24`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App.Server/Caching/IconCache.cs#L12-L24)），键 = (扩展名, 是否目录, 尺寸)。
- 真正的取图实现（`Files.Storage/Windows/Helpers/ShellImage.cs`）：
  - 首选 `IShellItemImageFactory::GetImage`，flags 组合 `SIIGBF_BIGGERSIZEOK` +（`ICONONLY` / `THUMBNAILONLY` / `INCACHEONLY`）（[`#L129-L147`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.Storage/Windows/Helpers/ShellImage.cs#L129-L147)）；
  - 失败回退 `SHGetFileInfo` + `SHGetImageList`（[`#L172-L182`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.Storage/Windows/Helpers/ShellImage.cs#L172-L182)）。

> 注意：`Files.Storage` 是并行的新抽象；在 commit `3836c9c` 上，**文件列表的枚举路径仍然是 `ShellViewModel` + `Win32StorageEnumerator`**（见 §2），不是由 `Files.Storage` 接管。看到新目录不要误以为列表已经改走它。

### 3.5 占位与失败回退

- `FileIcon` 控件在 `x:Load` 为真时才创建，尺寸由外部传入；内部按 `FileImage` 有则显示图片、无则显示字体图标/字形（[`FileIcon.xaml#L39-L52`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/UserControls/FileIcon.xaml#L39-L52)）。
- 缩略图失败 → 2 秒重试 → 仍失败则保留预取图标，不出现空白项。

---

## 4. 分组与排序

- `CollectionViewSource` 初始化即 `IsSourceGrouped = true`（[`BaseLayoutPage.cs#L150-L153`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L150-L153)）。
- 分组/非分组切换时**整体替换 `CollectionViewSource`**，而不是改源集合（[`#L1629-L1660`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/Views/Layouts/BaseLayoutPage.cs#L1629-L1660)）；若源没变则直接返回，避免无谓重建。
- `GroupableBulkConcurrentObservableCollection` 在 `Apply` 里调用 `OrderGroups()` 让组内顺序与组间顺序一致（[`ShellViewModel.cs#L1162-L1163`](https://github.com/files-community/Files/blob/3836c9cd2cbab56248df63c8ad8bedfc8243bec0/src/Files.App/ViewModels/ShellViewModel.cs#L1162-L1163)）。
- 排序全部走 `SortingHelper.OrderFileList(...)`，且如 §2.3 所述，**只有 ≤10 000 项时才在中间批次排序**。

---

## 5. 与我们的场景（UWP XAML Island + 自建 Win32 后端）的对接

### 5.1 可以直接照搬（模式层面）

| 做法 | 出处 | 为什么能直接用 |
|---|---|---|
| `ListView`/`GridView` + `ItemsStackPanel`/`ItemsWrapGrid`，不做外层滚动、不约束高度 | §1 | XAML Island 里就是同一套 XAML 控件 |
| `x:Bind` + `x:Phase` + `x:Load` | §1.2 | UWP XAML 与 WinUI 3 都支持（`x:Phase`/`x:Load` 是 XAML 编译器特性） |
| 首批 25 ms / 常规 500 ms 的时间片批量喂入 | §2.3 | 与枚举后端无关，套在我们的 worker 上即可 |
| 批量 `BeginBulkOperation → Clear/AddRange → Reset` | §2.1 | 规避逐项 `CollectionChanged`，是 ListView 的唯一批量路径 |
| `ContainerContentChanging` + Low 优先级排队 + 容器归属复检 | §3.3 | 与后端无关 |
| 回收时取消/释放扩展属性 | §2.4 | 与后端无关 |
| 缓存优先（`ReturnOnlyIfCached`）→ 类型图标占位 → 异步补图 | §3.2 | 与后端无关，关键是"别等真图" |
| 非缓存缩略图 `SemaphoreSlim(8)` 限流 | §3.2 | 8 是上游经验值，可作起点 |
| 按 (扩展名, 目录, 尺寸, DPI) 缓存通用图标 | §3.1 | 与后端无关 |
| 滚动 200 ms 去抖 + 滚动期间跳过扩展属性 | §3.3 | 与后端无关 |
| 缩略图失败 2 秒重试 + 文件变更事件驱动重试 | §3.2 | 与后端无关 |
| 排序在超大列表上设上限（Files 取 1 万） | §2.3/§4 | 避免每批 O(n log n) |

### 5.2 必须替换 / 不能照搬

| 上游做法 | 问题 | 我们的替代 |
|---|---|---|
| `StorageFolder` 回退（Box/FTP 走 `GetFilesAsync`） | 需要 `Windows.Storage` 与包身份/库权限；实测 3.5–9.6 ms/项 | 自己的非文件系统后端；云盘另做 provider |
| `Files.App.Server` 跨进程 WinRT 图标服务 | 依赖 MSIX `OutOfProcessServer` 注册与包身份；未打包 XAML Island 宿主激活不了 | 进程内实现；若需隔离 shell 扩展，用自建 helper 进程 + 命名管道/COM，别用 WinRT out-of-proc |
| `App.AppModel.AppWindowDPI`、`MainWindow.Instance`、`Ioc.Default`、`UserSettingsService` | Files 应用模型耦合 | 换成我们的 DI/设置/DPI 来源 |
| `ShellViewModel` 整体 | 与标签页、云盘、搜索、watcher 深度耦合 | 只搬模式，不搬类 |
| `LayoutSizeKindHelper.GetIconSize(layoutMode)` | 依赖 Files 的布局枚举 | 映射到我们的视图尺寸 |
| 每次 `Apply` 全量 `Reset`（大目录每 500 ms 一次） | 上游可接受，但 10 万项时每次都会重建可见容器；若我们的列表更大/更重，需要放宽间隔 | 按规模自适应：>5 万项时把批次间隔放到 1 s，或在首屏之后改为追加式增量 |
| 中间批次不排序（>1 万时） | 大批量下用户会看到"先目录序、最后一次性跳成排序序" | 若排序是硬需求，改为后台持续归并 + 低频 `Reset`，或接受该 UX |
| `Files.Storage` 新抽象 | 该 commit 上未接管列表路径，不能当作"现成后端" | 以 `Win32StorageEnumerator` 的调用模式为准 |

### 5.3 建议的落地顺序

1. **先照搬 §5.1 的 UI/喂入/图片三段式**（这部分与后端解耦，收益最大）。
2. 枚举后端沿用我们已有的 Win32 实现；**只需补两件事**：
   - 打开目录的调用放到后台并加超时（SMB 不可达时别卡 UI）；
   - 提供 `intermediateAction` 式的时间片回调（首批快、后续 500 ms）。
3. 图标不要走跨进程 WinRT；进程内 `IShellItemImageFactory` + 8 路信号量 + 按扩展名缓存。
4. 最后再评估是否需要把枚举换成 `NtQueryDirectoryFileEx` 或 C++——依据 [PERF-RESEARCH.md §9.4](./PERF-RESEARCH.md) 的判据，本机数据不支持这样做。

---

## 6. 事实 vs 推测

**已核实（有代码位置）**

- 列表是 `ListView` + `SemanticZoom` / `GridView`，`ItemsStackPanel` / `ItemsWrapGrid`，`ItemContainerTransitions="{x:Null}"`。
- 详情模板不用 `x:Phase`；网格模板用 `x:Phase` 1/2。
- 枚举用 `FindFirstFileEx` + `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`（`ShellViewModel.cs#L2325-L2331`）。
- 枚举在 `Task.Run`，打开目录有 5 s 超时；一次枚举整个目录（`countLimit: -1`）。
- 喂入是 25 ms 首批 + 500 ms 常规批；>1 万项时中间批不排序。
- 应用变更用 `BeginBulkOperation`/`EndBulkOperation` 发一次 `Reset`。
- 缩略图有 `SemaphoreSlim(8)`、缓存优先、2 s 失败重试、滚动 200 ms 去抖。
- 图标服务是 out-of-process COM server（csproj 注释 + manifest）。

**推测（无上游声明）**

- 25 ms / 500 ms 这两个数是"首屏快 + UI 不卡"的经验折中；代码没有说明推导过程。
- 枚举时逐项 `await GetIconAsync` 是为了"首屏直接有图标"，代价是枚举变慢；代码注释只解释布局尺寸一致，没有说明性能取舍。
- `SemaphoreSlim(8)` 是经验值，上游没有压测说明。
- 不用 `ItemsRepeater` 是为了复用选择/拖放/分组能力（见 §1.3）。
- 跨进程图标服务的主要目的是隔离 shell 扩展/解码器崩溃，而不是提速；代码只说明"是 out-of-process"，没有说明动机。

---

## 7. 参考

- 仓库（固定 commit）：[files-community/Files @ 3836c9c](https://github.com/files-community/Files/tree/3836c9cd2cbab56248df63c8ad8bedfc8243bec0)
- 主调研与实测数据：[PERF-RESEARCH.md](./PERF-RESEARCH.md)
- 相关微软文档：[Optimize ListView and GridView](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/optimize-gridview-and-listview)、[x:Phase](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-phase-attribute)、[x:Load](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-load-attribute)、[{x:Bind}](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-bind-markup-extension)
