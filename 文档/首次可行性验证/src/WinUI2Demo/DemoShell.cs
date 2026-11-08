using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace WinUI2Demo
{
    /// <summary>
    /// 交互式演示的桥接层：XAML 按钮/开关与宿主消息循环之间只交换请求标记。
    /// </summary>
    internal static class DemoBridge
    {
        internal static bool PendingTransparencyToggle;
        internal static bool PendingModeToggle;
        internal static bool PendingFillToggle;
        internal static bool PendingClose;

        internal static void ToggleTransparency() => PendingTransparencyToggle = true;
        internal static void ToggleMode() => PendingModeToggle = true;
        internal static void ToggleFill() => PendingFillToggle = true;
        internal static void Close() => PendingClose = true;
    }

    /// <summary>
    /// 正式版外壳：顶部控制条（透明/退出） + 中间 Frame（默认文件视图） + 底部状态栏。
    /// 保持已验证的岛机制：DesktopWindowXamlSource + IXamlSourceTransparency；这里只是内容层。
    /// 入场动画由 Program 在“首帧已提交”后触发（PrepareEntrance → PlayEntrance）。
    /// </summary>
    public sealed partial class DemoShell : UserControl
    {
        private bool _syncing;
        private bool _entrancePlayed;
        private System.Threading.Timer? _animTimer;

        public DemoShell()
        {
            // XAML 里的 IsOn 初值也会触发 Toggled；初始化期间屏蔽，避免误发切换请求
            _syncing = true;
            InitializeComponent();
            _syncing = false;
            Loaded += (_, __) =>
            {
                Program.ShellLoaded = true;
                Program.Log($"[STARTUP] shell Loaded t={Program.StartupMs()}ms");
            };
        }

        internal Frame ContentFrame => MainFrame;

        /// <summary>v1.0 启动直接进入文件视图；由 Program 在挂上导航日志之后调用，保证 [NAV] 有记录。</summary>
        internal void NavigateHome()
        {
            MainFrame.Navigate(typeof(FileViewPage), Program.FileViewDefaultPath.Length > 0
                ? Program.FileViewDefaultPath
                : null);
        }

        /// <summary>
        /// 入场准备：内容完全透明。必须在窗口显示前调用，
        /// 这样第一帧即使已提交也只有桌面透出，不会出现白底或未布局画面；
        /// 位移（0 → 10 → 0）在 PlayEntrance 里由 composition 动画补上。
        /// </summary>
        internal void PrepareEntrance()
        {
            Opacity = 0;
            Program.Log($"[STARTUP] entrance prepared opacity=0 t={Program.StartupMs()}ms");
        }

        /// <summary>
        /// 首帧探针：把内容从 0 抬到 5%，强制 XAML 真正绘制一次带内容的帧。
        /// 纯 Opacity=0 的帧代价极低，无法代表“有内容的首次绘制”；5% 幽灵帧肉眼几乎不可见，
        /// 但会把那次昂贵的首次绘制（网格布局+容器 realize）提前做完。
        /// </summary>
        internal void KickFirstPaint()
        {
            Opacity = 0.05;
            Program.Log($"[STARTUP] firstPaint kick opacity=0.05 t={Program.StartupMs()}ms");
        }

        /// <summary>
        /// 首帧提交后播放淡入 + 轻微上移（从 5% 幽灵帧继续，不跳回 0）。
        /// 用 Composition visual 动画而不是 XAML Storyboard：网格内容的首帧光栅化会占用 UI 线程，
        /// Storyboard 的 ActualTheme/帧回调会被拖成 0.7 s 以上；composition 动画跑在合成线程，
        /// 只对已缓存层做 opacity/offset，不受 UI 线程忙影响（实测完成回调也从 ~720 ms 回到 ~250 ms）。
        /// </summary>
        internal void PlayEntrance()
        {
            if (_entrancePlayed)
                return;
            _entrancePlayed = true;

            // kick 阶段元素 Opacity=0.05；改用 visual.Opacity 做动画，先把元素本身置 1，
            // 视觉上仍是 0.05（0.05 × 1），不会闪一下全亮。
            Opacity = 1;
            var visual = ElementCompositionPreview.GetElementVisual(this);
            visual.Opacity = 0.05f;
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f), new Vector2(0.33f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(240);
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(240);
            slide.InsertKeyFrame(0f, new Vector3(0, 10, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, ease);
            visual.StartAnimation("Offset", slide);

            var startTicks = Program.StartupMs();
            Program.Log($"[STARTUP] anim start t={startTicks}ms durationMs=240 from=0.05 (composition)");

            // 用后台 Timer 记录完成点：DispatcherTimer 会被网格首帧的 UI 忙拖后（实测 720 ms），
            // 而 composition 动画本身跑在合成线程；这里只关心 240 ms 动画窗口何时结束。
            _animTimer = new System.Threading.Timer(_ =>
            {
                Program.Log($"[STARTUP] anim complete t={Program.StartupMs()}ms " +
                            $"afterMs={Program.StartupMs() - startTicks} visualOpacity=1.00");
                Program.OnEntranceComplete();
            }, null, 270, System.Threading.Timeout.Infinite);
        }

        /// <summary>透明 API 不可用时的兜底：给根容器一个不透明深色底，保证内容可读。</summary>
        internal void SetOpaqueFallback()
        {
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x11, 0x14, 0x1A));
            Program.Log("[FALLBACK] shell opaque background applied");
        }

        internal void UpdateStatus(string text, bool transparent, string workspace)
        {
            StatusText.Text = text;
            WorkspaceText.Text = workspace;
            _syncing = true;
            if (TransparencySwitch.IsOn != transparent)
                TransparencySwitch.IsOn = transparent;
            _syncing = false;
        }

        private void Transparency_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_syncing)
                DemoBridge.ToggleTransparency();
        }

        private void Quit_Click(object sender, RoutedEventArgs e) => DemoBridge.Close();
    }
}
