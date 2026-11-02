using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CardGrinder.Core;
using CardGrinder.Infrastructure;
using QRCoder;

namespace CardGrinder.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalStorage storage;
    private readonly SteamSession session;
    private readonly SteamCardLibrary library;
    private readonly FarmingEngine engine;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? signIn;
    private CancellationTokenSource? currentOperation;
    private CancellationToken WorkToken => currentOperation?.Token ?? lifetime.Token;
    private AppSettings settings;
    private FarmSnapshot? snapshot;
    private DateTimeOffset snapshotAt;
    private bool busy, signingIn, settingsOpen, together, showCompleted;
    private bool initialized;
    private string search = "", sort = "Name", error = "", connection = "Not connected", status = "Ready when you are";
    private SteamProfile? profile;
    private ImageSource? qrImage;
    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public string[] SortOptions { get; } = ["Name", "Cards remaining", "Playtime"];
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand NewQrCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public AsyncCommand StopCommand { get; }
    public RelayCommand CancelSignInCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) { NotifyCommands(); RaiseLibraryStatus(); } } }
    public bool SigningIn { get => signingIn; private set => Set(ref signingIn, value); }
    public bool SettingsOpen { get => settingsOpen; set => Set(ref settingsOpen, value); }
    public bool Together { get => together; set => Set(ref together, value); }
    public bool ShowCompleted { get => showCompleted; set { Set(ref showCompleted, value); GamesView.Refresh(); Raise(nameof(IsEmpty)); SelectionChanged(); } }
    public string Search { get => search; set { Set(ref search, value); GamesView.Refresh(); Raise(nameof(IsEmpty)); SelectionChanged(); } }
    public string Sort { get => sort; set { Set(ref sort, value); ApplySort(); } }
    public string Error { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); RaiseLibraryStatus(); } }
    public bool HasError => Error.Length > 0;
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string Status
    {
        get => Recovering ? status : Busy && !Connected ? "Connecting to Steam…" : Connected && snapshot?.UpdatedAt is null
            ? Busy ? "Steam connected. Loading card status…" : HasError ? "Steam connected. Card status could not be loaded." : "Steam connected. Refresh the library to check for drops."
            : status;
        private set => Set(ref status, value);
    }
    public string ProfileName => profile?.Name ?? "Steam account";
    public string AppVersion => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}  /  WINDOWS";
    public string? AvatarUrl => profile?.AvatarUrl;
    public bool Connected => session.IsLoggedOn;
    public bool Disconnected => !Connected;
    public bool CanSignOut => Connected || Recovering;
    public ImageSource? QrImage { get => qrImage; private set => Set(ref qrImage, value); }
    public bool HasQr => QrImage is not null;
    public string EligibleGames => snapshot?.UpdatedAt is null ? "—" : Games.Count(g => g.Remaining > 0).ToString();
    public string CardsRemaining => snapshot?.UpdatedAt is null ? "—" : Games.Sum(g => g.Remaining).ToString();
    public string CardsReceived => (snapshot?.Received ?? 0).ToString();
    public bool IsEmpty => GamesView.IsEmpty;
    private LibraryEmptyState EmptyState => LibraryEmptyState.Describe(Connected, Busy, HasError, snapshot?.UpdatedAt is not null, Games.Count > 0);
    public string EmptyTitle => EmptyState.Title;
    public string EmptyDescription => EmptyState.Description;
    public string SelectionText => $"{Games.Count(g => g.Selected)} selected";
    public bool CanSelectAll => GamesView.Cast<GameViewModel>().Any(g => g.CanSelect);
    public bool? AllVisibleGamesSelected
    {
        get
        {
            var visible = GamesView.Cast<GameViewModel>().Where(g => g.CanSelect).ToArray();
            int selected = visible.Count(g => g.Selected);
            return selected == 0 ? false : selected == visible.Length ? true : null;
        }
    }
    public GameViewModel? ActiveGame => GamesView.Cast<GameViewModel>().FirstOrDefault(g => g.Active);
    public string LastUpdated => snapshot?.UpdatedAt is { } time ? $"Updated {time:HH:mm} · checks every 3 min while farming" : "Library has not been refreshed";
    public string Duration
    {
        get
        {
            var span = snapshot?.Duration ?? TimeSpan.Zero;
            if (snapshot?.State == FarmState.Farming) span += DateTimeOffset.Now - snapshotAt;
            return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }
    public bool Farming => snapshot?.State == FarmState.Farming;
    public bool Recovering => snapshot?.State == FarmState.Reconnecting;
    public string StateLabel => snapshot?.State switch { FarmState.Farming => "FARMING", FarmState.Reconnecting => "RECONNECTING", FarmState.Paused => "PAUSED", FarmState.Completed => "COMPLETE", FarmState.Faulted => "NEEDS ATTENTION", _ => "STANDBY" };
    public bool StartMinimized { get => settings.StartMinimized; set { settings = settings with { StartMinimized = value }; SaveSettings(); Raise(); } }
    public bool MinimizeToTray { get => settings.MinimizeToTray; set { settings = settings with { MinimizeToTray = value }; SaveSettings(); Raise(); } }
    public bool AutomaticQueue { get => settings.AutomaticQueue; set { settings = settings with { AutomaticQueue = value }; SaveSettings(); Raise(); } }
    public bool WaitForEligiblePlaytime { get => settings.WaitForEligiblePlaytime; set { settings = settings with { WaitForEligiblePlaytime = value }; SaveSettings(); Raise(); } }

    public MainViewModel(LocalStorage storage, Dispatcher dispatcher)
    {
        this.storage = storage; this.dispatcher = dispatcher;
        settings = storage.LoadSettings();
        session = new(storage); library = new(session); engine = new(session, library);
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = item => item is GameViewModel g && (ShowCompleted || g.Remaining > 0) && (string.IsNullOrWhiteSpace(Search) || g.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString().Contains(Search));
        ApplySort();
        ConnectCommand = new(() => RunAsync(() => SignInAsync(false)), () => !Busy && !Connected && !Recovering);
        NewQrCommand = new(() => RunAsync(() => SignInAsync(true)), () => !Busy && !Connected && !Recovering);
        LogoutCommand = new(() => RunAsync(async () => { await engine.StopAsync(lifetime.Token); await session.DisconnectAsync(true, lifetime.Token); await engine.ResetAsync(lifetime.Token); UpdateProfile(null); }), () => !Busy && CanSignOut);
        RefreshCommand = new(() => RunAsync(() => engine.RefreshAsync(WorkToken)), () => !Busy && Connected && !Recovering);
        StartCommand = new(() => RunAsync(async () =>
        {
            // Revalidate ownership and card counts immediately before reporting games.
            await engine.RefreshAsync(WorkToken);
            await engine.StartAsync(Games.Where(g => g.Selected).Select(g => g.AppId).ToArray(), Together, AutomaticQueue, WaitForEligiblePlaytime, WorkToken);
        }), () => !Busy && Connected && !Farming && !Recovering && Games.Any(g => g.Selected));
        PauseCommand = new(() => InterruptAsync(false), () => Farming || Recovering);
        ResumeCommand = new(() => RunAsync(() => engine.ResumeAsync(WorkToken)), () => !Busy && Connected && !Recovering && snapshot?.State == FarmState.Paused);
        StopCommand = new(() => InterruptAsync(true), () => Busy && Connected || snapshot?.State is FarmState.Farming or FarmState.Paused or FarmState.Faulted or FarmState.Reconnecting);
        CancelSignInCommand = new(() => signIn?.Cancel());
        SettingsCommand = new(() => SettingsOpen = !SettingsOpen);
        DismissErrorCommand = new(() => Error = "");
        SelectAllCommand = new(() =>
        {
            var visible = GamesView.Cast<GameViewModel>().Where(g => g.CanSelect).ToArray();
            bool select = visible.Any(g => !g.Selected);
            foreach (var g in visible) g.Selected = select;
        }, () => CanSelectAll);
        session.ProfileChanged += p => OnUi(() => UpdateProfile(p));
        session.ConnectionChanged += s => OnUi(() => { Connection = s; Raise(nameof(Connected)); Raise(nameof(Disconnected)); Raise(nameof(CanSignOut)); RaiseLibraryStatus(); NotifyCommands(); });
        session.QrChallengeChanged += challenge => OnUi(() =>
        {
            if (!SigningIn) return;
            using var data = QRCodeGenerator.GenerateQrCode(challenge, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data);
            using var stream = new MemoryStream(png.GetGraphic(8));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            QrImage = bitmap; Raise(nameof(HasQr));
        });
        engine.Changed += value => OnUi(() => ApplySnapshot(value));
        clock.Tick += (_, _) => Raise(nameof(Duration)); clock.Start();
    }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        if (Busy || Connected || lifetime.IsCancellationRequested || !storage.HasSavedSession) return;
        await RunAsync(() => SignInAsync(false, savedSessionOnly: true));
    }

    private async Task SignInAsync(bool fresh, bool savedSessionOnly = false)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(WorkToken);
        signIn = operation; SigningIn = !savedSessionOnly; QrImage = null; Raise(nameof(HasQr));
        try
        {
            await session.ConnectAsync(fresh, operation.Token, savedSessionOnly);
            library.ResetAfterSignIn();
            SigningIn = false;
            UpdateProfile(session.Profile);
            await engine.RefreshAsync(operation.Token);
        }
        finally { SigningIn = false; QrImage = null; Raise(nameof(HasQr)); signIn = null; }
    }

    private void UpdateProfile(SteamProfile? value)
    {
        profile = value;
        Raise(nameof(ProfileName)); Raise(nameof(AvatarUrl)); Raise(nameof(Connected)); Raise(nameof(Disconnected)); Raise(nameof(CanSignOut)); RaiseLibraryStatus(); NotifyCommands();
    }
    internal void ApplySnapshot(FarmSnapshot value)
    {
        bool wasRecovering = Recovering;
        uint? previousActive = ActiveGame?.AppId;
        snapshot = value; snapshotAt = DateTimeOffset.Now; Status = value.Message;
        foreach (var p in value.Games)
        {
            var existing = Games.FirstOrDefault(g => g.AppId == p.Game.AppId);
            if (existing is null) Games.Add(new(p, SelectionChanged)); else existing.Update(p);
        }
        foreach (var old in Games.Where(g => !value.Games.Any(p => p.Game.AppId == g.AppId)).ToArray()) Games.Remove(old);
        GamesView.Refresh();
        SelectionChanged();
        if (ActiveGame?.AppId != previousActive) Raise(nameof(ActiveGame));
        foreach (string name in new[] { nameof(EligibleGames), nameof(CardsRemaining), nameof(CardsReceived), nameof(LastUpdated), nameof(Duration), nameof(StateLabel), nameof(Farming), nameof(Recovering), nameof(CanSignOut), nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyDescription), nameof(SelectionText), nameof(Status) }) Raise(name);
        if (Recovering || wasRecovering) Error = "";
        if (value.State == FarmState.Faulted) Error = value.Message;
        NotifyCommands();
    }
    internal void SelectionChanged()
    {
        Raise(nameof(SelectionText)); Raise(nameof(AllVisibleGamesSelected)); Raise(nameof(CanSelectAll));
        StartCommand?.Notify(); SelectAllCommand?.Notify();
    }
    private void RaiseLibraryStatus() { Raise(nameof(Status)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); }
    private void ApplySort()
    {
        using var refresh = GamesView.DeferRefresh();
        GamesView.SortDescriptions.Clear();
        GamesView.SortDescriptions.Add(new(nameof(GameViewModel.Active), ListSortDirection.Descending));
        GamesView.SortDescriptions.Add(Sort switch
        {
            "Cards remaining" => new(nameof(GameViewModel.Remaining), ListSortDirection.Descending),
            "Playtime" => new(nameof(GameViewModel.HoursPlayed), ListSortDirection.Ascending),
            _ => new(nameof(GameViewModel.Name), ListSortDirection.Ascending)
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        currentOperation = operation;
        Busy = true; Error = "";
        try { await action(); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested && !(Recovering && NetworkFailures.IsTransient(ex))) Error = SafeErrors.Describe(ex); }
        finally { currentOperation = null; Busy = false; }
    }
    private async Task InterruptAsync(bool stop)
    {
        currentOperation?.Cancel();
        try { if (stop) await engine.StopAsync(lifetime.Token); else await engine.PauseAsync(lifetime.Token); }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) Error = SafeErrors.Describe(ex); }
    }
    private void SaveSettings()
    {
        try { storage.SaveSettings(settings); }
        catch { Error = "Settings could not be saved. Check access to your local application data folder."; }
    }
    private void OnUi(Action action) { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action); }
    private void NotifyCommands()
    {
        foreach (var command in new[] { ConnectCommand, NewQrCommand, LogoutCommand, RefreshCommand, StartCommand, PauseCommand, ResumeCommand, StopCommand }) command?.Notify();
    }
    public async ValueTask DisposeAsync()
    {
        clock.Stop(); lifetime.Cancel();
        // Let a cancelled login release the session gate before orderly shutdown.
        await engine.DisposeAsync();
        await session.DisposeAsync(); library.Dispose(); lifetime.Dispose();
    }
}
