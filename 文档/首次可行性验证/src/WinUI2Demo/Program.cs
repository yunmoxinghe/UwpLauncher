using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Xaml;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace WinUI2Demo
{
    /// <summary>
    /// 最小 UWP XAML Island 透明背景探针。
    /// 复刻 DesktopFlyouts 的 UWP 路径：WS_EX_NOREDIRECTIONBITMAP 宿主 + DesktopWindowXamlSource
    /// + Window.Current 上的 IXamlSourceTransparency(IsBackgroundTransparent)。
    /// 支持三种宿主模式：topmost（DesktopFlyouts 原方案）、plain、workerw（SetParent 到桌面 WorkerW）。
    /// </summary>
    internal static unsafe class Program
    {
        // 物理像素；按 1280x720/144DPI 的屏幕留出边距，约合 747x427 DIP
        private const int HostX = 80, HostY = 60, HostW = 1120, HostH = 640;

        // 全局热键：嵌入 WorkerW 后窗口没有焦点，按钮点不到，必须靠热键切回/退出
        private const int HotkeyTransparency = 1;
        private const int HotkeyMode = 2;
        private const int HotkeyClose = 3;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkF2 = 0x71;
        private const uint VkF3 = 0x72;
        private const uint VkF4 = 0x73;

        private const uint WS_POPUP = 0x80000000u;
        private const uint WS_CHILD = 0x40000000u;
        private const uint WS_VISIBLE = 0x10000000u;
        private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000u;
        private const uint WS_EX_TOOLWINDOW = 0x00000080u;
        private const uint WS_EX_TOPMOST = 0x00000008u;
        private const uint WS_EX_NOACTIVATE = 0x08000000u;

        private const uint WM_SIZE = 0x0005u;
        private const uint WM_DESTROY = 0x0002u;
        private const uint WM_CLOSE = 0x0010u;
        private const uint WM_QUIT = 0x0012u;
        private const uint WM_HOTKEY = 0x0312u;
        private const uint WM_SPAWN_WORKERW = 0x052Cu;

        // 工作区检测：消息与常量
        private const uint WM_SETTINGCHANGE = 0x001Au;
        private const uint WM_DISPLAYCHANGE = 0x007Eu;
        private const uint WM_DPICHANGED = 0x02E0u;
        private const uint SPI_GETWORKAREA = 0x0030u;
        private const uint SPI_SETWORKAREA = 0x002Fu;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const int SM_CMONITORS = 80;
        private const uint ABM_GETTASKBARPOS = 5u;
        private const uint MONITORINFOF_PRIMARY = 1u;

        // 输入诊断用：只记录关心的几条，避免 mousemove 刷屏
        private const uint WM_ACTIVATE = 0x0006u;
        private const uint WM_SETFOCUS = 0x0007u;
        private const uint WM_MOUSEACTIVATE = 0x0021u;
        private const uint WM_SETCURSOR = 0x0020u;
        private const uint WM_NCHITTEST = 0x0084u;
        private const uint WM_LBUTTONDOWN = 0x0201u;
        private const uint WM_LBUTTONUP = 0x0202u;
        private const uint WM_POINTERDOWN = 0x0246u;
        private const uint WM_POINTERUP = 0x0247u;
        private const int MA_ACTIVATE = 1;

        private static readonly Guid IID_IDesktopWindowXamlSourceNative2 = new("E3DCD8C7-3057-4692-99C3-7B7720AFDA31");
        private static readonly Guid IID_ICoreWindowInterop = new("45D64A29-A63E-4CB6-B498-5781D298CB4F");
        private static readonly Guid IID_IWindowPrivate = new("06636C29-5A17-458D-8EA2-2422D997A922");

        private static readonly WNDPROC s_wndProc = new(WndProc);

        private static StreamWriter _log = StreamWriter.Null;
        private static readonly object s_logLock = new();
        // v1.0 默认形态：启动即嵌入桌面（可点击的 desktoppane）+ 透明开启；--mode/--transparent 仍可覆盖
        private static string _mode = "desktoppane";
        private static bool _transparent = true;
        // 启动时序：Main 起点的统一相对时间戳（毫秒），用于证明“创建 → Loaded → 首帧 → 动画”的顺序
        private static readonly Stopwatch s_startupWatch = new();
        internal static bool ShellLoaded;
        /// <summary>启动入场动画期间（隐藏创建→首帧→动画开始）暂停 UI 心跳统计，避免把启动闸门算成喂入停顿。</summary>
        internal static volatile bool StartupRevealInProgress;
        private static bool _firstRenderingTickLogged;
        private static int _frameTicks;
        private static bool _selftest;
        private static bool _exitRequested;
        private static bool _desktopMode;
        private static bool _transparentReadback;
        private static DemoShell? _shell;
        private static int _selftestStep;

        private static readonly HWND InsertAfterTop = new(IntPtr.Zero);
        private static readonly HWND InsertAfterBottom = new((IntPtr)1);

        private static HWND _host = HWND.Null;
        private static HWND _xamlHwnd = HWND.Null;
        private static HWND _coreHwnd = HWND.Null;
        private static HWND _workerW = HWND.Null;
        private static bool _desktoppane;
        private static DesktopWindowXamlSource? _dws;
        private static IDesktopWindowXamlSourceNative2? _native2;
        private static Frame? _frame;

        // 工作区/显示器状态
        private static readonly List<MonitorInfo> s_monitors = new();
        private static MonitorInfo? s_currentMonitor;
        private static string _workspaceText = "工作区: 初始化中…";
        internal static string FileViewDefaultPath = "";   // --dir：文件视图默认目录
        private static bool _fillDesktop;   // false=铺满 rcWork，true=铺满 rcMonitor
        private static bool _layoutFixed;   // --fill none：保留测试用的固定小窗口
        private static RECT32 s_sysWork;
        private static bool s_sysWorkOk;
        private static uint s_taskbarEdge = uint.MaxValue;
        private static RECT32 s_taskbarRect;

        private sealed class MonitorInfo
        {
            public nint Handle;
            public string Device = "";
            public bool Primary;
            public RECT32 Monitor;
            public RECT32 Work;
            public uint DpiX;
            public uint DpiY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT32
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFOEXW
        {
            public int cbSize;
            public RECT32 rcMonitor;
            public RECT32 rcWork;
            public uint dwFlags;
            public fixed char szDevice[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT32 rc;
            public nint lParam;
        }

        private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData);
        private static readonly MonitorEnumProc s_monitorEnum = OnMonitorEnum;

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(POINT32 point);

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint flags);

        // 项目关闭了运行时封送（DisableRuntimeMarshalling）：全部用 nint 传结构指针
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(nint hMonitor, nint lpmi);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, nint lpfnEnum, nint dwData);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(nint hMonitor, int dpiType, nint dpiX, nint dpiY);

        [DllImport("shell32.dll")]
        private static extern nint SHAppBarMessage(uint dwMessage, nint pData);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---- 遮挡检测 / 工作集修剪（R8）----
        private const int GA_ROOT = 2;
        private const uint GW_HWNDPREV = 3u;

        internal static event Action<bool, string>? OcclusionChanged;
        internal static bool IsHostOccluded => s_occState ?? false;
        /// <summary>性能口径复测时可关闭（--occ 0）：避免 IDE/终端窗口盖住桌面时干扰枚举批次。</summary>
        internal static bool OcclusionEnabled = true;
        /// <summary>诊断开关（--icons 0）：跳过 Shell 缩略图，用于把“网格布局代价”和“图标解码代价”拆开测。</summary>
        internal static bool IconsEnabled = true;

        private static System.Threading.Timer? s_occTimer;
        private static int s_occBusy;
        private static bool? s_occState;

        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern nint GetWindow(nint hwnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(nint hwnd);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool GetWindowRect(nint hwnd, nint lprc);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
        private static extern int GetClassName(nint hwnd, nint buf, int max);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(nint hProcess);

        // 首帧判据的“提交”侧证据：DwmFlush 会等到 DWM 完成一次合成，
        // 比单纯的 CompositionTarget.Rendering（帧生成前回调）更接近“已上屏”。
        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private const int StartupHiddenFrameWaitMs = 250;
        // 100k 项网格的首次“带内容绘制”实测约 400-600ms，留足余量；超时则直接降级播动画
        private const int StartupShownFrameWaitMs = 1_200;

        [STAThread]
        private static int Main(string[] args)
        {
            ParseArgs(args);
            var logPath = GetArgValue(args, "--log");
            if (string.IsNullOrEmpty(logPath))
            {
                // 打包安装后当前目录可能不可写，默认写到 LocalAppData
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinUI2Demo");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "winui2demo.log");
            }

            try
            {
                _log = new StreamWriter(logPath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch
            {
                // 无法写日志时仍继续，输出会丢失
            }

            s_startupWatch.Start();
            Log("=== WinUI2Demo v1.0 ===");
            Log($"{StartupTag()} pid={Environment.ProcessId} os={Environment.OSVersion.VersionString}");

            var hr = PInvoke.RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED);
            Log($"RoInitialize(STA) => hr=0x{hr.Value:X8}");
            LogPackageIdentity();

            // 与 DesktopFlyouts 样例一致：在 Application 构造期间完成 XAML 初始化与消息循环
            _ = new App();

            _log.Flush();
            return 0;
        }

        internal static long StartupMs() => s_startupWatch.IsRunning ? (long)s_startupWatch.ElapsedMilliseconds : 0;

        private static string StartupTag() => $"[STARTUP] t={StartupMs()}ms";

        private static void ParseArgs(string[] args)
        {
            var mode = GetArgValue(args, "--mode");
            if (!string.IsNullOrEmpty(mode))
                _mode = mode!;

            var transparent = GetArgValue(args, "--transparent");
            if (!string.IsNullOrEmpty(transparent))
                _transparent = transparent == "1" || transparent.Equals("true", StringComparison.OrdinalIgnoreCase);

            // --fill work（默认，铺满 rcWork）| desktop（铺满 rcMonitor）| none（固定小窗口，供对照）
            var fill = GetArgValue(args, "--fill");
            if (!string.IsNullOrEmpty(fill))
            {
                _fillDesktop = fill.Equals("desktop", StringComparison.OrdinalIgnoreCase);
                _layoutFixed = fill.Equals("none", StringComparison.OrdinalIgnoreCase);
            }

            _selftest = args.Length > 0 && Array.IndexOf(args, "--selftest") >= 0;

            // --dir：文件视图页默认目录（不传时页内回退到用户目录）
            var dir = GetArgValue(args, "--dir");
            if (!string.IsNullOrWhiteSpace(dir))
                FileViewDefaultPath = dir!;

            // --memgc 1：内存测量专用，探针打点前强制 GC，避免把未回收垃圾算进“自持内存”
            MemProbe.ForceGc = GetArgValue(args, "--memgc") == "1";

            // --occ 0：性能/内存口径复测时关闭遮挡降级，避免桌面被 IDE/终端盖住时干扰枚举批次
            OcclusionEnabled = GetArgValue(args, "--occ") != "0";
            // --icons 0：只做诊断，跳过 Shell 缩略图解码
            IconsEnabled = GetArgValue(args, "--icons") != "0";
        }

        private static string? GetArgValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }

        internal static void Run()
        {
            try
            {
                Log($"mode={_mode} transparent={_transparent} selftest={_selftest}");

                var manager = WindowsXamlManager.InitializeForCurrentThread();
                Log($"WindowsXamlManager.InitializeForCurrentThread => {manager is not null}");

                // 与 DesktopFlyouts 样例一致，给 XAML 一个 DispatcherQueue 同步上下文
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));

                var window = Window.Current;
                Log($"Window.Current => {(window is null ? "NULL" : window.GetType().FullName)}");

                var coreWindow = CoreWindow.GetForCurrentThread();
                Log($"CoreWindow.GetForCurrentThread => {(coreWindow is null ? "NULL" : coreWindow.GetType().FullName)}");
                if (coreWindow is not null)
                    ResolveCoreWindowHwnd(coreWindow);

                TryLoadWinUI2Resources();

                CreateHostWindow();
                Log($"{StartupTag()} window created hidden hwnd=0x{Hx(_host)}");

                InitializeXamlIsland();
                Log($"{StartupTag()} island content set hwnd=0x{Hx(_xamlHwnd)}");

                // 岛一律先以透明态初始化。实测：在子窗嵌入 Progman 的模式下，
                // 启动阶段就把 IsBackgroundTransparent 置 false 会让整个岛不渲染；
                // 先把窗口显示出来、再写 false 则正常（与热键切换等价）。
                var transparencyOk = TrySetIsBackgroundTransparent(_transparent);
                Log($"{StartupTag()} transparency applied ok={transparencyOk} requested={_transparent}");
                if (!transparencyOk && _transparent)
                {
                    // 透明 API 不可用：保持可用，改为不透明深色底，避免内容不可读
                    _transparent = false;
                    _shell?.SetOpaqueFallback();
                    Log("[FALLBACK] transparency-unavailable => opaque island (window stays usable)");
                }

                Log($"host dpi={PInvoke.GetDpiForWindow(_host)} coreHwnd=0x{Hx(_coreHwnd)} xamlHwnd=0x{Hx(_xamlHwnd)}");

                LogDesktopStructure();

                if (_mode == "workerw" || _mode == "desktoppane")
                {
                    RegisterDemoHotkeys();
                    var embedOk = _mode == "workerw" ? ReparentToWorkerW() : ReparentToDesktopPane();
                    if (embedOk)
                    {
                        _desktopMode = true;
                        Log($"{StartupTag()} desktop embed ok mode={_mode}");
                    }
                    else
                    {
                        // 嵌入失败：退化为置顶窗口，保持功能可用（点击/透明/退出都不受影响）
                        FallbackToTopmost($"desktop-embed-failed[{_mode}]");
                    }

                    LogDesktopStructure();
                }
                else
                {
                    RegisterDemoHotkeys();
                }

                // 先隐藏完成布局（不要在这里 Show），后续由 RunStartupReveal 在首帧后显示
                ApplyLayout("startup", show: false);
                UpdateWorkspace("startup");
                Log($"{StartupTag()} layout done (host still hidden)");

                if (!_transparent)
                {
                    // 窗口显示后再写 false 才与热键切换路径一致（这里先记录意图，显示后执行）
                    Log("[XST] deferred opaque apply after show");
                }

                RunStartupReveal();

                if (!_transparent)
                    ApplyTransparency();

                StartOcclusionWatch();

                RunInteractiveLoop();
            }
            catch (Exception ex)
            {
                Log($"FATAL EXCEPTION: {ex}");
            }
            finally
            {
                Cleanup();
                _log.Flush();
            }
        }

        private static void LogPackageIdentity()
        {
            try
            {
                var id = Windows.ApplicationModel.Package.Current.Id;
                Log($"[PKG] Package.Current fullName={id.FullName} family={id.FamilyName}");
            }
            catch (Exception ex)
            {
                Log($"[PKG] no package identity: {ex.GetType().Name} hr=0x{ex.HResult:X8} msg={ex.Message}");
            }
        }

        private static void ResolveCoreWindowHwnd(CoreWindow coreWindow)
        {
            try
            {
                var ptr = ((IWinRTObject)coreWindow).NativeObject.ThisPtr;
                nint ppv;
                var iid = IID_ICoreWindowInterop;
                Marshal.QueryInterface(ptr, ref iid, out ppv);
                var cw = new StrategyBasedComWrappers();
                var interop = (ICoreWindowInterop)cw.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None);
                Marshal.Release(ppv);
                interop.get_WindowHandle((HWND*)Unsafe.AsPointer(ref _coreHwnd));
                Log($"[CoreWindowInterop] hwnd=0x{Hx(_coreHwnd)}");
            }
            catch (Exception ex)
            {
                Log($"[CoreWindowInterop] FAILED: {ex.Message}");
            }
        }

        private static void CreateHostWindow()
        {
            var className = "WinUI2DemoHost." + Guid.NewGuid().ToString("N");
            var hInstance = PInvoke.GetModuleHandle(default);

            WNDCLASSW wc = default;
            wc.lpfnWndProc = (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)(void*)Marshal.GetFunctionPointerForDelegate(s_wndProc);
            wc.hInstance = hInstance;
            wc.lpszClassName = S(className);

            var atom = PInvoke.RegisterClass(&wc);
            Log($"[RegisterClass] host atom={atom} lastErr={Marshal.GetLastWin32Error()}");

            var exStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW;
            if (_mode == "topmost")
                exStyle |= WS_EX_TOPMOST;

            _host = PInvoke.CreateWindowEx(
                (WINDOW_EX_STYLE)exStyle,
                S(className),
                S("WinUI2DemoHost"),
                (WINDOW_STYLE)WS_POPUP,
                HostX, HostY, HostW, HostH,
                HWND.Null, HMENU.Null, hInstance, null);
            Log($"[CreateWindow] host hwnd=0x{Hx(_host)} exStyle=0x{exStyle:X} lastErr={Marshal.GetLastWin32Error()}");
        }

        private static void InitializeXamlIsland()
        {
            _dws = new DesktopWindowXamlSource();
            Log($"[DWS] created={_dws is not null}");

            var ptr = ((IWinRTObject)_dws).NativeObject.ThisPtr;
            nint ppv;
            var iid = IID_IDesktopWindowXamlSourceNative2;
            Marshal.QueryInterface(ptr, ref iid, out ppv);
            var cw = new StrategyBasedComWrappers();
            _native2 = (IDesktopWindowXamlSourceNative2)cw.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None);
            Marshal.Release(ppv);

            var attachHr = _native2.AttachToWindow(_host);
            Log($"[DWS] AttachToWindow(host) => 0x{attachHr.Value:X8}");

            _native2.get_WindowHandle((HWND*)Unsafe.AsPointer(ref _xamlHwnd));
            Log($"[DWS] island hwnd=0x{Hx(_xamlHwnd)}");

            var content = BuildContent();
            if (content is null)
            {
                // 实验失败路径：不设置任何替代内容，保持岛为空，采样记录真实画面
                Log("[DWS] Content NOT set (WinUI 2 content creation failed)");
            }
            else
            {
                _dws.Content = content;
                Log("[DWS] Content set");
            }

            RECT rc;
            PInvoke.GetClientRect(_host, &rc);
            PInvoke.SetWindowPos(
                _xamlHwnd, HWND.Null, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
            Log($"[DWS] island sized to {rc.right - rc.left}x{rc.bottom - rc.top}");
        }

        private static void TryLoadWinUI2Resources()
        {
            try
            {
                var resources = new Microsoft.UI.Xaml.Controls.XamlControlsResources();
                var app = Application.Current;
                if (app is null)
                {
                    Log("[WINUI2] XamlControlsResources created but Application.Current is NULL; not merged");
                    return;
                }

                app.Resources.MergedDictionaries.Add(resources);
                Log("[WINUI2] XamlControlsResources merged into Application.Resources");

                // 诊断：记录 NavigationView 内容区背景资源，解释透明岛上的半透明白底来自何处
                foreach (var key in new[] { "NavigationViewContentBackground", "NavigationViewDefaultBackground", "ApplicationPageBackgroundThemeBrush" })
                {
                    try
                    {
                        var value = app.Resources[key];
                        var detail = value is SolidColorBrush brush
                            ? $"{brush.GetType().Name} color=#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2} opacity={brush.Opacity}"
                            : $"{value?.GetType().FullName}";
                        Log($"[WINUI2] resource {key} => {detail}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[WINUI2] resource {key} lookup failed: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WINUI2] XamlControlsResources FAILED: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg={ex.Message}");
                if (ex.InnerException is not null)
                    Log($"[WINUI2] inner: {ex.InnerException.GetType().FullName} hr=0x{ex.InnerException.HResult:X8} msg={ex.InnerException.Message}");
            }
        }

        private static UIElement? BuildContent()
        {
            try
            {
                var shell = new DemoShell();
                _shell = shell;
                _frame = shell.ContentFrame;
                AttachFrameLogging(_frame);
                // v1.0：日志挂好后再导航，确保 [NAV] 记录可用于启动时序核对
                shell.NavigateHome();
                // 首帧之前就让内容透明并下移：窗口显示时不可能画出未布局的白底
                shell.PrepareEntrance();
                Log($"[DEMO] DemoShell created, content={_frame.Content?.GetType().FullName ?? "NULL"} " +
                    $"backStack={_frame.BackStackDepth} canGoBack={_frame.CanGoBack} t={StartupMs()}ms");
                MemProbe.Log("idle", 0, 0, 0, 0);
                return shell;
            }
            catch (Exception ex)
            {
                Log($"[DEMO] DemoShell FAILED: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg={ex.Message}");
                if (ex.InnerException is not null)
                    Log($"[DEMO] inner: {ex.InnerException.GetType().FullName} hr=0x{ex.InnerException.HResult:X8} msg={ex.InnerException.Message}");
                return null;
            }
        }

        private static void AttachFrameLogging(Frame frame)
        {
            frame.Navigating += (_, e) =>
                Log($"[NAV] Navigating to {e.SourcePageType?.FullName} mode={e.NavigationMode} backStack={frame.BackStackDepth}");
            frame.Navigated += (_, e) =>
                Log($"[NAV] Navigated to {e.SourcePageType?.FullName} mode={e.NavigationMode} " +
                    $"content={frame.Content?.GetType().FullName ?? "NULL"} backStack={frame.BackStackDepth} canGoBack={frame.CanGoBack}");
            frame.NavigationFailed += (_, e) =>
                Log($"[NAV] NavigationFailed: {e.Exception?.GetType().FullName} hr=0x{e.Exception?.HResult:X8} msg={e.Exception?.Message}");
        }

        private static void RunInteractiveLoop()
        {
            var sw = Stopwatch.StartNew();
            long lastStatus = -1;
            long lastZOrder = 0;
            while (!_exitRequested)
            {
                Pump(16);
                HandleDemoRequests();

                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed - lastStatus >= 300)
                {
                    lastStatus = elapsed;
                    UpdateDemoStatus();
                }

                // explorer 偶尔会重排桌面子窗，定期把宿主重新顶到 DefView 之上
                if (elapsed - lastZOrder >= 1000)
                {
                    lastZOrder = elapsed;
                    EnsureDesktopPaneZOrder();
                }

                if (_selftest)
                    RunSelfTestStep(elapsed);

                // 防御性上限：避免无人值守时无限运行
                if (elapsed > 6 * 60 * 60 * 1000)
                    break;
            }

            Log("[DEMO] interactive loop exited");
        }

        private static void HandleDemoRequests()
        {
            if (DemoBridge.PendingTransparencyToggle)
            {
                DemoBridge.PendingTransparencyToggle = false;
                _transparent = !_transparent;
                TrySetIsBackgroundTransparent(_transparent);
                Log($"[DEMO] transparency toggled => {_transparent}");
            }

            if (DemoBridge.PendingModeToggle)
            {
                DemoBridge.PendingModeToggle = false;
                ToggleDesktopMode();
            }

            if (DemoBridge.PendingFillToggle)
            {
                DemoBridge.PendingFillToggle = false;
                _fillDesktop = !_fillDesktop;
                _layoutFixed = false;
                Log($"[DEMO] fill toggled => {(_fillDesktop ? "desktop" : "work")}");
                ApplyLayout("fill-toggle");
                UpdateWorkspace("fill-toggle");
            }

            if (DemoBridge.PendingClose)
            {
                DemoBridge.PendingClose = false;
                Log("[DEMO] close requested");
                _exitRequested = true;
            }
        }

        private static void UpdateDemoStatus()
        {
            var frame = _frame;
            var page = frame?.Content?.GetType().Name ?? "-";
            var stack = frame?.BackStackDepth ?? -1;
            var canBack = frame?.CanGoBack == true;
            string package;
            try
            {
                package = Windows.ApplicationModel.Package.Current.Id.FullName;
            }
            catch
            {
                package = "无包标识（直接 exe 启动，WinUI 2 资源不可用）";
            }

            var modeText = _desktoppane ? "嵌入桌面 (DefView 之上)" : _desktopMode ? "嵌入桌面 (WorkerW 壁纸层)" : "置顶浮层";
            var text =
                $"模式: {modeText}    透明: {(_transparent ? "开" : "关")}    IsBackgroundTransparent={_transparentReadback}\n" +
                $"页面: {page}    返回栈: {stack}    可返回: {(canBack ? "是" : "否")}    " +
                $"布局: {(_layoutFixed ? "固定窗口" : _fillDesktop ? "铺满桌面" : "铺满工作区")}\n" +
                $"包: {package}\n" +
                $"启动时序: window={StartupMs()}ms shellLoaded={ShellLoaded} frameTicks={_frameTicks}\n" +
                "快捷键: Ctrl+F2 透明 | Ctrl+F3 置顶/桌面 | Ctrl+F4 退出";
            _shell?.UpdateStatus(text, _transparent, _workspaceText);
        }

        private static void RegisterDemoHotkeys()
        {
            // 全局热键：即使窗口嵌进 WorkerW、没有焦点，也能切回/退出
            Log($"[HOTKEY] Ctrl+F2={(bool)RegisterHotKey(_host, HotkeyTransparency, ModControl | ModNoRepeat, VkF2)} " +
                $"Ctrl+F3={(bool)RegisterHotKey(_host, HotkeyMode, ModControl | ModNoRepeat, VkF3)} " +
                $"Ctrl+F4={(bool)RegisterHotKey(_host, HotkeyClose, ModControl | ModNoRepeat, VkF4)}");
        }

        private static void ToggleDesktopMode()
        {
            if (!_desktopMode)
            {
                if (ReparentToDesktopPane())
                {
                    _desktopMode = true;
                }
                else
                {
                    FallbackToTopmost("desktop-embed-failed[toggle]");
                }
            }
            else
            {
                long style = (long)PInvoke.GetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
                style = (style & ~(long)WS_CHILD) | WS_POPUP;
                PInvoke.SetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)style);

                long exStyle = (long)PInvoke.GetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                exStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                PInvoke.SetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

                var oldParent = PInvoke.SetParent(_host, HWND.Null);
                PInvoke.SetWindowPos(
                    _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW |
                    SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
                _desktopMode = false;
                Log($"[DEMO] mode => topmost oldParent=0x{Hx(oldParent)} style=0x{style:X} exStyle=0x{exStyle:X} lastErr={Marshal.GetLastWin32Error()}");
                LogHitTestProbe();
            }

            // 重新挂父后屏幕坐标基准可能变化（子窗坐标是父客户区坐标），重新套用工作区布局
            ApplyLayout("mode-toggle");
            UpdateWorkspace("mode-toggle");
        }

        /// <summary>
        /// 自动化冒烟测试（--selftest）：走与按钮/开关相同的桥接路径，跑完自动退出，用于打包验证。
        /// </summary>
        private static void RunSelfTestStep(long elapsed)
        {
            switch (_selftestStep)
            {
                case 0 when elapsed >= 1000:
                    Log("[SELFTEST] transparency off");
                    DemoBridge.ToggleTransparency();
                    _selftestStep = 1;
                    break;
                case 1 when elapsed >= 1800:
                    Log("[SELFTEST] transparency on");
                    DemoBridge.ToggleTransparency();
                    _selftestStep = 2;
                    break;
                case 2 when elapsed >= 2400:
                    Log("[SELFTEST] navigate PageB");
                    _frame?.Navigate(typeof(PageB), "selftest");
                    _selftestStep = 3;
                    break;
                case 3 when elapsed >= 3200:
                    Log("[SELFTEST] go back");
                    if (_frame?.CanGoBack == true)
                        _frame.GoBack();
                    _selftestStep = 4;
                    break;
                case 4 when elapsed >= 4000:
                    Log("[SELFTEST] mode -> desktoppane");
                    DemoBridge.ToggleMode();
                    _selftestStep = 5;
                    break;
                case 5 when elapsed >= 5000:
                    Log("[SELFTEST] mode -> topmost");
                    DemoBridge.ToggleMode();
                    _selftestStep = 6;
                    break;
                case 6 when elapsed >= 5800:
                    Log("[SELFTEST] fill -> desktop (rcMonitor)");
                    DemoBridge.ToggleFill();
                    _selftestStep = 7;
                    break;
                case 7 when elapsed >= 6500:
                    Log("[SELFTEST] fill -> work (rcWork)");
                    DemoBridge.ToggleFill();
                    _selftestStep = 8;
                    break;
                case 8 when elapsed >= 7200:
                    Log("[SELFTEST] close");
                    DemoBridge.Close();
                    _selftestStep = 9;
                    break;
            }
        }

        private static void ApplyTransparency()
        {
            if (!_transparent)
            {
                // 显式写 false，作为单变量对照
                TrySetIsBackgroundTransparent(false);
                return;
            }

            TrySetIsBackgroundTransparent(true);
        }

        private static bool TrySetIsBackgroundTransparent(bool value)
        {
            try
            {
                var window = Window.Current;
                if (window is null)
                {
                    Log("[XST] FAILED: Window.Current is NULL, cannot QI IXamlSourceTransparency");
                    return false;
                }

                var ptr = ((IWinRTObject)window).NativeObject.ThisPtr;
                nint ppv;
                var iid = IID_IWindowPrivate;
                Marshal.QueryInterface(ptr, ref iid, out ppv);
                var cw = new StrategyBasedComWrappers();
                var xst = (IXamlSourceTransparency)cw.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None);
                Marshal.Release(ppv);

                var setHr = xst.set_IsBackgroundTransparent(value);
                xst.get_IsBackgroundTransparent(out var readback);
                _transparentReadback = readback;
                Log($"[XST] set_IsBackgroundTransparent({value}) => hr=0x{setHr.Value:X8} readback={readback}");
                return setHr.Value >= 0 && readback == value;
            }
            catch (Exception ex)
            {
                Log($"[XST] FAILED to set IsBackgroundTransparent={value}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool ReparentToWorkerW()
        {
            _desktoppane = false;
            _workerW = FindWorkerW();
            if (_workerW.IsNull)
            {
                Log("[WORKERW] not found, keep top-level windows");
                return false;
            }

            // 本机 WorkerW 内部有壁纸合成子窗，HWND_BOTTOM 会被其遮住，放在 HWND_TOP
            ReparentWindow(_host, _workerW, HostX, HostY, HostW, HostH, bottom: false, show: false);

            PInvoke.SetWindowPos(
                _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

            var ok = (bool)PInvoke.IsChild(_workerW, _host);
            Log($"[WORKERW] parent=0x{Hx(_workerW)} hostIsChild={ok}");
            LogHitTestProbe();
            return ok;
        }

        /// <summary>
        /// 嵌入态输入的推荐路线：宿主仍挂在桌面窗口树里（Progman 的子窗），但 Z 序放到
        /// SHELLDLL_DefView 之上。DefView 是全屏视图且位于 WorkerW 之上（本机枚举证实），
        /// 挂在 WorkerW 里的窗口命中测试永远落到 DefView，鼠标消息到不了宿主；
        /// 放到 DefView 之上后点击真实命中宿主，而透明区域下方仍是图标与壁纸（视觉不变）。
        /// </summary>
        private static bool ReparentToDesktopPane()
        {
            var progman = PInvoke.FindWindow(S("Progman"), default);
            if (progman.IsNull)
            {
                Log("[DESKTOPPANE] Progman not found, stay topmost");
                return false;
            }

            var defView = PInvoke.FindWindowEx(progman, HWND.Null, S("SHELLDLL_DefView"), default);

            ReparentWindow(_host, progman, HostX, HostY, HostW, HostH, bottom: false, show: false);

            // Progman 子窗 Z 序最前 = 高于 SHELLDLL_DefView；仍受 Progman 约束，不会盖到普通应用
            PInvoke.SetWindowPos(
                _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

            var ok = (bool)PInvoke.IsChild(progman, _host);
            _desktoppane = ok;
            Log($"[DESKTOPPANE] parent=0x{Hx(progman)} defView=0x{Hx(defView)} hostIsChild={ok}");
            LogHitTestProbe();
            return ok;
        }

        /// <summary>桌面结构诊断：Progman 子窗按 Z 序列出，解释点击命中谁。</summary>
        private static void LogDesktopStructure()
        {
            var progman = PInvoke.FindWindow(S("Progman"), default);
            Log($"[DESK] Progman=0x{Hx(progman)}");
            if (progman.IsNull)
                return;

            var parts = new StringBuilder();
            HWND child = PInvoke.FindWindowEx(progman, HWND.Null, default, default);
            while (!child.IsNull)
            {
                RECT r = default;
                PInvoke.GetWindowRect(child, &r);
                parts.Append($"{ClassNameOf(child)}(0x{Hx(child)},visible={(bool)PInvoke.IsWindowVisible(child)},rect={r.left},{r.top},{r.right},{r.bottom}) -> ");
                child = PInvoke.FindWindowEx(progman, child, default, default);
            }

            Log($"[DESK] Progman children in Z order: {parts}");
        }

        /// <summary>命中测试诊断：这些坐标上的点击会被谁接收。</summary>
        private static void LogHitTestProbe()
        {
            if (_host.IsNull)
                return;

            RECT r = default;
            if (!PInvoke.GetWindowRect(_host, &r) || r.right <= r.left || r.bottom <= r.top)
                return;

            var probes = new (int X, int Y)[]
            {
                ((r.left + r.right) / 2, (r.top + r.bottom) / 2),
                (r.left + 140, r.top + 200),
                (r.right - 140, r.top + 200),
            };

            foreach (var (x, y) in probes)
            {
                var hit = WindowFromPoint(new POINT32 { X = x, Y = y });
                var hitHwnd = new HWND(hit);
                var ours = hit == (nint)_host.Value || hit == (nint)_xamlHwnd.Value;
                Log($"[HITTEST] ({x},{y}) => {ClassNameOf(hitHwnd)} 0x{Hx(hitHwnd)} ours={ours} " +
                    $"root=0x{Hx(PInvoke.GetAncestor(hitHwnd, GET_ANCESTOR_FLAGS.GA_ROOT))}");
            }
        }

        private static string ClassNameOf(HWND hwnd)
        {
            if (hwnd.IsNull)
                return "NULL";

            Span<char> buf = stackalloc char[128];
            fixed (char* p = buf)
            {
                var n = PInvoke.GetClassName(hwnd, (PWSTR)p, buf.Length);
                return n > 0 ? new string(buf[..n]) : "?";
            }
        }

        // ---------------- 工作区（显示器 / rcMonitor / rcWork） ----------------

        /// <summary>枚举显示器并选定宿主所在显示器（MonitorFromWindow + GetMonitorInfo）。</summary>
        private static void EnsureMonitors()
        {
            s_monitors.Clear();
            var fp = Marshal.GetFunctionPointerForDelegate(s_monitorEnum);
            var ok = EnumDisplayMonitors(nint.Zero, nint.Zero, fp, nint.Zero);
            var hm = _host.IsNull ? nint.Zero : MonitorFromWindow((nint)_host.Value, MONITOR_DEFAULTTONEAREST);

            s_currentMonitor = null;
            foreach (var m in s_monitors)
            {
                if (m.Handle == hm)
                {
                    s_currentMonitor = m;
                    break;
                }
            }

            if (s_currentMonitor is null)
                s_currentMonitor = FindPrimaryMonitor();
            if (s_currentMonitor is null && s_monitors.Count > 0)
                s_currentMonitor = s_monitors[0];

            Log($"[WS] enum monitors ok={ok} count={s_monitors.Count} SM_CMONITORS={GetSystemMetrics(SM_CMONITORS)} " +
                $"MonitorFromWindow(0x{Hx(_host)},NEAREST)={HexPtr(hm)}");
        }

        private static bool OnMonitorEnum(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData)
        {
            try
            {
                var info = QueryMonitor(hMonitor);
                if (info is not null)
                    s_monitors.Add(info);
            }
            catch
            {
                // 回调里抛异常会穿过 user32 栈，直接吞掉
            }

            return true;
        }

        private static MonitorInfo? QueryMonitor(nint hMonitor)
        {
            var info = default(MONITORINFOEXW);
            info.cbSize = sizeof(MONITORINFOEXW);
            if (!GetMonitorInfo(hMonitor, (nint)(void*)&info))
                return null;

            uint dpiX = 96, dpiY = 96;
            uint dx = 0, dy = 0;
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, (nint)(void*)&dx, (nint)(void*)&dy) >= 0)
            {
                dpiX = dx;
                dpiY = dy;
            }

            var mi = new MonitorInfo
            {
                Handle = hMonitor,
                Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Monitor = info.rcMonitor,
                Work = info.rcWork,
                DpiX = dpiX,
                DpiY = dpiY,
            };

            // 本地结构体上的固定缓冲区已固定在栈上，可直接取指针（不能再用 fixed 语句）
            char* p = info.szDevice;
            var len = 0;
            while (len < 32 && p[len] != '\0')
                len++;
            mi.Device = len > 0 ? new string(p, 0, len) : "?";

            return mi;
        }

        private static MonitorInfo? FindPrimaryMonitor()
        {
            foreach (var m in s_monitors)
            {
                if (m.Primary)
                    return m;
            }

            return null;
        }

        /// <summary>
        /// 按工作区（rcWork）或整块桌面（rcMonitor）套用宿主矩形。
        /// 嵌入桌面时宿主是 Progman 的子窗，SetWindowPos 的 x/y 是父客户区坐标，
        /// 所以写入后再用 GetWindowRect 复核，按差值校正一次（不假设父窗原点 = 屏幕原点）。
        /// </summary>
        private static void ApplyLayout(string reason, bool show = true)
        {
            if (_layoutFixed || _host.IsNull)
            {
                Log($"[WS] layout({reason}) skipped (fill=none or host null)");
                return;
            }

            EnsureMonitors();
            var m = s_currentMonitor;
            if (m is null)
            {
                Log($"[WS] layout({reason}) skipped: no monitor info");
                return;
            }

            var target = _fillDesktop ? m.Monitor : m.Work;
            var w = target.Right - target.Left;
            var h = target.Bottom - target.Top;

            var showFlag = show ? SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW : (SET_WINDOW_POS_FLAGS)0;
            PInvoke.SetWindowPos(_host, InsertAfterTop, target.Left, target.Top, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | showFlag);

            var actual = HostRect();
            var dx = target.Left - actual.Left;
            var dy = target.Top - actual.Top;
            if (dx != 0 || dy != 0)
            {
                PInvoke.SetWindowPos(_host, InsertAfterTop, target.Left + dx, target.Top + dy, w, h,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | showFlag);
                actual = HostRect();
            }

            var parent = PInvoke.GetAncestor(_host, GET_ANCESTOR_FLAGS.GA_PARENT);
            Log($"[WS] layout({reason}) basis={(_fillDesktop ? "rcMonitor" : "rcWork")} show={show} " +
                $"target={Rect(target)} actual={Rect(actual)} parent=0x{Hx(parent)} " +
                $"parentOffsetCorrection=({dx},{dy}) lastErr={Marshal.GetLastWin32Error()}");

            LogRects();
        }

        /// <summary>
        /// 采集并输出工作区信息：每块显示器的 rcMonitor/rcWork（像素 + DIP）、
        /// SPI_GETWORKAREA 对照、任务栏位置、本次布局判定，以及“实际可见范围”的命中/像素证据。
        /// </summary>
        private static void UpdateWorkspace(string reason)
        {
            EnsureMonitors();

            for (var i = 0; i < s_monitors.Count; i++)
            {
                var mi = s_monitors[i];
                Log($"[WS] monitor[{i + 1}/{s_monitors.Count}] hm={HexPtr(mi.Handle)} device={mi.Device} " +
                    $"primary={mi.Primary} dpi={mi.DpiX}x{mi.DpiY} " +
                    $"rcMonitor={Rect(mi.Monitor)}px rcWork={Rect(mi.Work)}px | " +
                    $"dipMonitor={DipRect(mi.Monitor, mi.DpiX)} dipWork={DipRect(mi.Work, mi.DpiX)}");
            }

            RECT32 sysWork = default;
            s_sysWorkOk = SystemParametersInfo(SPI_GETWORKAREA, 0, (nint)(void*)&sysWork, 0);
            s_sysWork = sysWork;
            var cur = s_currentMonitor;
            var primary = FindPrimaryMonitor();
            Log($"[WS] SPI_GETWORKAREA ok={s_sysWorkOk} rect={Rect(s_sysWork)}px " +
                $"dip={(cur is null ? "-" : DipRect(s_sysWork, cur.DpiX))} " +
                $"equalCurrentRcWork={(cur is not null && SameRect(s_sysWork, cur.Work))} " +
                $"equalPrimaryRcWork={(primary is not null && SameRect(s_sysWork, primary.Work))}");

            var abd = default(APPBARDATA);
            abd.cbSize = (uint)sizeof(APPBARDATA);
            var abmOk = SHAppBarMessage(ABM_GETTASKBARPOS, (nint)(void*)&abd) != 0;
            s_taskbarEdge = abd.uEdge;
            s_taskbarRect = abd.rc;
            Log($"[WS] taskbar ABM_GETTASKBARPOS ok={abmOk} edge={EdgeName(abd.uEdge)}({abd.uEdge}) " +
                $"rect={Rect(abd.rc)}px");

            if (cur is not null)
            {
                Log($"[WS] insets(work vs monitor) px: left={cur.Work.Left - cur.Monitor.Left} " +
                    $"top={cur.Work.Top - cur.Monitor.Top} right={cur.Monitor.Right - cur.Work.Right} " +
                    $"bottom={cur.Monitor.Bottom - cur.Work.Bottom} " +
                    $"dipBottom={Dip(cur.Monitor.Bottom - cur.Work.Bottom, cur.DpiX)}");
            }

            LogLayoutVerdict(reason);
            _workspaceText = BuildWorkspaceText();
            Log($"[WS] text => {_workspaceText.Replace("\n", " | ")}");
        }

        /// <summary>布局判定 + 可见范围证据（命中测试与真实像素，不只报窗口矩形）。</summary>
        private static void LogLayoutVerdict(string reason)
        {
            var m = s_currentMonitor;
            if (m is null || _host.IsNull)
                return;

            RECT32 host = HostRect();
            var kind = SameRect(host, m.Monitor) ? "铺满整个桌面(rcMonitor)"
                     : SameRect(host, m.Work) ? "铺满工作区(rcWork)"
                     : "自定义";
            var overlap = Math.Max(0, host.Bottom - m.Work.Bottom);
            Log($"[WS] verdict({reason}) host={Rect(host)}px layout={kind} " +
                $"bottomOverWork={overlap}px({Dip(overlap, m.DpiX)}dip) desktopMode={_desktopMode}");

            var cx = (host.Left + host.Right) / 2;
            ProbeHit("workBottom-10", cx, m.Work.Bottom - 10);
            ProbeHit("workBottom+10", cx, m.Work.Bottom + 10);
        }

        private static void ProbeHit(string label, int x, int y)
        {
            var hit = WindowFromPoint(new POINT32 { X = x, Y = y });
            var hwnd = new HWND(hit);
            var root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
            var ours = hit == (nint)_host.Value || hit == (nint)_xamlHwnd.Value;
            Log($"[WS] hit({label}) ({x},{y}) => {ClassNameOf(hwnd)} 0x{Hx(hwnd)} ours={ours} root=0x{Hx(root)}");
        }

        private static string BuildWorkspaceText()
        {
            var m = s_currentMonitor;
            if (m is null)
                return "工作区: 无显示器信息";

            RECT32 host = HostRect();

            var kind = SameRect(host, m.Monitor) ? "铺满整个桌面"
                     : SameRect(host, m.Work) ? "铺满工作区"
                     : "自定义（固定窗口）";
            string visible;
            if (_desktopMode)
            {
                visible = "嵌入态：宿主在桌面窗口树内，任务栏/普通应用都在其上方 → 可见范围≈工作区";
            }
            else
            {
                // 置顶态与任务栏同为 topmost，谁在上取决于 Z 序；用真实命中给出本次结果
                var probeY = Math.Min(m.Monitor.Bottom - 4, m.Work.Bottom + 4);
                nint hit = WindowFromPoint(new POINT32 { X = (m.Work.Left + m.Work.Right) / 2, Y = probeY });
                var ours = hit == (nint)_host.Value || hit == (nint)_xamlHwnd.Value;
                visible = ours
                    ? "置顶态：宿主为 topmost，任务栏区域命中宿主 → 可见范围=宿主矩形"
                    : "置顶态：宿主为 topmost，但任务栏仍在最上层 → 可见范围≈工作区";
            }

            return
                $"显示器: {m.Device}（{(m.Primary ? "主屏" : "副屏")}）" +
                $"{RWidth(m.Monitor)}x{RHeight(m.Monitor)}px @{m.DpiX}DPI → " +
                $"{Dip(RWidth(m.Monitor), m.DpiX)}x{Dip(RHeight(m.Monitor), m.DpiX)}DIP\n" +
                $"rcMonitor={Rect(m.Monitor)}px{ToDip(m.Monitor, m.DpiX)} rcWork={Rect(m.Work)}px{ToDip(m.Work, m.DpiX)}\n" +
                $"SPI_GETWORKAREA={Rect(s_sysWork)}px ok={s_sysWorkOk} 与当前屏rcWork一致={SameRect(s_sysWork, m.Work)} " +
                $"任务栏边={EdgeName(s_taskbarEdge)} rect={Rect(s_taskbarRect)}px\n" +
                $"本次布局: {kind}（host={Rect(host)}px）；{visible}";
        }

        private static string ToDip(RECT32 r, uint dpi) =>
            $" / {(int)Math.Round(r.Left * 96.0 / SafeDpi(dpi))},{(int)Math.Round(r.Top * 96.0 / SafeDpi(dpi))}," +
            $"{(int)Math.Round(r.Right * 96.0 / SafeDpi(dpi))},{(int)Math.Round(r.Bottom * 96.0 / SafeDpi(dpi))}DIP";

        private static string DipRect(RECT32 r, uint dpi) => $"({ToDipInner(r, dpi)})";

        private static string ToDipInner(RECT32 r, uint dpi)
        {
            var k = 96.0 / SafeDpi(dpi);
            return $"{(int)Math.Round(r.Left * k)},{(int)Math.Round(r.Top * k)}," +
                   $"{(int)Math.Round(r.Right * k)},{(int)Math.Round(r.Bottom * k)}";
        }

        private static int Dip(int px, uint dpi) => (int)Math.Round(px * 96.0 / SafeDpi(dpi));

        private static uint SafeDpi(uint dpi) => dpi == 0 ? 96 : dpi;

        private static string Rect(RECT32 r) => $"({r.Left},{r.Top},{r.Right},{r.Bottom})";

        /// <summary>宿主窗口的屏幕矩形（GetWindowRect 走 CsWin32 的 RECT，这里转成本文件用的 RECT32）。</summary>
        private static RECT32 HostRect()
        {
            RECT r = default;
            if (!_host.IsNull)
                PInvoke.GetWindowRect(_host, &r);
            return new RECT32 { Left = r.left, Top = r.top, Right = r.right, Bottom = r.bottom };
        }

        private static int RWidth(RECT32 r) => r.Right - r.Left;

        private static int RHeight(RECT32 r) => r.Bottom - r.Top;

        private static bool SameRect(RECT32 a, RECT32 b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        private static string EdgeName(uint edge) => edge switch
        {
            0 => "左",
            1 => "上",
            2 => "右",
            3 => "下",
            _ => "未知",
        };

        private static string HexPtr(nint value) => value == 0 ? "0" : ((long)value).ToString("X");

        /// <summary>explorer 重新排列桌面子窗时把宿主重新顶到 DefView 之上。</summary>
        private static void EnsureDesktopPaneZOrder()
        {
            if (!_desktopMode || !_desktoppane || _host.IsNull)
                return;

            PInvoke.SetWindowPos(
                _host, InsertAfterTop, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }

        private static HWND FindWorkerW()
        {
            var progman = PInvoke.FindWindow(S("Progman"), default);
            Log($"[WORKERW] Progman=0x{Hx(progman)}");

            if (!progman.IsNull)
            {
                var result = PInvoke.SendMessage(progman, WM_SPAWN_WORKERW, default, default);
                Log($"[WORKERW] SendMessage(Progman, 0x052C, 0, 0) => 0x{result.Value:X}");
            }

            // 本机（Win11 26300）真实结构：Progman 的子窗口 WorkerW 才是壁纸宿主
            HWND candidate = HWND.Null;
            if (!progman.IsNull)
            {
                candidate = PInvoke.FindWindowEx(progman, HWND.Null, S("WorkerW"), default);
                Log($"[WORKERW] Progman child WorkerW=0x{Hx(candidate)} valid={(bool)PInvoke.IsWindow(candidate)} visible={(bool)PInvoke.IsWindowVisible(candidate)}");
            }

            // 兼容 Win10 经典结构：顶层 WorkerW（无 SHELLDLL_DefView 且可见）
            if (candidate.IsNull)
            {
                HWND current = HWND.Null;
                while (true)
                {
                    current = PInvoke.FindWindowEx(HWND.Null, current, S("WorkerW"), default);
                    if (current.IsNull)
                        break;

                    var defView = PInvoke.FindWindowEx(current, HWND.Null, S("SHELLDLL_DefView"), default);
                    var visible = (bool)PInvoke.IsWindowVisible(current);
                    Log($"[WORKERW-CANDIDATE] hwnd=0x{Hx(current)} hasShellDefView={!defView.IsNull} visible={visible}");

                    if (visible && defView.IsNull && candidate.IsNull)
                        candidate = current;
                }
            }

            if (candidate.IsNull)
            {
                Log("[WORKERW] no usable WorkerW; fallback to Progman");
                candidate = progman;
            }

            return candidate;
        }

        private static void ReparentWindow(HWND hwnd, HWND parent, int x, int y, int w, int h,
            bool bottom, bool show = true)
        {
            long style = (long)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            style = (style & ~(long)WS_POPUP) | WS_CHILD;
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)style);

            long exStyle = (long)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            exStyle &= ~(long)WS_EX_TOPMOST;
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

            var oldParent = PInvoke.SetParent(hwnd, parent);
            Log($"[REPARENT] hwnd=0x{Hx(hwnd)} oldParent=0x{Hx(oldParent)} newParent=0x{Hx(parent)} style=0x{style:X} exStyle=0x{exStyle:X} lastErr={Marshal.GetLastWin32Error()}");

            PInvoke.SetWindowPos(
                hwnd,
                bottom ? InsertAfterBottom : InsertAfterTop,
                x, y, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                (show ? SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW : (SET_WINDOW_POS_FLAGS)0) |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }

        /// <summary>
        /// 遮挡监测：600 ms 轮询。判定依据（写明口径，避免“看起来被盖住”的推断）：
        /// 1) 宿主本身不可见/最小化 → occluded；
        /// 2) 取宿主顶层祖先，按 Z 序向上（GW_HWNDPREV）遍历所有更高层窗口，
        ///    任一可见窗口与宿主矩形交集 ≥ 宿主面积 95% → occluded；
        /// 3) 只做矩形覆盖，不做逐像素/合成层（DWM cloaked）分析；多个窗口拼合覆盖不合并计算。
        /// </summary>
        private static void StartOcclusionWatch()
        {
            if (!OcclusionEnabled)
            {
                Log("[OCC] disabled by --occ 0");
                return;
            }

            if (s_occTimer is not null)
                return;
            s_occTimer = new System.Threading.Timer(_ => PollOcclusion(), null, 600, 600);
            Log("[OCC] watch started intervalMs=600 rule=anyWindowAboveCoverHost95%");
        }

        private static void PollOcclusion()
        {
            if (Interlocked.Exchange(ref s_occBusy, 1) == 1)
                return;

            try
            {
                var occluded = ComputeOccluded(out var reason);
                if (s_occState is null || s_occState.Value != occluded)
                {
                    s_occState = occluded;
                    var fg = (nint)GetForegroundWindow();
                    Log($"[OCC] occluded={(occluded ? 1 : 0)} reason={reason} fg=0x{fg:X} fgClass={ClassNameOf(fg)}");
                    OcclusionChanged?.Invoke(occluded, reason);
                }
            }
            catch (Exception ex)
            {
                Log($"[OCC] poll failed {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref s_occBusy, 0);
            }
        }

        private static bool ComputeOccluded(out string reason)
        {
            reason = "host-missing";
            if (_host.IsNull)
                return false;

            var host = (nint)_host.Value;
            var root = GetAncestor(host, GA_ROOT);
            if (root == 0)
                root = host;

            if (!IsWindowVisible(root))
            {
                reason = "host-hidden";
                return true;
            }

            if (IsIconic(root))
            {
                reason = "host-minimized";
                return true;
            }

            RECT32 hostRect;
            if (!GetWindowRect(host, (nint)(&hostRect)))
            {
                reason = "host-rect-failed";
                return false;
            }

            var hostArea = (long)(hostRect.Right - hostRect.Left) * (hostRect.Bottom - hostRect.Top);
            if (hostArea <= 0)
            {
                reason = "host-area-zero";
                return false;
            }

            // 前景窗口单独判一次：TopMost 窗口和普通窗口处在不同 Z 序带，
            // GW_HWNDPREV 只会在同一带内游走，TopMost 的覆盖窗必须直接从前景句柄看。
            var fg = GetForegroundWindow();
            if (fg != 0 && fg != host && IsWindowVisible(fg) && !IsShellDesktopClass(fg))
            {
                RECT32 fgRect;
                if (GetWindowRect(fg, (nint)(&fgRect)))
                {
                    var fw = Math.Min(fgRect.Right, hostRect.Right) - Math.Max(fgRect.Left, hostRect.Left);
                    var fh = Math.Min(fgRect.Bottom, hostRect.Bottom) - Math.Max(fgRect.Top, hostRect.Top);
                    if (fw > 0 && fh > 0)
                    {
                        var fgCovered = (long)fw * fh;
                        if (fgCovered >= hostArea * 95 / 100)
                        {
                            reason = $"foreground-covers[{ClassNameOf(fg)}] cover={fgCovered * 100 / hostArea}%";
                            return true;
                        }
                    }
                }
            }

            var w = GetWindow(root, GW_HWNDPREV);
            var probes = 0;
            while (w != 0 && probes++ < 64)
            {
                if (IsWindowVisible(w) && !IsShellDesktopClass(w))
                {
                    RECT32 r;
                    if (GetWindowRect(w, (nint)(&r)))
                    {
                        var iw = Math.Min(r.Right, hostRect.Right) - Math.Max(r.Left, hostRect.Left);
                        var ih = Math.Min(r.Bottom, hostRect.Bottom) - Math.Max(r.Top, hostRect.Top);
                        if (iw > 0 && ih > 0)
                        {
                            var covered = (long)iw * ih;
                            if (covered >= hostArea * 95 / 100)
                            {
                                reason = $"covered-by[{ClassNameOf(w)}] cover={covered * 100 / hostArea}%";
                                return true;
                            }
                        }
                    }
                }

                w = GetWindow(w, GW_HWNDPREV);
            }

            reason = "clear";
            return false;
        }

        private static bool IsShellDesktopClass(nint hwnd)
        {
            var cls = ClassNameOf(hwnd);
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        }

        private static unsafe string ClassNameOf(nint hwnd)
        {
            if (hwnd == 0)
                return "";

            var buf = stackalloc char[128];
            var n = GetClassName(hwnd, (nint)buf, 128);
            if (n <= 0)
                return "";
            return new string(buf, 0, n);
        }

        /// <summary>
        /// ④ 的收尾动作：先强制 GC，再 EmptyWorkingSet。
        /// 注意：EmptyWorkingSet 只把页面移出工作集，private bytes 基本不变——不能把 WS 下降说成“省了内存”。
        /// </summary>
        internal static void TrimWorkingSet(string tag)
        {
            var p = Process.GetCurrentProcess();
            p.Refresh();
            var wsBefore = p.WorkingSet64;
            var priv = p.PrivateMemorySize64;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            p.Refresh();
            var wsAfterGc = p.WorkingSet64;
            var ok = EmptyWorkingSet(p.Handle);
            p.Refresh();

            Log($"[MEM] tag={tag} wsBefore={wsBefore / 1048576.0:F1}MB wsAfterGc={wsAfterGc / 1048576.0:F1}MB " +
                $"wsAfterTrim={p.WorkingSet64 / 1048576.0:F1}MB private={priv / 1048576.0:F1}MB " +
                $"managed={GC.GetTotalMemory(false) / 1048576.0:F2}MB emptyOk={ok} note=EmptyWorkingSet只降WS");
        }

        /// <summary>
        /// 启动显示时序：
        /// 1) 窗口以隐藏状态创建，内容和布局都在不可见状态下完成（DemoShell 已 PrepareEntrance：Opacity=0 + 下移 10）；
        /// 2) 等一次 CompositionTarget.Rendering（隐藏态可能不触发，只作信息记录，最多 250 ms）；
        /// 3) 显示宿主（内容仍为 Opacity=0，透明背景 → 即使这一帧上屏也只见桌面，不可能出现白底/空布局）；
        /// 4) 再等一次 Rendering（此时窗口已可见、合成器一定会排帧），DwmFlush 等待 DWM 合成完成；
        /// 5) 播放淡入 + 上移动画。
        /// 判据可靠性：Rendering 回调发生在该帧提交之前，所以我把它当作“即将提交”的信号，
        /// 再用 DwmFlush 作为“已合成”的确认；超时则记录并降级为直接播动画（内容从 Opacity=0 开始，仍不会闪白）。
        /// </summary>
        private static void RunStartupReveal()
        {
            StartupRevealInProgress = true;
            var revealStartTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnStartupRendering;

            var hiddenTick = WaitForFrameTick(StartupHiddenFrameWaitMs);
            Log($"{StartupTag()} hiddenFrameTick={hiddenTick} ticks={_frameTicks} shellLoaded={ShellLoaded}");

            // 显示宿主；内容在 PrepareEntrance 后仍是 Opacity=0，透明岛不会画出未布局的空白
            ShowWindows();
            Log($"{StartupTag()} host shown contentOpacity=0");

            // 抬起 5% 幽灵帧：强制一次真正带内容的绘制，避免动画时钟启动后才发现首次绘制很贵。
            // 只等 1 个 tick 不够：Rendering 回调只是“开始处理这帧”，100k 网格的首次光栅化会继续占用
            // UI 线程（实测约 800ms）；等到第 2 个 tick 才算这帧真正走完，再启动动画。
            _shell?.KickFirstPaint();
            var shownTick = WaitForFrameTicks(2, StartupShownFrameWaitMs);
            CompositionTarget.Rendering -= OnStartupRendering;

            if (shownTick)
            {
                var flushHr = DwmFlush();
                Log($"{StartupTag()} firstFrame committed ticks={_frameTicks} shellLoaded={ShellLoaded} " +
                    $"dwmFlushHr=0x{flushHr:X8}");
            }
            else
            {
                Log($"[FALLBACK] first-frame-timeout waitedMs={StartupShownFrameWaitMs} " +
                    $"ticks={_frameTicks} shellLoaded={ShellLoaded} => animate anyway");
            }

            if (!ShellLoaded)
                Log("[STARTUP] WARN shell Loaded 事件未在首帧前观测到（Loaded 可能延迟到下一次布局）");

            var gateMs = (long)((Stopwatch.GetTimestamp() - revealStartTicks) * 1000.0 / Stopwatch.Frequency);
            Log($"{StartupTag()} revealGateMs={gateMs} (hiddenWait+show+kickPaint+firstFrame+DwmFlush)");
            _shell?.PlayEntrance();
            StartupRevealInProgress = false;
        }

        internal static void OnEntranceComplete()
        {
            StartupRevealInProgress = false;
        }

        private static void OnStartupRendering(object? sender, object e)
        {
            _frameTicks++;
            if (!_firstRenderingTickLogged)
            {
                _firstRenderingTickLogged = true;
                Log($"{StartupTag()} Rendering tick#1 (window hidden/shown pending) shellLoaded={ShellLoaded}");
            }
        }

        /// <summary>在当前线程泵消息，直到出现新的 Rendering tick 或超时。</summary>
        private static bool WaitForFrameTick(int timeoutMs) => WaitForFrameTicks(1, timeoutMs);

        /// <summary>等待至少 count 个新的 Rendering tick（用于确认上一次首帧绘制已走完）。</summary>
        private static bool WaitForFrameTicks(int count, int timeoutMs)
        {
            var baseline = _frameTicks;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Pump(2);
                if (_frameTicks >= baseline + count)
                    return true;
            }

            return false;
        }

        private static void ShowWindows()
        {
            // 布局已经在隐藏状态完成，这里只做显示，不再重置位置
            PInvoke.ShowWindow(_host, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            if (!_xamlHwnd.IsNull)
                PInvoke.ShowWindow(_xamlHwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

            Log($"[SHOW] hostVisible={(bool)PInvoke.IsWindowVisible(_host)} xamlVisible={(bool)PInvoke.IsWindowVisible(_xamlHwnd)}");
            LogRects();
        }

        /// <summary>嵌入失败兜底：恢复为置顶浮层（WS_POPUP + WS_EX_TOPMOST），按工作区定位，保持可用。</summary>
        private static void FallbackToTopmost(string reason)
        {
            Log($"{StartupTag()} [FALLBACK] {reason} => topmost window");

            long style = (long)PInvoke.GetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            style = (style & ~(long)WS_CHILD) | WS_POPUP;
            PInvoke.SetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)style);

            long exStyle = (long)PInvoke.GetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            exStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            PInvoke.SetWindowLongPtr(_host, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

            var oldParent = PInvoke.SetParent(_host, HWND.Null);
            _desktopMode = false;
            _desktoppane = false;

            PInvoke.SetWindowPos(
                _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            Log($"[FALLBACK] topmost restored oldParent=0x{Hx(oldParent)} style=0x{style:X} exStyle=0x{exStyle:X}");
        }

        private static void LogRects()
        {
            RECT r;
            if (!_host.IsNull && PInvoke.GetWindowRect(_host, &r))
                Log($"[RECT] host screen=({r.left},{r.top},{r.right},{r.bottom})");
            if (!_xamlHwnd.IsNull && PInvoke.GetWindowRect(_xamlHwnd, &r))
            {
                var root = PInvoke.GetAncestor(_xamlHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
                Log($"[RECT] island screen=({r.left},{r.top},{r.right},{r.bottom}) root=0x{Hx(root)}");
            }
        }

        private static void Pump(int milliseconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < milliseconds)
            {
                MSG msg;
                while (PInvoke.PeekMessage(&msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    if (msg.message == WM_QUIT)
                        return;

                    LogInputMessage(&msg);

                    BOOL handled = false;
                    if (_native2 is not null)
                    {
                        try
                        {
                            _native2.PreTranslateMessage((MSG*)Unsafe.AsPointer(ref msg), &handled);
                        }
                        catch
                        {
                            // 预翻译失败不应阻塞消息循环
                        }
                    }

                    if (!handled)
                    {
                        PInvoke.TranslateMessage(&msg);
                        PInvoke.DispatchMessage(&msg);
                    }
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// 输入诊断：记录消息队列里收到的鼠标/激活消息。用来区分
        /// “消息根本没到线程”（被桌面 DefView 挡住）与“到了但 XAML 没反应”。
        /// </summary>
        private static void LogInputMessage(MSG* msg)
        {
            uint id = msg->message;
            if (id != WM_LBUTTONDOWN && id != WM_LBUTTONUP && id != WM_MOUSEACTIVATE &&
                id != WM_NCHITTEST && id != WM_ACTIVATE && id != WM_SETFOCUS && id != WM_SETCURSOR &&
                id != WM_POINTERDOWN && id != WM_POINTERUP)
                return;

            Log($"[INPUT] msg=0x{id:X4} hwnd=0x{Hx(msg->hwnd)} lParam=0x{(long)msg->lParam.Value:X}");
        }

        private static LRESULT WndProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam)
        {
            switch (uMsg)
            {
                case WM_SIZE:
                    if (!_xamlHwnd.IsNull)
                    {
                        RECT rc;
                        PInvoke.GetClientRect(hWnd, &rc);
                        PInvoke.SetWindowPos(
                            _xamlHwnd, HWND.Null, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
                            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW |
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                            SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
                    }

                    // 复刻 DesktopFlyouts：尺寸变化同步给 XAML 的 CoreWindow
                    if (!_coreHwnd.IsNull)
                        PInvoke.SendMessage(_coreHwnd, WM_SIZE, wParam, lParam);
                    break;

                case WM_MOUSEACTIVATE:
                    // 子窗口的 WM_MOUSEACTIVATE 默认会交给父窗口（Progman/explorer）裁决，
                    // 桌面父窗通常不希望激活任何东西。这里自己返回 MA_ACTIVATE，保证点击后激活。
                    Log($"[INPUT] WM_MOUSEACTIVATE on host hwnd=0x{Hx(hWnd)} => MA_ACTIVATE");
                    return (LRESULT)MA_ACTIVATE;

                case WM_HOTKEY:
                    switch ((int)wParam.Value)
                    {
                        case HotkeyTransparency:
                            DemoBridge.ToggleTransparency();
                            break;
                        case HotkeyMode:
                            DemoBridge.ToggleMode();
                            break;
                        case HotkeyClose:
                            DemoBridge.Close();
                            break;
                    }
                    return default;

                case WM_SETTINGCHANGE:
                    // wParam 是 SPI_ 动作：任务栏移动/尺寸变化会带 SPI_SETWORKAREA
                    if (wParam.Value == SPI_SETWORKAREA)
                    {
                        Log("[WS] WM_SETTINGCHANGE(SPI_SETWORKAREA) => refresh");
                        ApplyLayout("settingchange");
                        UpdateWorkspace("settingchange");
                    }
                    break;

                case WM_DISPLAYCHANGE:
                    Log($"[WS] WM_DISPLAYCHANGE bpp={wParam.Value} size={(int)(lParam.Value & 0xFFFF)}x{(int)((lParam.Value >> 16) & 0xFFFF)}");
                    ApplyLayout("displaychange");
                    UpdateWorkspace("displaychange");
                    break;

                case WM_DPICHANGED:
                    Log($"[WS] WM_DPICHANGED newDpi={wParam.Value & 0xFFFF}x{(wParam.Value >> 16) & 0xFFFF}");
                    ApplyLayout("dpichanged");
                    UpdateWorkspace("dpichanged");
                    break;

                case WM_DESTROY:
                    _exitRequested = true;
                    PInvoke.PostQuitMessage(0);
                    break;
            }

            return PInvoke.DefWindowProc(hWnd, uMsg, wParam, lParam);
        }

        private static void Cleanup()
        {
            try
            {
                if (_dws is not null)
                {
                    _dws.Content = null;
                    _dws.Dispose();
                    _dws = null;
                    Log("[CLEANUP] DesktopWindowXamlSource disposed");
                }
            }
            catch (Exception ex)
            {
                Log($"[CLEANUP] DWS dispose failed: {ex.Message}");
            }

            UnregisterHotKey(_host, HotkeyTransparency);
            UnregisterHotKey(_host, HotkeyMode);
            UnregisterHotKey(_host, HotkeyClose);

            if (!_host.IsNull)
                PInvoke.DestroyWindow(_host);

            _host = HWND.Null;
            _xamlHwnd = HWND.Null;
            Log("[CLEANUP] windows destroyed");
        }

        private static PCWSTR S(string value) => (PCWSTR)Marshal.StringToHGlobalUni(value).ToPointer();

        private static string Hx(HWND hwnd) => hwnd.IsNull ? "0" : ((nint)hwnd.Value).ToString("X");

        internal static void Log(string message)
        {
            // 遮挡轮询在后台线程写日志，必须串行化，避免 StreamWriter 并发损坏
            lock (s_logLock)
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                _log.WriteLine(line);
            }
        }
    }
}


