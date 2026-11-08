using Windows.UI.Xaml;

namespace IslandProbe
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
