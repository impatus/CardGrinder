using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CardGrinder.Core;
using CardGrinder.ViewModels;

namespace CardGrinder;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private MainViewModel? model;
    private bool shuttingDown;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Length == 2 && e.Args[0] == "--smoke-test";
        instance = new Mutex(true, "Local\\CardGrinder.SingleInstance", out bool first);
        if (!first) { System.Windows.MessageBox.Show("CardGrinder is already running. Check the system tray.", "CardGrinder"); Shutdown(); return; }
        model = new(new LocalStorage(), Dispatcher);
        var window = new MainWindow(model);
        MainWindow = window;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _ = ShutDownSafelyAsync();
            System.Windows.MessageBox.Show("CardGrinder encountered an unexpected error and is stopping its Steam connection.", "CardGrinder");
        };
        if (smoke)
        {
            window.ShowInTaskbar = false; window.Left = -20000; window.Top = -20000;
            _ = SmokeTestAsync(window, e.Args[1]);
        }
        window.Show();
        if (!smoke && model.StartMinimized) window.WindowState = WindowState.Minimized;
        if (!smoke) _ = model.InitializeAsync();
    }

    public async Task ShutDownSafelyAsync()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        try { if (model is not null) await model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { /* Process termination also closes the separate Steam transport. Never persist error payloads. */ }
        if (MainWindow is MainWindow window) window.DisposeTray();
        Shutdown();
    }

    private async Task SmokeTestAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        var bindings = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveView(window, Path.Combine(output, "dashboard.png"));
            window.Width = 940; window.Height = 680;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveView(window, Path.Combine(output, "dashboard-small.png"));
            model!.SettingsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveView(window, Path.Combine(output, "settings.png"));
            File.WriteAllText(Path.Combine(output, "smoke-test.txt"), bindings.Errors.Count == 0 ? "PASS: Window, compact layout and settings rendered with no binding errors." : string.Join(Environment.NewLine, bindings.Errors));
            if (bindings.Errors.Count > 0) Environment.ExitCode = 1;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "smoke-test.txt"), ex.GetType().Name + ": " + ex.Message); Environment.ExitCode = 1; }
        finally { await ShutDownSafelyAsync(); }
    }

    private static void SaveView(MainWindow window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
