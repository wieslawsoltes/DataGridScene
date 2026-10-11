using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static MainPage? Page { get; private set; }

    public App()
    {
        // Pin the existing package's static face. The Skia browser host cannot rely on
        // system Segoe UI or variable-font fallback. This also reaches native popups.
        Uno.UI.FeatureConfiguration.Font.DefaultTextFontFamily = "ms-appx:///Uno.Fonts.OpenSans/Fonts/OpenSans-Regular.ttf";
        // The sample is code-first; register the same native control resources used by XAML applications.
        Resources.MergedDictionaries.Add(new XamlControlsResources());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "DataGridScene — Data Grid" };
        Page = new MainPage();
        MainWindow.Content = Page;
        MainWindow.Activate();
    }
}
