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
    /// 列表项：所有显示用字符串在后台枚举线程里预格式化，UI 只读（避免绑定 getter 现算字符串）。
    /// Thumbnail 在 UI 线程由两段式图标管线补齐，失败则保留类型字形。
    /// </summary>
    public sealed class FileEntry : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        internal FileEntry(string name, string fullPath, bool isDirectory, ulong size, DateTime modified)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            Size = size;
            Modified = modified;
            SizeText = isDirectory ? "" : FormatSize(size);
            DateText = modified == default ? "" : modified.ToString("yyyy-MM-dd HH:mm");
            var dot = name.LastIndexOf('.');
            var ext = dot > 0 ? name.Substring(dot).ToLowerInvariant() : "";
            TypeText = isDirectory ? "文件夹" : (ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() + " 文件" : "文件");
            Glyph = GlyphFor(ext, isDirectory);
        }

        public string Name { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public ulong Size { get; }
        public DateTime Modified { get; }
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
        private const int MaxBatchItems = 20_000;

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

                    var entry = BuildEntry(path, ref data);
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

        private static unsafe FileEntry? BuildEntry(string dir, ref WIN32_FIND_DATAW data)
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
                return new FileEntry(name, Path.Combine(dir, name), isDir, size, modified);
            }
        }
    }
}
