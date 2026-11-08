using Windows.UI.Xaml;

namespace WinUI2Probe
{
    public sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            Program.Run();
        }
    }
}
