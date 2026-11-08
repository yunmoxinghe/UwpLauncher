using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace WinUI2Demo
{
    /// <summary>
    /// 演示页 A：可导航到 B；返回按钮在返回栈为空时禁用。
    /// 页面实例计数用于让用户看到：默认 NavigationCacheMode 下，GoBack 会重建页面。
    /// </summary>
    public sealed partial class PageA : Page
    {
        internal static int InstanceCount;
        internal static int NavigatedToCount;

        public PageA()
        {
            InstanceCount++;
            InitializeComponent();
            InstanceText.Text = $"PageA 实例 #{InstanceCount}";
            Log($"[PAGE-A] ctor instance=#{InstanceCount} cacheMode={NavigationCacheMode}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            NavigatedToCount++;
            Log($"[PAGE-A] OnNavigatedTo #{NavigatedToCount} mode={e.NavigationMode} param='{e.Parameter}' " +
                $"backStack={Frame?.BackStackDepth} canGoBack={Frame?.CanGoBack}");
            InstanceText.Text = $"PageA 实例 #{InstanceCount} · OnNavigatedTo {NavigatedToCount} 次";
            BackButton.IsEnabled = Frame?.CanGoBack == true;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Log($"[PAGE-A] OnNavigatedFrom mode={e.NavigationMode}");
        }

        private void GoB_Click(object sender, RoutedEventArgs e)
        {
            Log("[DEMO] PageA -> Navigate(PageB)");
            Frame?.Navigate(typeof(PageB), "from-A");
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Log("[DEMO] PageA -> GoBack()");
                Frame.GoBack();
            }
        }

        private static void Log(string message) => Program.Log(message);
    }

    /// <summary>演示页 B：返回 A。</summary>
    public sealed partial class PageB : Page
    {
        internal static int InstanceCount;
        internal static int NavigatedToCount;

        public PageB()
        {
            InstanceCount++;
            InitializeComponent();
            InstanceText.Text = $"PageB 实例 #{InstanceCount}";
            Log($"[PAGE-B] ctor instance=#{InstanceCount} cacheMode={NavigationCacheMode}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            NavigatedToCount++;
            Log($"[PAGE-B] OnNavigatedTo #{NavigatedToCount} mode={e.NavigationMode} param='{e.Parameter}' " +
                $"backStack={Frame?.BackStackDepth} canGoBack={Frame?.CanGoBack}");
            InstanceText.Text = $"PageB 实例 #{InstanceCount} · OnNavigatedTo {NavigatedToCount} 次";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Log($"[PAGE-B] OnNavigatedFrom mode={e.NavigationMode}");
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Log("[DEMO] PageB -> GoBack()");
                Frame.GoBack();
            }
        }

        private static void Log(string message) => Program.Log(message);
    }
}
