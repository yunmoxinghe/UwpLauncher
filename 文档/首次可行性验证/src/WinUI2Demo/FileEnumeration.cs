using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;

namespace WinUI2Demo
{
    /// <summary>
    /// 单次枚举内的显示字符串去重池：同值只保留一个实例（SizeText/DateText/TypeText）。
    /// 每类最多记录 8192 个不同值，达到上限后只查不增——这样在全不同的真实目录里，
    /// 额外开销也封顶在几十万个引用以内，不会把内存换成“字典更大”。
    /// </summary>
    internal sealed class DisplayTextPool
    {
        private const int MaxDistinct = 8192;
        private readonly Dictionary<string, string> _size = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _date = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _type = new(StringComparer.Ordinal);

        internal string Size(string value) => Share(_size, value);
        internal string Date(string value) => Share(_date, value);
        internal string Type(string value) => Share(_type, value);

        private static string Share(Dictionary<string, string> map, string value)
        {
            if (map.TryGetValue(value, out var existing))
                return existing;
            if (map.Count < MaxDistinct)
                map[value] = value;
            return value;
        }
    }

    /// <summary>
    /// 列表项：所有显示用字符串在后台枚举线程里预格式化，UI 只读（避免绑定 getter 现算字符串）。
    /// Thumbnail 在 UI 线程由两段式图标管线补齐，失败则保留类型字形。
    /// </summary>
    public sealed class FileEntry : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;
        // 父目录在整批条目间共享同一个字符串实例（避免 10 万份全路径重复驻留）；
        // FullPath 只在补图标时按需拼一次，不进常驻模型。
        private readonly string _dir;

        internal FileEntry(string dir, string name, bool isDirectory, ulong size, DateTime modified,
            DisplayTextPool? textPool)
        {
            _dir = dir;
            Name = name;
            IsDirectory = isDirectory;
            var sizeText = isDirectory ? "" : FormatSize(size);
            var dateText = modified == default ? "" : modified.ToString("yyyy-MM-dd HH:mm");
            var dot = name.LastIndexOf('.');
            var ext = dot > 0 ? name.Substring(dot).ToLowerInvariant() : "";
            var typeText = isDirectory ? "文件夹" : (ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() + " 文件" : "文件");
            SizeText = textPool is null ? sizeText : textPool.Size(sizeText);
            DateText = textPool is null ? dateText : textPool.Date(dateText);
            TypeText = textPool is null ? typeText : textPool.Type(typeText);
            Glyph = GlyphFor(ext, isDirectory);
        }

        public string Name { get; }
        public string FullPath => Path.Combine(_dir, Name);
        internal string DirPath => _dir;
        public bool IsDirectory { get; }
        public string SizeText { get; }
        public string DateText { get; }
        public string TypeText { get; }
        public string Glyph { get; }

        /// <summary>0=未请求 1=加载中 2=成功 3=失败（只由 UI 线程读写）。</summary>
        internal int ThumbnailState;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            internal set
            {
                if (_thumbnail == value)
                    return;
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatSize(ulong bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            var kb = bytes / 1024.0;
            if (kb < 1024)
                return $"{kb:F1} KB";
            var mb = kb / 1024.0;
            if (mb < 1024)
                return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }

        private static string GlyphFor(string ext, bool isDirectory)
        {
            if (isDirectory)
                return "\uE8B7"; // Folder

            switch (ext)
            {
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif":
                case ".webp": case ".tif": case ".tiff": case ".ico": case ".heic":
                    return "\uE91B"; // Photo
                case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv":
                case ".webm": case ".flv": case ".m4v":
                    return "\uE714"; // Video
                case ".mp3": case ".wav": case ".flac": case ".aac": case ".ogg":
                case ".wma": case ".m4a":
                    return "\uE8D6"; // Audio
                case ".zip": case ".7z": case ".rar": case ".tar": case ".gz":
                case ".bz2": case ".xz": case ".cab": case ".iso":
                    return "\uE7B8"; // Zip
                case ".exe": case ".msi": case ".msix": case ".appx":
                    return "\uE8A5"; // Application
                case ".txt": case ".log": case ".csv":
                    return "\uE8A5"; // Page-ish
                default:
                    return "\uE7C3"; // Page
            }
        }
    }

    /// <summary>一批已构造好的列表项；Items 在后台线程构造，UI 线程只做引用搬运。</summary>
    internal sealed class FileBatch
    {
        internal List<FileEntry> Items = null!;
        internal int BatchIndex;
        internal int TotalSoFar;
        internal long EnumElapsedMs;
        internal bool IsFirst;
        internal bool IsFinal;
    }

