using System;
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

namespace WinUI2Probe
{
    /// <summary>
    /// 最小 UWP XAML Island 透明背景探针。
    /// 复刻 DesktopFlyouts 的 UWP 路径：WS_EX_NOREDIRECTIONBITMAP 宿主 + DesktopWindowXamlSource
    /// + Window.Current 上的 IXamlSourceTransparency(IsBackgroundTransparent)。
    /// 支持三种宿主模式：topmost（DesktopFlyouts 原方案）、plain、workerw（SetParent 到桌面 WorkerW）。
    /// </summary>
    internal static unsafe class Program
    {
        private const int HostX = 60, HostY = 60, HostW = 520, HostH = 380;
        private const int BackX = 20, BackY = 20, BackW = 600, BackH = 460;

        private const uint WS_POPUP = 0x80000000u;
        private const uint WS_CHILD = 0x40000000u;
        private const uint WS_VISIBLE = 0x10000000u;
        private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000u;
        private const uint WS_EX_TOOLWINDOW = 0x00000080u;
        private const uint WS_EX_TOPMOST = 0x00000008u;
        private const uint WS_EX_NOACTIVATE = 0x08000000u;

        private const uint WM_SIZE = 0x0005u;
        private const uint WM_DESTROY = 0x0002u;
        private const uint WM_QUIT = 0x0012u;
        private const uint WM_SPAWN_WORKERW = 0x052Cu;

        private static readonly Guid IID_IDesktopWindowXamlSourceNative2 = new("E3DCD8C7-3057-4692-99C3-7B7720AFDA31");
        private static readonly Guid IID_ICoreWindowInterop = new("45D64A29-A63E-4CB6-B498-5781D298CB4F");
        private static readonly Guid IID_IWindowPrivate = new("06636C29-5A17-458D-8EA2-2422D997A922");

        private static readonly WNDPROC s_wndProc = new(WndProc);

        private static StreamWriter _log = StreamWriter.Null;
        private static string _mode = "topmost";
        private static string _content = "winui2";
        private static bool _transparent = true;
        private static int _holdMs = 4000;
        private static int _captureAtMs = 2500;

        private static readonly HWND InsertAfterTop = new(IntPtr.Zero);
        private static readonly HWND InsertAfterBottom = new((IntPtr)1);

