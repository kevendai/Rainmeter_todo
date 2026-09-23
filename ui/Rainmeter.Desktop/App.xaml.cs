using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Text;

namespace Rainmeter.Desktop;

public partial class App : Application
{
    private Window? window;
    private Mutex? instanceMutex;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var route = Environment.GetCommandLineArgs().Skip(1).ToArray();
            instanceMutex = new Mutex(true, @"Local\RainmeterDesktopUi", out var firstInstance);
            if (!firstInstance)
            {
                instanceMutex.Dispose();
                instanceMutex = null;
                _ = SendToRunningAsync(route);
                return;
            }
            window = new MainWindow();
            window.Activate();
            _ = ListenForRoutesAsync();
            ((MainWindow)window).OpenInitialRoute(route);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString());
            throw;
        }
    }

    private async Task ListenForRoutesAsync()
    {
        while (window is MainWindow main)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("RainmeterDesktopUi", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                if (line is not null) main.OpenInitialRoute(DecodeRoute(line));
            }
            catch { await Task.Delay(250); }
        }
    }

    private async Task SendToRunningAsync(string[] route)
    {
        var encoded = string.Join("|", route.Take(3).Select(value =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "RainmeterDesktopUi", PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(200);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                await writer.WriteLineAsync(encoded);
                await writer.FlushAsync();
                Exit();
                return;
            }
            catch { await Task.Delay(100); }
        }
        Exit();
    }

    private static string[] DecodeRoute(string value)
    {
        try { return value.Split('|').Select(part => Encoding.UTF8.GetString(Convert.FromBase64String(part))).ToArray(); }
        catch { return []; }
    }
}
