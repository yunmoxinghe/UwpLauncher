using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

        internal void RaiseReset()
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// 文件视图页：Win32 后台枚举 + 时间片批量喂入 + ListView 虚拟化 + 两段式图标。
    /// 顶栏的透明/嵌入桌面/退出仍由 DemoShell 控制，本页不改变宿主样式。
    /// </summary>
    public sealed partial class FileViewPage : Page
    {
        private static readonly SemaphoreSlim IconSemaphore = new(8, 8);
        private const int IconSizePx = 32;

        public BulkObservableCollection<FileEntry> Items { get; } = new();

        private CoreDispatcher? _dispatcher;
        private CancellationTokenSource? _cts;
        private DispatcherTimer? _progressTimer;
        private DispatcherTimer? _heartbeatTimer;
        private DispatcherTimer? _scrollSummaryTimer;
        private DispatcherTimer? _iconSettleTimer;
        private ScrollViewer? _scrollViewer;
        private bool _active;
        private bool _feedDone;
        private bool _firstRealizedLogged;
        private int _generation;
        private static readonly long ViewLogIntervalTicks = 500 * Stopwatch.Frequency / 1000;

        private long _pageStartTicks;
        private long _firstPaintMs;
        private long _enumDoneMs;
        private long _feedDoneMs;
        private long _uiApplyMaxMs;
        private int _batchCount;
        private int _realized;
        private int _enumScanned;
        private int _iconRequested;
        private int _iconLoaded;
        private int _iconFailed;
        private long _hbLastTicks;
        private long _hbMaxGapMs;
        private int _hbSamples;
        private long _lastViewLogTicks;

        public FileViewPage()
        {
            InitializeComponent();
            FileList.Loaded += (_, __) => AttachScrollViewer();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _dispatcher = Dispatcher;
            var path = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(path))
                path = Program.FileViewDefaultPath;
            if (string.IsNullOrWhiteSpace(path))
                path = DefaultDirectory();

            PathBox.Text = path;
            Program.Log($"[FV] page start path='{path}'");
            StartLoad(path);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Program.Log($"[FV] navigated from mode={e.NavigationMode}");
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

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Program.Log("[FV] back => GoBack()");
                Frame.GoBack();
            }
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            var dir = PathBox.Text.Trim();
            if (string.IsNullOrEmpty(dir))
                return;
            Program.Log($"[FV] reload path='{dir}'");
            StartLoad(dir);
        }

        private void StartLoad(string path)
        {
            StopLoad();

            Items.ClearSilent();
            Items.RaiseReset();

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
            _realized = 0;
            _enumScanned = 0;
            _iconRequested = 0;
            _iconLoaded = 0;
            _iconFailed = 0;
            _lastViewLogTicks = 0;
            _hbMaxGapMs = 0;
            _hbSamples = 0;
            _hbLastTicks = Stopwatch.GetTimestamp();

            MetricsText.Text = $"路径: {path} | 枚举中…";
            StatusText.Text = "枚举中… 后台线程 FindFirstFileExW（FindExInfoBasic + LARGE_FETCH）";

            StartTimers();

            if (!Directory.Exists(path))
            {
                Program.Log($"[FV] path missing '{path}'");
                _feedDone = true;
                StatusText.Text = $"目录不存在: {path}";
                MetricsText.Text = $"路径: {path} | 错误: 目录不存在";
                _progressTimer?.Stop();
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

            _progressTimer?.Stop();
            _progressTimer = null;
            _heartbeatTimer?.Stop();
            _heartbeatTimer = null;
            _scrollSummaryTimer?.Stop();
            _scrollSummaryTimer = null;
            _iconSettleTimer?.Stop();
            _iconSettleTimer = null;
        }

        private void StartTimers()
        {
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, __) => UpdateProgressText();
            _progressTimer.Start();

            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _heartbeatTimer.Tick += (_, __) => OnHeartbeat();
            _heartbeatTimer.Start();
        }

        /// <summary>UI 线程心跳：两次 Tick 的间隔最大值就是喂入期间 UI 被占用的最长时段。</summary>
        private void OnHeartbeat()
        {
            var now = Stopwatch.GetTimestamp();
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
            _batchCount = batch.BatchIndex;

            var applyMs = (long)((Stopwatch.GetTimestamp() - applyStart) * 1000.0 / Stopwatch.Frequency);
            if (applyMs > _uiApplyMaxMs)
                _uiApplyMaxMs = applyMs;

            if (batch.IsFirst)
            {
                _firstPaintMs = PageElapsedMs();
                Program.Log($"[FV] firstBatch count={batch.Items.Count} atMs={_firstPaintMs} " +
                    $"uiDelayMs={uiDelayMs} applyMs={applyMs} enumMs={batch.EnumElapsedMs}");
            }

            Program.Log($"[FV] batch n={batch.BatchIndex} count={batch.Items.Count} shown={Items.Count} " +
                $"scanned={batch.TotalSoFar} enumMs={batch.EnumElapsedMs} uiDelayMs={uiDelayMs} applyMs={applyMs}");

            UpdateProgressText();
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

            _progressTimer?.Stop();
            ResetHeartbeatWindow();

            // 图标管线是异步的，枚举完成后再等 2 秒记录一次最终计数（失败回退也要可见）
            _iconSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _iconSettleTimer.Tick += (_, __) =>
            {
                Program.Log($"[FV] icons settle requested={_iconRequested} " +
                    $"loaded={Volatile.Read(ref _iconLoaded)} failed={Volatile.Read(ref _iconFailed)} " +
                    $"realized={_realized}");
                _iconSettleTimer?.Stop();
                _iconSettleTimer = null;
            };
            _iconSettleTimer.Start();

            var iconOk = Volatile.Read(ref _iconLoaded);
            var iconFail = Volatile.Read(ref _iconFailed);
            var summary = $"完成: {Items.Count} 项 · 枚举 {enumMs} ms · 首批到列表 {_firstPaintMs} ms · " +
                $"全部入列表 {_feedDoneMs} ms · 批次 {_batchCount} · " +
                $"图标 {iconOk}/{_iconRequested} 成功({iconFail} 失败)" +
                (error is null ? "" : $" · 错误: {error}");
            StatusText.Text = summary;
            MetricsText.Text = $"路径: {PathBox.Text} | UI 最大停顿 {_hbMaxGapMs} ms/{_hbSamples} 次 · " +
                $"UI 应用单批最大 {_uiApplyMaxMs} ms";

            UpdateProgressText();
        }

        private void UpdateProgressText()
        {
            if (_feedDone)
                return;

            var scanned = Volatile.Read(ref _enumScanned);
            StatusText.Text = $"枚举中… 后台已扫描 {scanned} 项 / 已显示 {Items.Count} 项 · " +
                $"批次 {_batchCount} · 首批 {(_firstPaintMs > 0 ? _firstPaintMs + " ms" : "未上屏")}";
            MetricsText.Text = $"路径: {PathBox.Text} | UI 最大停顿 {_hbMaxGapMs} ms/{_hbSamples} 次";
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
                return;

            if (args.Phase == 0)
            {
                _realized++;
                if (!_firstRealizedLogged)
                {
                    _firstRealizedLogged = true;
                    var name = args.Item is FileEntry firstEntry ? firstEntry.Name : "?";
                    Program.Log($"[FV] firstRealized name='{name}' atMs={PageElapsedMs()}");
                }
            }

            if (args.Item is FileEntry entry)
                QueueIcon(entry);
        }

        private void QueueIcon(FileEntry entry)
        {
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
            }
        }

        private void Scroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_feedDone)
                return;

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
            var panel = FileList.ItemsPanelRoot as ItemsStackPanel;
            var first = panel?.FirstVisibleIndex ?? -1;
            var last = panel?.LastVisibleIndex ?? -1;
            Program.Log($"[FV] view({reason}) first={first} last={last} realized={_realized} " +
                $"shown={Items.Count} icons requested={_iconRequested} " +
                $"loaded={Volatile.Read(ref _iconLoaded)} failed={Volatile.Read(ref _iconFailed)}");
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