    /// <summary>
    /// Win32 目录枚举：FindFirstFileExW + FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH。
    /// 全程在后台线程；按时间片分批（首批最多 60 项尽快上屏，之后每 500 ms 合并一次，
    /// 另外单批上限 20 000 项，避免慢盘上一次性把超大目录全量刷给 UI）。
    /// </summary>
    internal static class FindFileEnumerator
    {
        private const int FindExInfoBasic = 1;
        private const int FindExSearchNameMatch = 0;
        private const uint FIND_FIRST_EX_LARGE_FETCH = 2;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int ErrorNoMoreFiles = 18;

        private const int FirstBatchMaxItems = 60;
        private const int FirstBatchMinItems = 30;
        private const long FirstBatchMaxDelayMs = 25;
        private const long FirstBatchHardDeadlineMs = 100;
        private const long BatchIntervalMs = 500;
        internal const int NormalBatchItems = 20_000;
        internal const int OccludedBatchItems = 1_500;
        /// <summary>单批上限；被遮挡时调小以减少枚举缓冲与一次 Reset 的驻留/停顿。</summary>
        internal static volatile int MaxBatchItems = NormalBatchItems;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            public fixed char cFileName[260];
            public fixed char cAlternateFileName[14];
        }

        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW")]
        private static extern nint FindFirstFileExW(nint lpFileName, int fInfoLevelId, nint lpFindFileData,
            int fSearchOp, nint lpSearchFilter, uint dwAdditionalFlags);

        [DllImport("kernel32.dll", EntryPoint = "FindNextFileW")]
        private static extern bool FindNextFileW(nint hFindFile, nint lpFindFileData);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(nint hFindFile);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        /// <summary>
        /// 启动一次枚举。onBatch/onDone 在后台线程回调，调用方负责切回 UI 线程。
        /// </summary>
        internal static Task EnumerateAsync(
            string path,
            Action<FileBatch> onBatch,
            Action<int, long, string?> onDone,
            CancellationToken ct)
        {
            return Task.Run(() => RunEnumeration(path, onBatch, onDone, ct), ct);
        }

