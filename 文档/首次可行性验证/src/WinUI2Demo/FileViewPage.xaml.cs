using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace WinUI2Demo
{
    /// <summary>
    /// 批量通知集合：后台枚举结果在 UI 线程一次性 AddRangeSilent + RaiseReset。
    /// 规避逐项 CollectionChanged（每次都会触发布局/容器更新，n 项接近 O(n²)）；
    /// WinUI 的 ListView 不支持多条目 Add 通知，Reset 是唯一批量路径（与 Files 做法一致）。
    /// </summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        internal void AddRangeSilent(IEnumerable<T> items)
        {
            foreach (var item in items)
                Items.Add(item);
        }

        internal void ClearSilent()
        {
            Items.Clear();
        }

        /// <summary>用源列表的一段重建当前可见页（一次 Reset，不逐项通知）。</summary>
        internal void SetPage(IList<T> source, int start, int count)
        {
            Items.Clear();
            var end = Math.Min(source.Count, start + count);
            for (var i = start; i < end; i++)
                Items.Add(source[i]);
        }

        internal void RaiseReset()
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// 文件视图页：Win32 后台枚举 + 时间片批量喂入 + GridView/ItemsWrapGrid 虚拟化 + 两段式图标。
    /// 顶栏的透明/退出仍由 DemoShell 控制，本页不改变宿主样式。
    /// </summary>
    public sealed partial class FileViewPage : Page
    {
        private static readonly SemaphoreSlim IconSemaphore = new(8, 8);
        private const int IconSizePx = 32;
        /// <summary>图标缓存上限：只保留最近使用且靠近视口的图标，超出后淘汰离屏项（回落为类型字形）。</summary>
        private const int MaxCachedIcons = 160;

        /// <summary>完整枚举结果（分页的数据源）。</summary>
        public BulkObservableCollection<FileEntry> Items { get; } = new();
        /// <summary>当前页绑定到 GridView 的可见子集；滚动已禁用，只显示一屏。</summary>
        public BulkObservableCollection<FileEntry> VisibleItems { get; } = new();

        private double _itemSlotWidth = 180;
        private double _itemSlotHeight = 104;
        private int _pageSize = 12;
        private int _pageIndex;

        private CoreDispatcher? _dispatcher;
        private CancellationTokenSource? _cts;
        private DispatcherTimer? _heartbeatTimer;
        private DispatcherTimer? _scrollSummaryTimer;
        private DispatcherTimer? _iconSettleTimer;
        // 滚动/布局停止后再补图标：避免 UIA 跳转/快速滚动时对中间位置的几百个容器逐页解码
        private DispatcherTimer? _iconQueueTimer;
        private ScrollViewer? _scrollViewer;
        private bool _active;
        private bool _feedDone;
        private bool _firstRealizedLogged;
        private string _currentPath = "";
        private int _generation;

        // 条目自绘状态：同一时刻最多一个悬停/按下项，选中项由 SelectionChanged 维护
        private FileEntry? _hoveredEntry;
        private FileEntry? _pressedEntry;
        private FileEntry? _selectedEntry;
        private Brush? _idleBrush;
        private Brush? _hoverBrush;
        private Brush? _pressedBrush;
        private Brush? _selectedBrush;
        private Brush? _selectedBorderBrush;
        private double _selectedBorderThickness = 2;
        private static readonly long ViewLogIntervalTicks = 500 * Stopwatch.Frequency / 1000;

        private long _pageStartTicks;
        private long _firstPaintMs;
        private long _enumDoneMs;
        private long _feedDoneMs;
        private long _uiApplyMaxMs;
        private int _batchCount;
        private int _maxBatchItems;
        private int _realized;
        private int _enumScanned;
        private int _iconRequested;
        private int _iconLoaded;
        private int _iconFailed;
        private int _iconEvicted;
        // 图标 LRU：只有已成功加载的项进链表；淘汰时只动离屏项（在屏项移到表尾跳过）。
        private readonly LinkedList<FileEntry> _iconLru = new();
        private readonly Dictionary<FileEntry, LinkedListNode<FileEntry>> _iconLruNodes = new();
        private readonly HashSet<FileEntry> _realizedEntries = new();

        // R8：遮挡降级状态
        private bool _occluded;
        private long _occHiddenTicks;
        private DispatcherTimer? _restoreTimer;
        private long _restoreStartTicks;
        private bool _memoryHook;
        private long _hbLastTicks;
        private long _hbMaxGapMs;
        private int _hbSamples;
        private long _lastViewLogTicks;
        private int _attachAttempts;
        // 每次 Reset 之后第一次布局的耗时（换 GridView 后的关键回归指标）
        private bool _layoutPending;
        private long _layoutPendingTicks;
        private int _layoutPendingBatch;
        private int _layoutPendingItems;
        private long _layoutMaxMs;

        public FileViewPage()
        {
            InitializeComponent();
            _itemSlotWidth = ResDouble("ItemSlotWidth", 180);
            _itemSlotHeight = ResDouble("ItemSlotHeight", 104);
            _idleBrush = ResBrush("ItemIdleBrush");
            _hoverBrush = ResBrush("ItemHoverBrush");
            _pressedBrush = ResBrush("ItemPressedBrush");
            _selectedBrush = ResBrush("ItemSelectedBrush");
            _selectedBorderBrush = ResBrush("ItemSelectedBorderBrush");
            _selectedBorderThickness = ResDouble("ItemSelectedBorderThickness", 2);
            FileList.Loaded += (_, __) => AttachScrollViewer();
            // NativeAOT 下实测 Loaded 不一定会触发，布局变化时也尝试挂载
            FileList.LayoutUpdated += OnListLayoutUpdated;
            // 滚动已禁用：滚轮改为翻页（GridView 内部 ScrollViewer 可能标记 handled，这里强制接收）
            RootGrid.AddHandler(UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(RootGrid_PointerWheelChanged), true);
            // 右键菜单：GridViewItem 可能吃掉 RightTapped，这里在根层兜底并手动弹出
            RootGrid.AddHandler(UIElement.RightTappedEvent,
                new RightTappedEventHandler(RootGrid_RightTapped), true);
            // 启动时序证据：页面进入可视树并完成第一次布局的时间点
            Loaded += (_, __) => Program.Log(
                $"[STARTUP] page Loaded name={GetType().Name} t={Program.StartupMs()}ms");
        }

        private static double ResDouble(string key, double fallback)
        {
            var value = Application.Current.Resources[key];
            return value is double d ? d : fallback;
        }

        private static Brush? ResBrush(string key) => Application.Current.Resources[key] as Brush;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _dispatcher = Dispatcher;
            var path = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(path))
                path = Program.FileViewDefaultPath;
            if (string.IsNullOrWhiteSpace(path))
                path = DefaultDirectory();

            Program.Log($"[FV] page start path='{path}'");
            Program.OcclusionChanged += OnOcclusionFromAnyThread;
            HookMemoryManager();
            StartLoad(path);

            // 打开页面时宿主可能已经被盖住：事件只在变化时触发，需主动补一次当前状态
            if (Program.IsHostOccluded)
                ApplyOcclusion(true, "initial");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Program.Log($"[FV] navigated from mode={e.NavigationMode}");
            Program.OcclusionChanged -= OnOcclusionFromAnyThread;
            if (_memoryHook)
            {
                try { MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased; }
                catch { /* 降级路径忽略 */ }
                _memoryHook = false;
            }
            _restoreTimer?.Stop();
            _restoreTimer = null;
            // 导航离开时页面实例会被释放，但先把引用断掉，避免事件/定时器延长生命周期
            _iconLru.Clear();
            _iconLruNodes.Clear();
            _realizedEntries.Clear();
            VisibleItems.ClearSilent();
            VisibleItems.RaiseReset();
            FindFileEnumerator.MaxBatchItems = FindFileEnumerator.NormalBatchItems;
            StopLoad();
            base.OnNavigatedFrom(e);
        }

        private static string DefaultDirectory()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(profile, "Downloads");
            if (Directory.Exists(downloads))
                return downloads;
            return profile;
        }

        /// <summary>右键菜单：返回上一级（到根目录时禁用）。</summary>
        private void Up_Click(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(_currentPath);
            if (parent is null)
                return;
            Program.Log($"[FV] up from '{_currentPath}' => '{parent.FullName}'");
            StartLoad(parent.FullName);
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Program.Log("[FV] quit requested");
            DemoBridge.Close();
        }

        /// <summary>单击：GridView 自身完成选中（SelectionMode=Single），这里只记录命中，便于回归核对。</summary>
        private void FileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FileEntry entry)
                Program.Log($"[FV] item click name='{entry.Name}' isDir={entry.IsDirectory}");
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var previous = _selectedEntry;
            _selectedEntry = FileList.SelectedItem as FileEntry;
            UpdateEntrySurface(previous);
            UpdateEntrySurface(_selectedEntry);
            Program.Log($"[FV] selection changed -> '{_selectedEntry?.Name ?? "<none>"}' count={FileList.SelectedItems.Count}");
        }

        private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var entry = (sender as FrameworkElement)?.DataContext as FileEntry;
            _hoveredEntry = entry;
            ApplySurfaceVisual(sender as Border, entry);
            if (Program.InputLogEnabled && entry is not null)
                Program.Log($"[FV] hover enter name='{entry.Name}'");
        }

        private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            var entry = (sender as FrameworkElement)?.DataContext as FileEntry;
            if (_hoveredEntry == entry)
                _hoveredEntry = null;
            ApplySurfaceVisual(sender as Border, entry);
        }

        private void Item_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var entry = (sender as FrameworkElement)?.DataContext as FileEntry;
            _pressedEntry = entry;
            ApplySurfaceVisual(sender as Border, entry);
        }

        private void Item_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var entry = (sender as FrameworkElement)?.DataContext as FileEntry;
            if (_pressedEntry == entry)
                _pressedEntry = null;
            ApplySurfaceVisual(sender as Border, entry);
        }

        private void ApplySurfaceVisual(Border? surface, FileEntry? entry)
        {
            if (surface is null)
                return;

            var selected = entry is not null && entry == _selectedEntry;
            surface.Background = selected ? _selectedBrush
                : entry == _pressedEntry ? _pressedBrush
                : entry == _hoveredEntry ? _hoverBrush
                : _idleBrush;

            // 选中态 = 淡填充 + 2px 强调色描边（Files GridLayoutPage.xaml:932-934）
            surface.BorderBrush = selected ? _selectedBorderBrush : null;
            surface.BorderThickness = new Thickness(selected ? _selectedBorderThickness : 0);
        }

        private void UpdateEntrySurface(FileEntry? entry)
        {
            if (entry is null)
                return;
            if (FileList.ContainerFromItem(entry) is GridViewItem container)
                ApplySurfaceVisual(FindItemSurface(container), entry);
        }

        private static Border? FindItemSurface(DependencyObject? root)
        {
            if (root is null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border)
                    return border;
                var found = FindItemSurface(child);
                if (found is not null)
                    return found;
            }
            return null;
        }

        // ---------------- 分页：可用区域定列数/行数，滚动禁用 ----------------

        private int PageCount => Math.Max(1, (Items.Count + _pageSize - 1) / _pageSize);

        private void FileList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageMetrics();

        /// <summary>列数 = floor(可用宽度 / 槽宽)，行数 = floor(可用高度 / 槽高)，一页即一屏。</summary>
        private void UpdatePageMetrics()
        {
            var width = FileList.ActualWidth;
            var height = FileList.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var columns = Math.Max(1, (int)Math.Floor(width / _itemSlotWidth));
            var rows = Math.Max(1, (int)Math.Floor(height / _itemSlotHeight));
            var size = columns * rows;
            if (size == _pageSize)
                return;

            _pageSize = size;
            _pageIndex = 0;
            Program.Log($"[FV] page metrics w={width:F0} h={height:F0} " +
                $"cols={columns} rows={rows} pageSize={size}");
            RefreshVisiblePage();
        }

        private void RefreshVisiblePage()
        {
            var pages = PageCount;
            if (_pageIndex >= pages)
                _pageIndex = pages - 1;
            if (_pageIndex < 0)
                _pageIndex = 0;

            var start = _pageIndex * _pageSize;
            var count = Math.Min(_pageSize, Math.Max(0, Items.Count - start));
            VisibleItems.SetPage(Items, start, count);
            VisibleItems.RaiseReset();
            UpdateMenuState();
        }

        private void UpdateMenuState()
        {
            try { UpMenuItem.IsEnabled = Directory.GetParent(_currentPath) is not null; }
            catch { UpMenuItem.IsEnabled = false; }
            PrevPageMenuItem.IsEnabled = _pageIndex > 0;
            NextPageMenuItem.IsEnabled = _pageIndex < PageCount - 1;
        }

        private void GoToPage(int delta)
        {
            var next = _pageIndex + delta;
            var pages = PageCount;
            if (next < 0 || next >= pages)
                return;

            _pageIndex = next;
            Program.Log($"[FV] page {_pageIndex + 1}/{pages} size={_pageSize} start={_pageIndex * _pageSize}");
            _hoveredEntry = null;
            _pressedEntry = null;
            FileList.SelectedItem = null;
            RefreshVisiblePage();
            ScheduleVisibleIconQueue();
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(-1);

        private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(1);

        /// <summary>滚轮翻页：桌面形态下不滚动长列表，一格滚轮切换一屏。</summary>
        private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
            if (delta == 0)
                return;
            GoToPage(delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (RootGrid.Resources["DesktopMenu"] is not MenuFlyout menu)
                return;
            var point = e.GetPosition(RootGrid);
            menu.ShowAt(RootGrid, new Point(point.X, point.Y));
            e.Handled = true;
        }

        /// <summary>双击目录进入；文件双击不启动外部程序（避免桌面层误触发）。</summary>
        private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var entry = (e.OriginalSource as FrameworkElement)?.DataContext as FileEntry;
            Program.Log($"[FV] double tapped src={e.OriginalSource?.GetType().Name} " +
                $"entry='{entry?.Name ?? "<none>"}'");
            if (entry?.IsDirectory != true)
                return;

            Program.Log($"[FV] double click enter dir='{entry.FullPath}'");
            StartLoad(entry.FullPath);
            e.Handled = true;
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath))
                return;
            Program.Log($"[FV] reload path='{_currentPath}'");
            StartLoad(_currentPath);
        }

        private void StartLoad(string path)
        {
            StopLoad();

            _currentPath = path;
            _pageIndex = 0;
            _hoveredEntry = null;
            _pressedEntry = null;
            _selectedEntry = null;

            Items.ClearSilent();
            Items.RaiseReset();
            VisibleItems.ClearSilent();
            VisibleItems.RaiseReset();
            UpdateMenuState();

            _active = true;
            _feedDone = false;
            _firstRealizedLogged = false;
            _generation++;
            var generation = _generation;
            _cts = new CancellationTokenSource();
            _pageStartTicks = Stopwatch.GetTimestamp();
            _firstPaintMs = 0;
            _enumDoneMs = 0;
            _feedDoneMs = 0;
            _uiApplyMaxMs = 0;
            _batchCount = 0;
            _maxBatchItems = 0;
            _realized = 0;
            _enumScanned = 0;
            _iconRequested = 0;
            _iconLoaded = 0;
            _iconFailed = 0;
            _iconEvicted = 0;
            // 旧条目已从列表移除：LRU/在屏集合必须一起清空，否则旧项会被当作“缓存”持有
            _iconLru.Clear();
            _iconLruNodes.Clear();
            _realizedEntries.Clear();
            _lastViewLogTicks = 0;
            _hbMaxGapMs = 0;
            _hbSamples = 0;
            _hbLastTicks = Stopwatch.GetTimestamp();

            MemProbe.ResetBaseline();
            StartTimers();

            if (!Directory.Exists(path))
            {
                Program.Log($"[FV] path missing '{path}'");
                _feedDone = true;
                return;
            }

            _ = FindFileEnumerator.EnumerateAsync(
                path,
                batch => OnBatchFromBackground(batch, generation),
                (total, enumMs, error) => OnDoneFromBackground(total, enumMs, error, generation),
                _cts.Token);
        }

        private void StopLoad()
        {
            _active = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _heartbeatTimer?.Stop();
            _heartbeatTimer = null;
            _scrollSummaryTimer?.Stop();
            _scrollSummaryTimer = null;
            _iconSettleTimer?.Stop();
            _iconSettleTimer = null;
            _iconQueueTimer?.Stop();
            _iconQueueTimer = null;
            _restoreTimer?.Stop();
            _restoreTimer = null;
        }

        private void StartTimers()
        {
            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _heartbeatTimer.Tick += (_, __) => OnHeartbeat();
            _heartbeatTimer.Start();

            _iconQueueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _iconQueueTimer.Tick += (_, __) =>
            {
                _iconQueueTimer?.Stop();
                QueueVisibleIcons();
            };
            _iconQueueTimer.Start();
        }

        private bool IconQueuePending => _iconQueueTimer?.IsEnabled == true;

        /// <summary>重新计时：连续滚动时不断延后，直到停下 120-150 ms 才按最终可视区补图标。</summary>
        private void ScheduleVisibleIconQueue(int delayMs = 150)
        {
            if (_iconQueueTimer is null)
                return;
            _iconQueueTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _iconQueueTimer.Stop();
            _iconQueueTimer.Start();
        }

        /// <summary>UI 线程心跳：两次 Tick 的间隔最大值就是喂入期间 UI 被占用的最长时段。</summary>
        private void OnHeartbeat()
        {
            var now = Stopwatch.GetTimestamp();
            // 启动闸门（隐藏创建→首帧→动画开始）不计入喂入心跳，避免和 FV 的真实 UI 停顿混淆；
            // 首帧前的布局另由 [FV] layoutAfterBatch 单独记录。
            if (Program.StartupRevealInProgress)
            {
                _hbLastTicks = now;
                return;
            }
            if (_hbLastTicks != 0)
            {
                var gap = (long)((now - _hbLastTicks) * 1000.0 / Stopwatch.Frequency);
                _hbSamples++;
                if (gap > _hbMaxGapMs)
                    _hbMaxGapMs = gap;
            }

            _hbLastTicks = now;
        }

        private void OnBatchFromBackground(FileBatch batch, int generation)
        {
            Volatile.Write(ref _enumScanned, batch.TotalSoFar);
            var queuedTicks = Stopwatch.GetTimestamp();
            var dispatcher = _dispatcher;
            if (dispatcher is null)
                return;

            _ = dispatcher.TryRunAsync(CoreDispatcherPriority.Low, () => ApplyBatch(batch, queuedTicks, generation));
        }

        private void ApplyBatch(FileBatch batch, long queuedTicks, int generation)
        {
            if (!_active || generation != _generation)
                return;

            var uiDelayMs = (long)((Stopwatch.GetTimestamp() - queuedTicks) * 1000.0 / Stopwatch.Frequency);
            var applyStart = Stopwatch.GetTimestamp();

            Items.AddRangeSilent(batch.Items);
            Items.RaiseReset();
            RefreshVisiblePage();
            _layoutPending = true;
            _layoutPendingTicks = Stopwatch.GetTimestamp();
            _layoutPendingBatch = batch.BatchIndex;
            _layoutPendingItems = Items.Count;
            _batchCount = batch.BatchIndex;
            if (batch.Items.Count > _maxBatchItems)
                _maxBatchItems = batch.Items.Count;

            var applyMs = (long)((Stopwatch.GetTimestamp() - applyStart) * 1000.0 / Stopwatch.Frequency);
            if (applyMs > _uiApplyMaxMs)
                _uiApplyMaxMs = applyMs;

            if (batch.IsFirst)
            {
                _firstPaintMs = PageElapsedMs();
                Program.Log($"[FV] firstBatch count={batch.Items.Count} atMs={_firstPaintMs} " +
                    $"uiDelayMs={uiDelayMs} applyMs={applyMs} enumMs={batch.EnumElapsedMs}");
                MemProbe.Log("firstBatch", Items.Count, EstimateModelBytes(),
                    Volatile.Read(ref _iconLoaded), _maxBatchItems);
            }

            Program.Log($"[FV] batch n={batch.BatchIndex} count={batch.Items.Count} shown={Items.Count} " +
                $"scanned={batch.TotalSoFar} enumMs={batch.EnumElapsedMs} uiDelayMs={uiDelayMs} applyMs={applyMs}");
        }

        private void OnDoneFromBackground(int total, long enumMs, string? error, int generation)
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null)
                return;

            Volatile.Write(ref _enumScanned, total);
            _ = dispatcher.TryRunAsync(CoreDispatcherPriority.Low, () => OnEnumDone(total, enumMs, error, generation));
        }

        private void OnEnumDone(int total, long enumMs, string? error, int generation)
        {
            if (!_active || generation != _generation)
                return;

            _feedDone = true;
            _enumDoneMs = enumMs;
            _feedDoneMs = PageElapsedMs();

            Program.Log($"[FV] enum done count={total} enumMs={enumMs} error={error ?? "none"} " +
                $"scanned={Volatile.Read(ref _enumScanned)}");
            Program.Log($"[FV] feed done shown={Items.Count} batches={_batchCount} firstPaintMs={_firstPaintMs} " +
                $"feedDoneMs={_feedDoneMs} uiApplyMaxMs={_uiApplyMaxMs} " +
                $"icons requested={_iconRequested} loaded={Volatile.Read(ref _iconLoaded)} " +
                $"failed={Volatile.Read(ref _iconFailed)}");
            Program.Log($"[FV] heartbeat feed maxGapMs={_hbMaxGapMs} samples={_hbSamples}");
            LogModelDetail("feedDone");
            MemProbe.Log("feedDone", Items.Count, EstimateModelBytes(),
                _iconLru.Count, _maxBatchItems);

            ResetHeartbeatWindow();

            // 图标管线是异步的，枚举完成后再等 2 秒记录一次最终计数（失败回退也要可见）
            _iconSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _iconSettleTimer.Tick += (_, __) =>
            {
                Program.Log($"[FV] icons settle requested={_iconRequested} " +
                    $"loaded={Volatile.Read(ref _iconLoaded)} failed={Volatile.Read(ref _iconFailed)} " +
                    $"evicted={_iconEvicted} cache={_iconLru.Count} " +
                    $"realized={_realized}");
                MemProbe.Log("iconsSettle", Items.Count, EstimateModelBytes(),
                    _iconLru.Count, _maxBatchItems);
                _iconSettleTimer?.Stop();
                _iconSettleTimer = null;
            };
            _iconSettleTimer.Start();

            var iconOk = Volatile.Read(ref _iconLoaded);
            var iconFail = Volatile.Read(ref _iconFailed);
            Program.Log($"[FV] done items={Items.Count} enumMs={enumMs} firstBatchMs={_firstPaintMs} " +
                $"feedDoneMs={_feedDoneMs} batches={_batchCount} icons={iconOk}/{_iconRequested} fail={iconFail}" +
                (error is null ? "" : $" error={error}"));
        }

        /// <summary>
        /// 条目模型内存估算（去重感知）：采样 2000 项，按“每项独立字符串 + 去重后共享字符串”两种口径分别折算。
        /// FileEntry ≈ 104 B、集合槽 8 B；每项独立字符串（Name）20 B 头；去重字段（Size/Date/Type/Glyph）
        /// 按样本去重数线性外推后只留共享实例；父目录全列表一份。
        /// </summary>
        private long EstimateModelBytes()
        {
            var count = Items.Count;
            if (count == 0)
                return 0;

            var sample = Math.Min(2000, count);
            long nameChars = 0;
            var sizeSet = new HashSet<string>(StringComparer.Ordinal);
            var dateSet = new HashSet<string>(StringComparer.Ordinal);
            var typeSet = new HashSet<string>(StringComparer.Ordinal);
            var glyphSet = new HashSet<string>(StringComparer.Ordinal);
            long sizeDistinctChars = 0, dateDistinctChars = 0, typeDistinctChars = 0, glyphDistinctChars = 0;

            for (var i = 0; i < sample; i++)
            {
                var e = Items[i];
                nameChars += e.Name.Length;
                if (sizeSet.Add(e.SizeText)) sizeDistinctChars += e.SizeText.Length;
                if (dateSet.Add(e.DateText)) dateDistinctChars += e.DateText.Length;
                if (typeSet.Add(e.TypeText)) typeDistinctChars += e.TypeText.Length;
                if (glyphSet.Add(e.Glyph)) glyphDistinctChars += e.Glyph.Length;
            }

            var scale = (double)count / sample;
            var nameTotalChars = (long)(nameChars * scale);

            // 去重数外推：全同→1，全不同→count；对应字符数按“不同值的平均长度 × 外推个数”折算。
            long DistinctEst(int distinct, long distinctChars)
            {
                if (distinct == 0)
                    return 0;
                var n = (long)Math.Min(count, distinct * scale);
                return n * distinctChars / distinct;
            }

            var sharedChars = DistinctEst(sizeSet.Count, sizeDistinctChars) +
                              DistinctEst(dateSet.Count, dateDistinctChars) +
                              DistinctEst(typeSet.Count, typeDistinctChars) +
                              DistinctEst(glyphSet.Count, glyphDistinctChars);
            var sharedCount = Math.Min(count, sizeSet.Count * scale) + Math.Min(count, dateSet.Count * scale) +
                              Math.Min(count, typeSet.Count * scale) + Math.Min(count, glyphSet.Count * scale);
            var dirBytes = Items[0].DirPath.Length * 2.0 + 20.0;

            var objBytes = count * 104.0;
            var headBytes = (count + sharedCount) * 20.0 + 20.0;
            var contentBytes = (nameTotalChars + sharedChars) * 2.0;
            return (long)(objBytes + headBytes + contentBytes + dirBytes);
        }

        /// <summary>
        /// 条目模型内存分解（台账测量用，只在 --memgc 1 下执行，避免常规运行多付一次全量扫描）：
        /// 对象+集合槽 = 112 B/项（含共享父目录引用）；每个字符串实例头 20 B；内容 UTF-16 2 B/字符。
        /// distinct* 给出 SizeText/DateText/TypeText/Glyph 的重复度，dedupTotal 是“这 4 个字段按值共享”
        /// 之后的模型估算（Name 仍按每项独立，父目录全列表一份）。
        /// </summary>
        private void LogModelDetail(string tag)
        {
            if (!MemProbe.ForceGc)
                return;

            var count = Items.Count;
            if (count == 0)
                return;

            var dirChars = Items[0].DirPath.Length;
            long nameChars = 0, sizeChars = 0, dateChars = 0, typeChars = 0, glyphChars = 0;
            var sizeSet = new HashSet<string>(StringComparer.Ordinal);
            var dateSet = new HashSet<string>(StringComparer.Ordinal);
            var typeSet = new HashSet<string>(StringComparer.Ordinal);
            var glyphSet = new HashSet<string>(StringComparer.Ordinal);
            long sizeDistinctChars = 0, dateDistinctChars = 0, typeDistinctChars = 0, glyphDistinctChars = 0;

            for (var i = 0; i < count; i++)
            {
                var e = Items[i];
                nameChars += e.Name.Length;
                sizeChars += e.SizeText.Length;
                dateChars += e.DateText.Length;
                typeChars += e.TypeText.Length;
                glyphChars += e.Glyph.Length;

                if (sizeSet.Add(e.SizeText)) sizeDistinctChars += e.SizeText.Length;
                if (dateSet.Add(e.DateText)) dateDistinctChars += e.DateText.Length;
                if (typeSet.Add(e.TypeText)) typeDistinctChars += e.TypeText.Length;
                if (glyphSet.Add(e.Glyph)) glyphDistinctChars += e.Glyph.Length;
            }

            var objBytes = count * 104L;
            var headBytes = count * 5L * 20L + 20L;
            var contentChars = nameChars + dirChars + sizeChars + dateChars + typeChars + glyphChars;
            var contentBytes = contentChars * 2L;
            var dedupHeadBytes = (count + sizeSet.Count + dateSet.Count + typeSet.Count + glyphSet.Count) * 20L + 20L;
            var dedupContentBytes = (nameChars + dirChars +
                sizeDistinctChars + dateDistinctChars + typeDistinctChars + glyphDistinctChars) * 2L;

            Program.Log($"[MODEL] tag={tag} items={count} " +
                $"obj+slot={objBytes / 1048576.0:F2}MB strHead={headBytes / 1048576.0:F2}MB " +
                $"content={contentBytes / 1048576.0:F2}MB total={(objBytes + headBytes + contentBytes) / 1048576.0:F2}MB " +
                $"chars name={nameChars} dir={dirChars}(shared) size={sizeChars} date={dateChars} " +
                $"type={typeChars} glyph={glyphChars} " +
                $"distinct size={sizeSet.Count} date={dateSet.Count} type={typeSet.Count} glyph={glyphSet.Count} " +
                $"dedupTotal={(objBytes + dedupHeadBytes + dedupContentBytes) / 1048576.0:F2}MB");
        }

        private long PageElapsedMs()
        {
            return (long)((Stopwatch.GetTimestamp() - _pageStartTicks) * 1000.0 / Stopwatch.Frequency);
        }

        private void ResetHeartbeatWindow()
        {
            _hbMaxGapMs = 0;
            _hbSamples = 0;
            _hbLastTicks = Stopwatch.GetTimestamp();
        }

        // ---------------- 图标：类型字形（模板里立即可见）→ 限流异步补真图标 ----------------

        private void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.Item is FileEntry recycled)
                    _realizedEntries.Remove(recycled);
                return;
            }

            if (args.Phase == 0)
            {
                _realized++;
                if (!_firstRealizedLogged)
                {
                    _firstRealizedLogged = true;
                    var name = args.Item is FileEntry firstEntry ? firstEntry.Name : "?";
                    Program.Log($"[FV] firstRealized name='{name}' atMs={PageElapsedMs()}");
                }
                // 容器复用后旧项的背景可能残留；相位 1 时模板已展开，按当前状态重置
                args.RegisterUpdateCallback(OnItemContentPhase1);
            }

            if (args.Item is FileEntry entry)
            {
                _realizedEntries.Add(entry);
                // 滚动/布局稳定前不逐容器请求；停下后由 QueueVisibleIcons 一次性取最终可视区
                if (!IconQueuePending)
                    QueueIconIfNearViewport(entry, args.ItemIndex);
            }
        }

        private void OnItemContentPhase1(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
                return;
            ApplySurfaceVisual(FindItemSurface(args.ItemContainer), args.Item as FileEntry);
        }

        /// <summary>只为可视区（±2 项）请求图标；二维缓存区离屏项不解码，滚入时由 ViewChanged 补。</summary>
        private void QueueIconIfNearViewport(FileEntry entry, int index)
        {
            var panel = FileList.ItemsPanelRoot as ItemsWrapGrid;
            if (panel is null)
            {
                QueueIcon(entry);
                return;
            }

            var first = panel.FirstVisibleIndex;
            var last = panel.LastVisibleIndex;
            if (first < 0 || last < 0)
            {
                // 可视区尚未确定：交给 150 ms 后的 QueueVisibleIcons，避免缓存区容器抢先解码
                return;
            }

            if (index >= first - 2 && index <= last + 2)
                QueueIcon(entry);
        }

        private void QueueVisibleIcons()
        {
            var panel = FileList.ItemsPanelRoot as ItemsWrapGrid;
            if (panel is null || VisibleItems.Count == 0)
                return;

            var first = panel.FirstVisibleIndex;
            var last = panel.LastVisibleIndex;
            if (first < 0 || last < 0)
                return;

            var start = Math.Max(0, first - 2);
            var end = Math.Min(VisibleItems.Count - 1, last + 2);
            for (var i = start; i <= end; i++)
                QueueIcon(VisibleItems[i]);
        }

        private void QueueIcon(FileEntry entry)
        {
            if (!Program.IconsEnabled)
                return;
            // 目录用文件夹字形；文件才请求 Shell 缩略图/真图标
            if (!_active || entry.IsDirectory || entry.ThumbnailState != 0)
                return;

            entry.ThumbnailState = 1;
            Interlocked.Increment(ref _iconRequested);
            var token = _cts?.Token ?? CancellationToken.None;
            var generation = _generation;
            _ = LoadIconAsync(entry, token, generation);
        }

        private async Task LoadIconAsync(FileEntry entry, CancellationToken ct, int generation)
        {
            try
            {
                await IconSemaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                entry.ThumbnailState = 3;
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                var file = await StorageFile.GetFileFromPathAsync(entry.FullPath);
                if (ct.IsCancellationRequested || generation != _generation)
                {
                    entry.ThumbnailState = 3;
                    return;
                }

                var thumb = await file.GetThumbnailAsync(
                    ThumbnailMode.ListView, IconSizePx, ThumbnailOptions.ResizeThumbnail);
                if (thumb is null || thumb.Size == 0)
                {
                    entry.ThumbnailState = 3;
                    Interlocked.Increment(ref _iconFailed);
                    return;
                }

                var dispatcher = _dispatcher;
                if (dispatcher is null || !_active || generation != _generation)
                {
                    entry.ThumbnailState = 3;
                    thumb.Dispose();
                    return;
                }

                _ = dispatcher.TryRunAsync(CoreDispatcherPriority.Low, () =>
                {
                    _ = ApplyThumbnailAsync(entry, thumb, generation);
                });
            }
            catch
            {
                entry.ThumbnailState = 3;
                Interlocked.Increment(ref _iconFailed);
            }
            finally
            {
                IconSemaphore.Release();
            }
        }

        /// <summary>在 UI 线程把 Shell 流包装成 BitmapImage；BitmapImage 有线程亲和性。</summary>
        private async Task ApplyThumbnailAsync(FileEntry entry, StorageItemThumbnail thumb, int generation)
        {
            try
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                if (!_active || generation != _generation)
                    return;

                entry.Thumbnail = bmp;
                entry.ThumbnailState = 2;
                Interlocked.Increment(ref _iconLoaded);
                TouchIconCache(entry);
                TrimIconCache();
            }
            catch
            {
                entry.ThumbnailState = 3;
                Interlocked.Increment(ref _iconFailed);
            }
            finally
            {
                thumb.Dispose();
            }
        }

        /// <summary>把刚加载成功的项放到 LRU 表尾；同一项重复加载时只移动位置。</summary>
        private void TouchIconCache(FileEntry entry)
        {
            var existing = _iconLruNodes.TryGetValue(entry, out var node) ? node : null;
            if (existing is not null)
            {
                _iconLru.Remove(existing);
                _iconLru.AddLast(existing);
                return;
            }

            _iconLruNodes[entry] = _iconLru.AddLast(entry);
        }

        /// <summary>
        /// 超过上限后从表头淘汰离屏图标；在屏项移到表尾跳过。
        /// 淘汰只把 Thumbnail 置空并复位状态，重新滚动回来会再走一次异步补图（代价计入 recovered）。
        /// </summary>
        private void TrimIconCache()
        {
            if (_iconLru.Count <= MaxCachedIcons)
                return;

            var node = _iconLru.First;
            var scanned = 0;
            var total = _iconLru.Count;
            while (node is not null && _iconLru.Count > MaxCachedIcons && scanned < total)
            {
                var next = node.Next;
                scanned++;
                var entry = node.Value;
                if (_realizedEntries.Contains(entry))
                {
                    _iconLru.Remove(node);
                    _iconLru.AddLast(node);
                }
                else
                {
                    _iconLru.Remove(node);
                    _iconLruNodes.Remove(entry);
                    entry.Thumbnail = null;
                    entry.ThumbnailState = 0;
                    _iconEvicted++;
                }

                node = next;
            }
        }

        // ---------------- R8：遮挡/不可见降级与恢复 ----------------

        private void OnOcclusionFromAnyThread(bool occluded, string reason)
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null)
                return;
            _ = dispatcher.TryRunAsync(CoreDispatcherPriority.Low, () => ApplyOcclusion(occluded, reason));
        }

        /// <summary>
        /// 分级降级（被遮挡时）：①图标位图缓存；②非当前页缓存；③枚举缓冲；④GC + 工作集修剪。
        /// 每一级单独打点，便于在台账里区分“真省了托管/私有内存”和“只把 WS 挪走”。
        /// </summary>
        private void ApplyOcclusion(bool occluded, string reason)
        {
            if (!_active)
            {
                Program.Log($"[OCC][FV] ignored occluded={occluded} (page inactive)");
                return;
            }

            if (occluded == _occluded)
                return;

            _occluded = occluded;

            if (occluded)
            {
                // 恢复计时若还没结束又进入遮挡，直接丢弃，避免出现“恢复很慢”的假结论
                _restoreTimer?.Stop();
                _restoreTimer = null;

                _occHiddenTicks = Stopwatch.GetTimestamp();
                var cacheBefore = _iconLru.Count;
                Program.Log($"[OCC][FV] enter reason={reason} items={Items.Count} " +
                    $"cacheBefore={cacheBefore} feedDone={_feedDone}");

                var m0 = GC.GetTotalMemory(true);
                var released = ClearIconCache("occ");
                var m1 = GC.GetTotalMemory(true);
                Program.Log($"[OCC][FV] tier1 icons released={released} cache={cacheBefore}->0 " +
                    $"managedDeltaKB={(m1 - m0) / 1024} (位图原生/显存部分不计入托管差值)");

                Program.Log("[OCC][FV] tier2 pages: Frame NavigationCacheMode=Disabled，无缓存页可释放 → 0");

                FindFileEnumerator.MaxBatchItems = FindFileEnumerator.OccludedBatchItems;
                Program.Log($"[OCC][FV] tier3 batchLimit={FindFileEnumerator.MaxBatchItems} " +
                    $"bufferPeak={_maxBatchItems} (只在枚举尚未完成时生效；已完成则为 0)");

                Program.TrimWorkingSet("occ.t4");
                return;
            }

            var hiddenMs = (long)((Stopwatch.GetTimestamp() - _occHiddenTicks) * 1000.0 / Stopwatch.Frequency);
            FindFileEnumerator.MaxBatchItems = FindFileEnumerator.NormalBatchItems;

            var pending = 0;
            foreach (var entry in _realizedEntries)
            {
                if (entry.ThumbnailState == 0 || entry.ThumbnailState == 1)
                {
                    pending++;
                    QueueIcon(entry);
                }
            }

            Program.Log($"[OCC][FV] exit hiddenMs={hiddenMs} reloadQueued={pending} cache={_iconLru.Count}");
            StartRestoreWatch();
        }

        private int ClearIconCache(string why)
        {
            var released = 0;
            foreach (var entry in _iconLru)
            {
                entry.Thumbnail = null;
                entry.ThumbnailState = 0;
                released++;
            }

            _iconLru.Clear();
            _iconLruNodes.Clear();
            Program.Log($"[OCC][FV] iconCache cleared n={released} why={why}");
            return released;
        }

        /// <summary>恢复耗时：从重新可见到“在屏项都有图标”的时间；超过 1 s 记为不划算。</summary>
        private void StartRestoreWatch()
        {
            _restoreTimer?.Stop();
            _restoreStartTicks = Stopwatch.GetTimestamp();
            _restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _restoreTimer.Tick += (_, __) =>
            {
                var pending = 0;
                foreach (var entry in _realizedEntries)
                {
                    if (entry.ThumbnailState == 0 || entry.ThumbnailState == 1)
                        pending++;
                }

                var elapsed = (long)((Stopwatch.GetTimestamp() - _restoreStartTicks) * 1000.0 / Stopwatch.Frequency);
                if (pending == 0 || elapsed >= 3000)
                {
                    _restoreTimer?.Stop();
                    _restoreTimer = null;
                    Program.Log($"[OCC][FV] restore pending={pending} restoreMs={elapsed} " +
                        $"verdict={(pending == 0 && elapsed <= 1000 ? "OK(<1s)" : "SLOW/INCOMPLETE")}");
                    LogView("restore");
                }
            };
            _restoreTimer.Start();
        }

        private void HookMemoryManager()
        {
            try
            {
                Program.Log($"[MEMPRESSURE] level={MemoryManager.AppMemoryUsageLevel} " +
                    $"usageMB={MemoryManager.AppMemoryUsage / 1048576.0:F1}");
                MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
                _memoryHook = true;
            }
            catch (Exception ex)
            {
                // 无包身份或 API 不可用时如实记录，不做 workaround
                Program.Log($"[MEMPRESSURE] unavailable {ex.GetType().Name} hr=0x{ex.HResult:X8}");
            }
        }

        private void OnAppMemoryUsageIncreased(object? sender, object e)
        {
            try
            {
                var level = MemoryManager.AppMemoryUsageLevel;
                Program.Log($"[MEMPRESSURE] increased level={level} " +
                    $"usageMB={MemoryManager.AppMemoryUsage / 1048576.0:F1}");
                if (level != Windows.System.AppMemoryUsageLevel.Low)
                {
                    _ = _dispatcher?.TryRunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (_iconLru.Count > 0)
                            ClearIconCache("pressure");
                    });
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[MEMPRESSURE] handler failed {ex.GetType().Name}");
            }
        }

        // ---------------- 滚动观测（虚拟化/加载表现的可见证据） ----------------

        private void AttachScrollViewer()
        {
            if (_scrollViewer is not null)
                return;

            _scrollViewer = FindScrollViewer(FileList);
            if (_scrollViewer is not null)
            {
                _scrollViewer.ViewChanged += Scroll_ViewChanged;
                Program.Log("[FV] scrollviewer attached");
                return;
            }

            // Loaded 时模板可能还没展开（NativeAOT 下实测会遇到），下一次布局再试
            if (_attachAttempts++ < 2)
                Program.Log($"[FV] scrollviewer not found (attempt={_attachAttempts})");
        }

        private void OnListLayoutUpdated(object? sender, object e)
        {
            AttachScrollViewer();
            // GridView 的二维缓存会提前 realize 数百个容器；布局稳定后再按最终可视区补图标，
            // 避免离屏项/滚动中间位置触发大量 Shell 缩略图解码把 UI 线程拖住。
            ScheduleVisibleIconQueue();
            if (!_layoutPending)
                return;

            _layoutPending = false;
            var ms = (long)((Stopwatch.GetTimestamp() - _layoutPendingTicks) * 1000.0 / Stopwatch.Frequency);
            if (ms > _layoutMaxMs)
                _layoutMaxMs = ms;
            Program.Log($"[FV] layoutAfterBatch n={_layoutPendingBatch} items={_layoutPendingItems} ms={ms}");
        }

        private void Scroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_feedDone)
                return;

            // 滚动时新进入可视区的条目不会重发 ContainerContentChanging；
            // 这里只重排定时器，停下后再按最终可视区补图标（跳转经过的中间位置不再解码）
            ScheduleVisibleIconQueue(120);

            var now = Stopwatch.GetTimestamp();
            if (now - _lastViewLogTicks >= ViewLogIntervalTicks)
            {
                _lastViewLogTicks = now;
                LogView("moving");
            }

            if (_scrollSummaryTimer is null)
            {
                ResetHeartbeatWindow();
                _scrollSummaryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _scrollSummaryTimer.Tick += (_, __) =>
                {
                    LogView("settle");
                    Program.Log($"[FV] heartbeat scroll maxGapMs={_hbMaxGapMs} samples={_hbSamples}");
                    MemProbe.Log("scrollSettle", Items.Count, EstimateModelBytes(),
                        _iconLru.Count, _maxBatchItems);
                    _scrollSummaryTimer?.Stop();
                    _scrollSummaryTimer = null;
                };
            }
            else
            {
                _scrollSummaryTimer.Stop();
            }

            _scrollSummaryTimer.Start();
        }

        private void LogView(string reason)
        {
            var panel = FileList.ItemsPanelRoot as ItemsWrapGrid;
            var first = panel?.FirstVisibleIndex ?? -1;
            var last = panel?.LastVisibleIndex ?? -1;
            var missing = 0;
            foreach (var e in _realizedEntries)
            {
                if (e.ThumbnailState == 0 || e.ThumbnailState == 1)
                    missing++;
            }

            Program.Log($"[FV] view({reason}) first={first} last={last} realized={_realized} " +
                $"total={Items.Count} visible={VisibleItems.Count} page={_pageIndex + 1}/{PageCount} " +
                $"icons requested={_iconRequested} " +
                $"loaded={Volatile.Read(ref _iconLoaded)} failed={Volatile.Read(ref _iconFailed)} " +
                $"evicted={_iconEvicted} cache={_iconLru.Count} visibleMissing={missing}");
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv)
                return sv;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found is not null)
                    return found;
            }

            return null;
        }
    }
}
