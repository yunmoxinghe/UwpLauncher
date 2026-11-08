using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WinUI2Demo
{
    /// <summary>
    /// 交互式演示的桥接层：XAML 按钮/开关与宿主消息循环之间只交换请求标记。
    /// </summary>
    internal static class DemoBridge
    {
        internal static bool PendingTransparencyToggle;
        internal static bool PendingModeToggle;
        internal static bool PendingClose;

        internal static void ToggleTransparency() => PendingTransparencyToggle = true;
        internal static void ToggleMode() => PendingModeToggle = true;
        internal static void Close() => PendingClose = true;
    }

    /// <summary>
    /// 演示外壳：顶部控制条（透明/嵌入桌面/退出） + 中间 Frame（PageA/PageB） + 底部状态栏。
    /// 保持已验证的岛机制：DesktopWindowXamlSource + IXamlSourceTransparency；这里只是内容层。
    /// </summary>
    public sealed partial class DemoShell : UserControl
    {
        private bool _syncing;

        public DemoShell()
        {
            // XAML 里的 IsOn 初值也会触发 Toggled；初始化期间屏蔽，避免误发切换请求
            _syncing = true;
            InitializeComponent();
            MainFrame.Navigate(typeof(PageA), "start");
            _syncing = false;
        }

        internal Frame ContentFrame => MainFrame;

        internal void UpdateStatus(string text, bool transparent, bool desktopMode)
        {
            StatusText.Text = text;
            _syncing = true;
            if (TransparencySwitch.IsOn != transparent)
                TransparencySwitch.IsOn = transparent;
            if (DesktopSwitch.IsOn != desktopMode)
                DesktopSwitch.IsOn = desktopMode;
            _syncing = false;
        }

        private void Transparency_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_syncing)
                DemoBridge.ToggleTransparency();
        }

        private void Desktop_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_syncing)
                DemoBridge.ToggleMode();
        }

        private void Quit_Click(object sender, RoutedEventArgs e) => DemoBridge.Close();
    }
}
