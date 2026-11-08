using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace WinUI2Probe
{
    /// <summary>
    /// Frame + Page 导航扩展性验证用的两个页面（XAML 编译，页面类型进入 XamlTypeInfo）。
    /// 布局判据：顶部 40 DIP 色带证明当前页面渲染；中部左列透明、右列 50% 红用于透明采样；
    /// 底部 96 DIP 状态区（TextBox + ScrollViewer）用于返回后状态保留检查。
    /// </summary>
    public sealed partial class PageA : Page
    {
        internal static PageA? Last;
        internal static int InstanceCount;
        internal static int NavigatedToCount;
        internal static int NavigatedFromCount;

        // XAML 生成的 x:Name 字段是 private，这里提供同程序集内的访问器供探针做状态检查
        internal TextBox State => StateBox;
        internal ScrollViewer Scroll => Scroller;

        public PageA()
        {
            InstanceCount++;
            Last = this;
            InitializeComponent();

            // 对照：默认 Disabled；--navcache 1 时改成 Required，验证返回时是否复用页面实例
            if (Program.NavCacheRequested)
                NavigationCacheMode = NavigationCacheMode.Required;

            Log($"[PAGE-A] ctor instance=#{InstanceCount} Frame={(Frame is null ? "NULL" : Frame.GetType().FullName)} " +
                $"Page.NavigationService property exists={typeof(Page).GetProperty("NavigationService") is not null} " +
                $"cacheMode={NavigationCacheMode}");
            Log($"[PAGE-A] page resource PageAOnlyBrush => {DescribeResource("PageAOnlyBrush")}");
            Log($"[PAGE-A] theme resource ApplicationPageBackgroundThemeBrush => {DescribeAppResource("ApplicationPageBackgroundThemeBrush")}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            NavigatedToCount++;
            var frame = Frame;
            Log($"[PAGE-A] OnNavigatedTo #{NavigatedToCount} mode={e.NavigationMode} param='{e.Parameter}' " +
                $"Frame={(frame is null ? "NULL" : frame.GetType().FullName)} " +
                $"backStack={(frame is null ? -1 : frame.BackStackDepth)} canGoBack={(frame is not null && frame.CanGoBack)} " +
                $"Window.Current={(Window.Current is null ? "NULL" : Window.Current.GetType().Name)}");

            // 只在首次进入时初始化；返回时保留导航前的状态（观测点：文本与滚动位置）
            if (NavigatedToCount == 1)
                StateBox.Text = "A0-initial";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            NavigatedFromCount++;
            Log($"[PAGE-A] OnNavigatedFrom #{NavigatedFromCount} mode={e.NavigationMode} " +
                $"text='{StateBox.Text}' scrollOffset={Scroller.VerticalOffset:F1}");
        }

        private string DescribeResource(string key)
        {
            try
            {
                return Describe(Resources[key]);
            }
            catch (Exception ex)
            {
                return $"missing ({ex.GetType().Name} hr=0x{ex.HResult:X8})";
            }
        }

        private static string DescribeAppResource(string key)
        {
            try
            {
                return Describe(Application.Current?.Resources[key]);
            }
            catch (Exception ex)
            {
                return $"missing ({ex.GetType().Name} hr=0x{ex.HResult:X8})";
            }
        }

        private static string Describe(object? value)
        {
            return value is SolidColorBrush brush
                ? $"SolidColorBrush #{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
                : (value?.GetType().FullName ?? "null");
        }

        private static void Log(string message) => Program.Log(message);
    }

    public sealed partial class PageB : Page
    {
        internal static PageB? Last;
        internal static int InstanceCount;
        internal static int NavigatedToCount;

        public PageB()
        {
            InstanceCount++;
            Last = this;
            InitializeComponent();
            Log($"[PAGE-B] ctor instance=#{InstanceCount} Frame={(Frame is null ? "NULL" : Frame.GetType().FullName)}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            NavigatedToCount++;
            var frame = Frame;
            Log($"[PAGE-B] OnNavigatedTo #{NavigatedToCount} mode={e.NavigationMode} param='{e.Parameter}' " +
                $"Frame={(frame is null ? "NULL" : frame.GetType().FullName)} " +
                $"backStack={(frame is null ? -1 : frame.BackStackDepth)} canGoBack={(frame is not null && frame.CanGoBack)}");
            StateBox.Text = $"B0-navigated-{NavigatedToCount}";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Log($"[PAGE-B] OnNavigatedFrom mode={e.NavigationMode}");
        }

        private static void Log(string message) => Program.Log(message);
    }
}
