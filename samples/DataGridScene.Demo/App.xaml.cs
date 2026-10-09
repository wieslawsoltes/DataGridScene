using Microsoft.UI.Xaml;

namespace DataGridScene.Demo;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static MainPage? Page { get; private set; }
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "DataGridScene — Data Grid" };
        Page = new MainPage(); MainWindow.Content = Page; MainWindow.Activate();
    }
}