        private static void RunEnumeration(
            string path,
            Action<FileBatch> onBatch,
            Action<int, long, string?> onDone,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                RunEnumerationCore(path, onBatch, onDone, ct, sw);
            }
            catch (Exception ex)
            {
                // 后台异常必须回报，否则页面只会停在“枚举中…”且没有任何诊断信息
                onDone(0, sw.ElapsedMilliseconds, $"exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RunEnumerationCore(
            string path,
            Action<FileBatch> onBatch,
            Action<int, long, string?> onDone,
            CancellationToken ct,
            Stopwatch sw)
        {
            var pattern = Path.Combine(path, "*");
            var chars = pattern.ToCharArray();
            var data = default(WIN32_FIND_DATAW);
            nint handle;
            unsafe
            {
                fixed (char* p = chars)
                    handle = FindFirstFileExW((nint)p, FindExInfoBasic, (nint)(&data),
                        FindExSearchNameMatch, 0, FIND_FIRST_EX_LARGE_FETCH);
            }

            if (handle == -1)
            {
                onDone(0, sw.ElapsedMilliseconds, $"open failed err={GetLastError()} path={path}");
                return;
            }

            var buffer = new List<FileEntry>(1024);
            var textPool = new DisplayTextPool();
            var batchIndex = 0;
            var total = 0;
            var firstSent = false;
            // 25ms 窗口从“打开目录之后”开始算：FindFirstFileEx 的冷启动可能就超过 25ms，
            // 若从线程启动计时，首批会只剩 1 项（实测过），失去“先上屏几条”的意义。
            var firstWindowStart = sw.ElapsedMilliseconds;
            var lastFlush = firstWindowStart;
            string? error = null;
            var lastErr = 0u;

            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        error = "cancelled";
                        break;
                    }

                    var entry = BuildEntry(path, ref data, textPool);
                    if (entry is not null)
                    {
                        buffer.Add(entry);
                        total++;
                    }

                    var now = sw.ElapsedMilliseconds;
                    var firstElapsed = now - firstWindowStart;
                    var due = !firstSent
                        ? buffer.Count >= FirstBatchMaxItems ||
                          (firstElapsed >= FirstBatchMaxDelayMs && buffer.Count >= FirstBatchMinItems) ||
                          firstElapsed >= FirstBatchHardDeadlineMs
                        : buffer.Count >= MaxBatchItems || now - lastFlush >= BatchIntervalMs;

                    if (due && buffer.Count > 0)
                    {
                        batchIndex++;
                        onBatch(new FileBatch
                        {
                            Items = buffer,
                            BatchIndex = batchIndex,
                            TotalSoFar = total,
                            EnumElapsedMs = now,
                            IsFirst = !firstSent,
                            IsFinal = false
                        });
                        firstSent = true;
                        buffer = new List<FileEntry>(Math.Min(MaxBatchItems, 4096));
                        lastFlush = now;
                    }

                    // 每 256 项才检查一次取消，减少热循环里的分支成本
                    if ((total & 0xFF) == 0 && ct.IsCancellationRequested)
                    {
                        error = "cancelled";
                        break;
                    }

                    unsafe
                    {
                        var d = (WIN32_FIND_DATAW*)&data;
                        if (!FindNextFileW(handle, (nint)d))
                        {
                            lastErr = GetLastError();
                            break;
                        }
                    }
                }
            }
            finally
            {
                FindClose(handle);
            }

            if (error is null && lastErr != 0 && lastErr != ErrorNoMoreFiles)
            {
                // 枚举结束但错误码不是 ERROR_NO_MORE_FILES：通常是权限/介质错误
                error = $"FindNextFile err={lastErr}";
            }

            if (buffer.Count > 0)
            {
                batchIndex++;
                onBatch(new FileBatch
                {
                    Items = buffer,
                    BatchIndex = batchIndex,
                    TotalSoFar = total,
                    EnumElapsedMs = sw.ElapsedMilliseconds,
                    IsFirst = !firstSent,
                    IsFinal = true
                });
            }

            onDone(total, sw.ElapsedMilliseconds, error);
        }

        private static unsafe FileEntry? BuildEntry(string dir, ref WIN32_FIND_DATAW data, DisplayTextPool textPool)
        {
            fixed (char* p = data.cFileName)
            {
                var len = 0;
                while (len < 260 && p[len] != '\0')
                    len++;

                if (len <= 0)
                    return null;

                // 跳过 "." 与 ".."，避免目录视图出现自引用项
                if (p[0] == '.' && (len == 1 || (len == 2 && p[1] == '.')))
                    return null;

                var name = new string(p, 0, len);
                var isDir = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                var size = ((ulong)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                var ft = data.ftLastWriteTime;
                var modified = ft.dwLowDateTime == 0 && ft.dwHighDateTime == 0
                    ? default
                    : DateTime.FromFileTimeUtc(((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime).ToLocalTime();
                return new FileEntry(dir, name, isDir, size, modified, textPool);
            }
        }
    }

    /// <summary>
    /// 内存基线探针：一行日志同时记录整进程（WS/Private/PeakWS）、托管堆、GC 次数与暂停，
    /// 以及调用方给的条目模型/图标/枚举缓冲估算。用于优化台账的逐轮对照。
    /// </summary>
    internal static class MemProbe
    {
        private static readonly Process Proc = Process.GetCurrentProcess();
        private static double _pauseBaselineMs;
        private static int _g0Base, _g1Base, _g2Base;

        /// <summary>内存测量专用：打点前强制 GC，得到的 managed 才是“仍存活”的占用。</summary>
        internal static bool ForceGc;

        /// <summary>每次加载开始时调用，之后 Log 里的 pauseΔ/gcΔ 就是本次加载的增量。</summary>
        internal static void ResetBaseline()
        {
            _pauseBaselineMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            _g0Base = GC.CollectionCount(0);
            _g1Base = GC.CollectionCount(1);
            _g2Base = GC.CollectionCount(2);
        }

        internal static void Log(string tag, int items, long estModelBytes, int iconsLoaded, int bufferPeakItems)
        {
            try
            {
                // 先取 GC 统计（探针自己触发的强制 GC 不能算进“枚举+喂入”的暂停/次数）
                var pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
                var g0 = GC.CollectionCount(0);
                var g1 = GC.CollectionCount(1);
                var g2 = GC.CollectionCount(2);

                if (ForceGc)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }

                Proc.Refresh();
                var ws = Proc.WorkingSet64;
                var priv = Proc.PrivateMemorySize64;
                var peakWs = Proc.PeakWorkingSet64;
                var managed = GC.GetTotalMemory(false);
                var pauseDelta = pauseMs - _pauseBaselineMs;
                Program.Log($"[MEM] tag={tag} items={items} " +
                    $"ws={ws / 1048576.0:F1}MB private={priv / 1048576.0:F1}MB peakWs={peakWs / 1048576.0:F1}MB " +
                    $"managed={managed / 1048576.0:F2}MB " +
                    $"gcDelta={g0 - _g0Base}/{g1 - _g1Base}/{g2 - _g2Base} " +
                    $"pauseDelta={pauseDelta:F1}ms pauseTotal={pauseMs:F1}ms " +
                    $"estModel={estModelBytes / 1048576.0:F2}MB estIcons={iconsLoaded * 32L * 32L * 4 / 1048576.0:F2}MB " +
                    $"bufferPeak={bufferPeakItems}");
            }
            catch
            {
                // 探针失败不影响页面功能
            }
        }
    }
}