        private static HWND _host = HWND.Null;
        private static HWND _backdrop = HWND.Null;
        private static HWND _xamlHwnd = HWND.Null;
        private static HWND _coreHwnd = HWND.Null;
        private static HWND _workerW = HWND.Null;
        private static DesktopWindowXamlSource? _dws;
        private static IDesktopWindowXamlSourceNative2? _native2;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorRef);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hDc, int x, int y);

        [STAThread]
        private static int Main(string[] args)
        {
            ParseArgs(args);
            try
            {
                _log = new StreamWriter(GetArgValue(args, "--log") ?? "island-probe.log", append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch
            {
                // 无法写日志时仍继续，输出会丢失
            }

            Log("=== WinUI2Probe ===");
            Log($"pid={Environment.ProcessId} os={Environment.OSVersion.VersionString}");

            var hr = PInvoke.RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED);
            Log($"RoInitialize(STA) => hr=0x{hr.Value:X8}");
            LogPackageIdentity();

            // 与 DesktopFlyouts 样例一致：在 Application 构造期间完成 XAML 初始化与消息循环
            _ = new App();

            _log.Flush();
            return 0;
        }

        private static void ParseArgs(string[] args)
        {
            var mode = GetArgValue(args, "--mode");
            if (!string.IsNullOrEmpty(mode))
                _mode = mode!;

            var content = GetArgValue(args, "--content");
            if (!string.IsNullOrEmpty(content))
                _content = content!;

            var transparent = GetArgValue(args, "--transparent");
            if (!string.IsNullOrEmpty(transparent))
                _transparent = transparent == "1" || transparent.Equals("true", StringComparison.OrdinalIgnoreCase);

            var hold = GetArgValue(args, "--hold");
            if (int.TryParse(hold, out var holdValue))
                _holdMs = holdValue;

            var capture = GetArgValue(args, "--capture");
            if (int.TryParse(capture, out var captureValue))
                _captureAtMs = captureValue;
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
                Log($"mode={_mode} content={_content} transparent={_transparent} hold={_holdMs} captureAt={_captureAtMs}");

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

                CreateBackdropWindow();
                CreateHostWindow();
                InitializeXamlIsland();
                ApplyTransparency();

                Log($"host dpi={PInvoke.GetDpiForWindow(_host)} coreHwnd=0x{Hx(_coreHwnd)} xamlHwnd=0x{Hx(_xamlHwnd)}");

                if (_mode == "workerw")
                    ReparentToWorkerW();

                ShowWindows();

                Pump(_captureAtMs);
                CaptureSamples();
                Pump(Math.Max(0, _holdMs - _captureAtMs));
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

        private static void CreateBackdropWindow()
        {
            var className = "WinUI2ProbeBackdrop." + Guid.NewGuid().ToString("N");
            var hInstance = PInvoke.GetModuleHandle(default);

            WNDCLASSW wc = default;
            wc.lpfnWndProc = (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)(void*)Marshal.GetFunctionPointerForDelegate(s_wndProc);
            wc.hInstance = hInstance;
            // 纯绿（COLORREF=0x00BBGGRR）
            wc.hbrBackground = (HBRUSH)(void*)CreateSolidBrush(0x0000FF00u);
            wc.lpszClassName = S(className);

            var atom = PInvoke.RegisterClass(&wc);
            Log($"[RegisterClass] backdrop atom={atom} lastErr={Marshal.GetLastWin32Error()}");

            var backdropExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_mode == "topmost")
                backdropExStyle |= WS_EX_TOPMOST;

            _backdrop = PInvoke.CreateWindowEx(
                (WINDOW_EX_STYLE)backdropExStyle,
                S(className),
                S("WinUI2ProbeBackdrop"),
                (WINDOW_STYLE)(WS_POPUP | WS_VISIBLE),
                BackX, BackY, BackW, BackH,
                HWND.Null, HMENU.Null, hInstance, null);
            Log($"[CreateWindow] backdrop hwnd=0x{Hx(_backdrop)} lastErr={Marshal.GetLastWin32Error()}");
        }

        private static void CreateHostWindow()
        {
            var className = "WinUI2ProbeHost." + Guid.NewGuid().ToString("N");
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
                S("WinUI2ProbeHost"),
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
            if (_content == "grid")
            {
                // 诊断用对照：同一打包上下文里不放 WinUI 2 控件，只放原来的判据布局
                Log("[CONTENT] grid: plain sampling layout (diagnostic)");
                return BuildSamplingLayout();
            }

            // 实验目标：直接以 WinUI 2 控件（Microsoft.UI.Xaml.Controls）作为 DesktopWindowXamlSource.Content。
            Microsoft.UI.Xaml.Controls.NavigationView nav;
            try
            {
                nav = new Microsoft.UI.Xaml.Controls.NavigationView();
                Log($"[WINUI2] new NavigationView() OK, type={nav.GetType().FullName}");
            }
            catch (Exception ex)
            {
                Log($"[WINUI2] new NavigationView() FAILED: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg={ex.Message}");
                if (ex.InnerException is not null)
                    Log($"[WINUI2] inner: {ex.InnerException.GetType().FullName} hr=0x{ex.InnerException.HResult:X8} msg={ex.InnerException.Message}");
                return null;
            }

            try
            {
                nav.IsSettingsVisible = false;
                nav.IsBackButtonVisible = Microsoft.UI.Xaml.Controls.NavigationViewBackButtonVisible.Collapsed;
                nav.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.LeftMinimal;
                nav.IsPaneOpen = false;
                // 岛背景透明的前提下，控件自身不能再画一层不透明底色；置为透明属于内容设计，不改变透明开关语义
                nav.Background = new SolidColorBrush(Colors.Transparent);

                var infoBar = new Microsoft.UI.Xaml.Controls.InfoBar
                {
                    Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    Title = "WinUI 2 InfoBar",
                    Message = "Microsoft.UI.Xaml.dll loaded from the framework package",
                    IsOpen = true,
                    IsClosable = false,
                    Margin = new Thickness(8),
                };
                Log($"[WINUI2] InfoBar created, severity={infoBar.Severity}");

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.Children.Add(infoBar);
                Grid.SetRow(infoBar, 0);
                var sampling = BuildSamplingLayout();
                root.Children.Add(sampling);
                Grid.SetRow(sampling, 1);

                nav.Content = root;
                Log("[WINUI2] NavigationView.Content set (WinUI 2 control as island root)");
                return nav;
            }
            catch (Exception ex)
            {
                Log($"[WINUI2] NavigationView setup FAILED: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg={ex.Message}");
                if (ex.InnerException is not null)
                    Log($"[WINUI2] inner: {ex.InnerException.GetType().FullName} hr=0x{ex.InnerException.HResult:X8} msg={ex.InnerException.Message}");
                return null;
            }
        }

        private static Grid BuildSamplingLayout()
        {
            // 与 IslandProbe 相同的判据布局：左列完全透明，右列 50% 红。用比例布局，避免 DPI 缩放影响采样。
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new Grid();
            var leftTitle = new TextBlock
            {
                Text = "TRANSPARENT REGION",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 20,
                Margin = new Thickness(14),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var leftSub = new TextBlock
            {
                Text = "left half = no background",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 14,
                Margin = new Thickness(14, 46, 14, 14),
                VerticalAlignment = VerticalAlignment.Top,
            };
            left.Children.Add(leftTitle);
            left.Children.Add(leftSub);
            root.Children.Add(left);
            Grid.SetColumn(left, 0);

            var right = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x00, 0x00)),
                Child = new TextBlock
                {
                    Text = "50% RED",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 20,
                    Margin = new Thickness(14),
                },
            };
            root.Children.Add(right);
            Grid.SetColumn(right, 1);

            return root;
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

        private static void TrySetIsBackgroundTransparent(bool value)
        {
            try
            {
                var window = Window.Current;
                if (window is null)
                {
                    Log("[XST] FAILED: Window.Current is NULL, cannot QI IXamlSourceTransparency");
                    return;
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
                Log($"[XST] set_IsBackgroundTransparent({value}) => hr=0x{setHr.Value:X8} readback={readback}");
            }
            catch (Exception ex)
            {
                Log($"[XST] FAILED to set IsBackgroundTransparent={value}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ReparentToWorkerW()
        {
            _workerW = FindWorkerW();
            if (_workerW.IsNull)
            {
                Log("[WORKERW] not found, keep top-level windows");
                return;
            }

            // 对比窗放在 HWND_TOP：本机 WorkerW 内部有壁纸合成子窗，HWND_BOTTOM 会被其遮住；
            // 随后再把宿主放到对比窗之上。
            ReparentWindow(_backdrop, _workerW, BackX, BackY, BackW, BackH, bottom: false);
            ReparentWindow(_host, _workerW, HostX, HostY, HostW, HostH, bottom: false);

            // 确保宿主位于同一 WorkerW 内的 backdrop 之上
            PInvoke.SetWindowPos(
                _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

            Log($"[WORKERW] parent=0x{Hx(_workerW)} hostIsChild={(bool)PInvoke.IsChild(_workerW, _host)} backdropIsChild={(bool)PInvoke.IsChild(_workerW, _backdrop)}");
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

        private static void ReparentWindow(HWND hwnd, HWND parent, int x, int y, int w, int h, bool bottom)
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
                SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }

        private static void ShowWindows()
        {
            if (_mode != "workerw")
            {
                PInvoke.SetWindowPos(
                    _backdrop, InsertAfterTop, BackX, BackY, BackW, BackH,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                PInvoke.SetWindowPos(
                    _host, InsertAfterTop, HostX, HostY, HostW, HostH,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            }

            PInvoke.ShowWindow(_backdrop, SHOW_WINDOW_CMD.SW_SHOW);
            PInvoke.ShowWindow(_host, SHOW_WINDOW_CMD.SW_SHOW);
            if (!_xamlHwnd.IsNull)
                PInvoke.ShowWindow(_xamlHwnd, SHOW_WINDOW_CMD.SW_SHOW);

            Log($"[SHOW] hostVisible={(bool)PInvoke.IsWindowVisible(_host)} xamlVisible={(bool)PInvoke.IsWindowVisible(_xamlHwnd)} backdropVisible={(bool)PInvoke.IsWindowVisible(_backdrop)}");
            LogRects();
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

        private static void CaptureSamples()
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                Log("[SAMPLE] FAILED: GetDC(NULL) returned NULL");
                return;
            }

            try
            {
                var dpi = PInvoke.GetDpiForWindow(_host);
                Log($"[SAMPLE] dpi={dpi} hostRect=({HostX},{HostY},{HostW}x{HostH}) backdrop=({BackX},{BackY},{BackW}x{BackH})");

                Sample(hdc, "transparent_left", HostX + HostW / 4, HostY + HostH * 55 / 100);
                Sample(hdc, "semi_red_right", HostX + HostW * 3 / 4, HostY + HostH * 55 / 100);
                Sample(hdc, "host_top_left", HostX + 5, HostY + 5);
                Sample(hdc, "backdrop_outside", BackX + 10, BackY + 10);
                Sample(hdc, "desktop_reference", 10, 700);
                // WinUI 2 专属控件（InfoBar）右上角留白处：用于证明内容确实由 Microsoft.UI.Xaml 渲染
                Sample(hdc, "winui2_infobar", HostX + 420, HostY + 30);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        private static void Sample(IntPtr hdc, string name, int x, int y)
        {
            var color = GetPixel(hdc, x, y);
            if (color == 0xFFFFFFFF)
            {
                Log($"[SAMPLE] {name} ({x},{y}) => INVALID/CLR_INVALID");
                return;
            }

            var r = (byte)(color & 0xFF);
            var g = (byte)((color >> 8) & 0xFF);
            var b = (byte)((color >> 16) & 0xFF);
            Log($"[SAMPLE] {name} ({x},{y}) COLORREF=0x{color:X8} RGB=({r},{g},{b})");
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

                case WM_DESTROY:
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

            foreach (var hwnd in new[] { _host, _backdrop })
            {
                if (!hwnd.IsNull)
                    PInvoke.DestroyWindow(hwnd);
            }

            _host = HWND.Null;
            _backdrop = HWND.Null;
            _xamlHwnd = HWND.Null;
            Log("[CLEANUP] windows destroyed");
        }

        private static PCWSTR S(string value) => (PCWSTR)Marshal.StringToHGlobalUni(value).ToPointer();

        private static string Hx(HWND hwnd) => hwnd.IsNull ? "0" : ((nint)hwnd.Value).ToString("X");

        private static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            _log.WriteLine(line);
        }
    }
}
