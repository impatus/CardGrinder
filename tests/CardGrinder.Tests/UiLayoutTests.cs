using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CardGrinder.Core;
using CardGrinder.ViewModels;
using Xunit;

namespace CardGrinder.Tests;

public sealed class UiLayoutTests
{
    [Fact]
    public void PopulatedDashboardBindsAndRendersAtBothSupportedSizes()
    {
        Exception? error = null;
        string phase = "starting the STA dispatcher";
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? window = null; MainViewModel? model = null;
                ErrorListener? listener = null;
                try
                {
                    Volatile.Write(ref phase, "loading WPF resources");
                    var app = new App(); app.InitializeComponent();
                    listener = new ErrorListener();
                    PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                    Volatile.Write(ref phase, "initializing the view model");
                    model = new(new LocalStorage(Path.Combine(Path.GetTempPath(), "CardGrinder-UI-" + Guid.NewGuid())), dispatcher);
                    await model.InitializeAsync();
                    await model.InitializeAsync();
                    Assert.False(model.Busy);
                    Assert.False(model.SigningIn);
                    Assert.False(model.Connected);
                    Assert.False(model.HasError);
                    Assert.Equal("—", model.EligibleGames);
                    Assert.Equal("—", model.CardsRemaining);
                    // Fixtures are test-only. Production contains no mock library or demo account.
                    GameProgress[] fixtures = [
                        new(new(620, "Portal 2", 2, 12.3), 3, 1, true),
                    new(new(10, "A deliberately long game name that must truncate within the available row width", 3, 6.4), 3, 0, false),
                    new(new(400, "Portal", 0, 4.2), 2, 2, false)
                    ];
                    foreach (var fixture in fixtures) model.Games.Add(new OfflineGame(fixture, model.SelectionChanged));
                    model.ApplySnapshot(new(FarmState.Farming, "Farming Portal 2", fixtures, 3, TimeSpan.FromMinutes(42), DateTimeOffset.Now));
                    Volatile.Write(ref phase, "showing and arranging the window");
                    window = new(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                    window.Show();
                    window.UpdateLayout();
                    foreach (string sort in model.SortOptions)
                    {
                        model.Sort = sort;
                        Assert.Equal(620u, model.GamesView.Cast<GameViewModel>().First().AppId);
                    }
                    model.Sort = "Name";
                    Volatile.Write(ref phase, "checking selection and filters");
                    var selectAll = FindElements<CheckBox>((DependencyObject)window.Content).Single(check => Equals(check.Content, "Select all"));
                    Click(selectAll);
                    Assert.True(model.AllVisibleGamesSelected);
                    Assert.True(selectAll.IsChecked);
                    Assert.All(model.Games.Where(game => game.CanSelect), game => Assert.True(game.Selected));
                    Assert.False(model.Games.Single(game => game.AppId == 400).Selected);
                    model.Games.Single(game => game.AppId == 10).Selected = false;
                    Assert.Null(model.AllVisibleGamesSelected);
                    Assert.Null(selectAll.IsChecked);
                    Click(selectAll);
                    Assert.True(model.AllVisibleGamesSelected);
                    Click(selectAll);
                    Assert.False(model.AllVisibleGamesSelected);
                    Assert.All(model.Games, game => Assert.False(game.Selected));
                    model.Search = "620";
                    Click(selectAll);
                    Assert.True(model.Games.Single(game => game.AppId == 620).Selected);
                    Assert.False(model.Games.Single(game => game.AppId == 10).Selected);
                    model.ShowCompleted = true;
                    model.Search = "400";
                    Assert.False(model.CanSelectAll);
                    Assert.False(selectAll.IsEnabled);
                    model.Search = ""; model.ShowCompleted = false;
                    Click(selectAll);
                    string output = Path.Combine(FindRoot(), "artifacts", "ui-tests"); Directory.CreateDirectory(output);
                    foreach (var size in new[] { (1180d, 820d), (940d, 680d) })
                    {
                        Volatile.Write(ref phase, $"rendering the {size.Item1}-pixel layout");
                        window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout();
                        Assert.Contains(FindText((DependencyObject)window.Content), text => text == "Portal 2");
                        var root = (FrameworkElement)window.Content;
                        Assert.True(root.ActualWidth > 900);
                        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        using var stream = File.Create(Path.Combine(output, $"library-{size.Item1}.png"));
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(stream);
                    }
                    var advanced = fixtures.Select(game => game with { Active = game.Game.AppId == 10 }).ToArray();
                    Volatile.Write(ref phase, "checking queue and reconnection controls");
                    model.ApplySnapshot(new(FarmState.Farming, "Farming the next selected game", advanced, 3, TimeSpan.FromMinutes(43), DateTimeOffset.Now));
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(10u, model.ActiveGame!.AppId);
                    Assert.Equal(10u, model.GamesView.Cast<GameViewModel>().First().AppId);
                    model.ApplySnapshot(new(FarmState.Reconnecting, "Reconnecting to Steam (attempt 1/5) in 5 seconds…", advanced.Select(game => game with { Active = false }).ToArray(), 3, TimeSpan.FromMinutes(43), DateTimeOffset.Now));
                    window.UpdateLayout();
                    Assert.True(model.Recovering); Assert.Equal("RECONNECTING", model.StateLabel);
                    Assert.True(model.PauseCommand.CanExecute(null)); Assert.True(model.StopCommand.CanExecute(null));
                    Assert.True(model.LogoutCommand.CanExecute(null)); Assert.False(model.ConnectCommand.CanExecute(null));
                    Assert.False(model.NewQrCommand.CanExecute(null)); Assert.False(model.StartCommand.CanExecute(null));
                    Assert.Contains("Reconnecting", model.Status);
                    Assert.Empty(listener.Errors);
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    Volatile.Write(ref phase, "closing the window and awaiting service shutdown");
                    try
                    {
                        window?.DisposeTray(); window?.Close();
                        if (model is not null) await model.DisposeAsync();
                    }
                    catch (Exception ex) { error ??= ex; }
                    finally
                    {
                        if (listener is not null) PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }
            }));
            // Keep the dispatcher alive for asynchronous WPF callbacks and disposal, as in the real app.
            Dispatcher.Run();
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"WPF layout test timed out during {Volatile.Read(ref phase)}.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static IEnumerable<string> FindText(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindText(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static IEnumerable<T> FindElements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T element) yield return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindElements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(CheckBox control) => typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, null);
    private sealed class OfflineGame(GameProgress progress, Action selectionChanged) : GameViewModel(progress, selectionChanged)
    {
        public override string? CoverUrl => null;
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CardGrinder.sln"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
    private sealed class ErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
