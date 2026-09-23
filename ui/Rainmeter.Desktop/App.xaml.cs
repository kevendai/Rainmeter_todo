using Microsoft.UI.Xaml;

namespace Rainmeter.Desktop;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString());
            throw;
        }
    }
}
