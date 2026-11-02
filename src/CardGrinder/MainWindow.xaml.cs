using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CardGrinder.ViewModels;
using Forms = System.Windows.Forms;

namespace CardGrinder;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem pause;
    private readonly Forms.ToolStripMenuItem resume;
    private bool disposed;
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = this.model = model;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open CardGrinder", null, (_, _) => RestoreWindow());
        pause = new("Pause", null, (_, _) => model.PauseCommand.Execute(null));
        resume = new("Resume", null, (_, _) => model.ResumeCommand.Execute(null));
        menu.Items.Add(pause); menu.Items.Add(resume); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ((App)System.Windows.Application.Current).ShutDownSafelyAsync());
        tray = new Forms.NotifyIcon { Text = "CardGrinder · Standby", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("CardGrinder.Assets.app.ico");
        if (iconStream is not null) tray.Icon = new System.Drawing.Icon(iconStream);
        tray.DoubleClick += (_, _) => RestoreWindow();
        menu.Opening += (_, _) => { pause.Enabled = model.PauseCommand.CanExecute(null); resume.Enabled = model.ResumeCommand.CanExecute(null); };
        model.PropertyChanged += OnModelChanged;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && model.MinimizeToTray) Hide(); };
        Closing += OnClosing;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed) return;
        if (e.PropertyName == nameof(MainViewModel.StateLabel)) tray.Text = $"CardGrinder · {model.StateLabel}";
        if (e.PropertyName == nameof(MainViewModel.ActiveGame) && model.ActiveGame is { } active)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (!disposed) GameList.ScrollIntoView(active);
            }));
    }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (disposed) return;
        e.Cancel = true;
        await ((App)System.Windows.Application.Current).ShutDownSafelyAsync();
    }
    public void DisposeTray()
    {
        if (disposed) return;
        disposed = true; model.PropertyChanged -= OnModelChanged;
        tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
