using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using CardGrinder.Core;
using CardGrinder.Infrastructure;

namespace CardGrinder.ViewModels;

public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    private readonly AppUpdateService service = new(UpdateChannel.LoadBundled(), typeof(UpdatesViewModel).Assembly.GetName().Version!);
    private readonly Func<Task> prepareInstall;
    private readonly Func<bool> canInstall;
    private CancellationTokenSource? operation;
    private UpdateRelease? release;
    private UpdateDownload? download;
    private bool busy;
    private string status = "Check for a newer version on impatus.no.";
    private double progress;
    public string CurrentVersion => $"Version {service.CurrentVersion.ToString(3)} · {(Installed ? "Installed" : "Portable")}";
    public bool Installed { get; } = IsInstalled();
    public bool Busy { get => busy; private set { Set(ref busy, value); NotifyCommands(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool Available => release is not null && download is null;
    public bool Ready => download is not null;
    public string Notes => release is null ? "" : $"Version {release.Version} · {release.PublishedAt:yyyy-MM-dd}\n{release.Notes}";
    public string InstallLabel => Installed ? "Install and close CardGrinder" : "Open update folder";
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }

    public UpdatesViewModel(Func<Task> prepareInstall, Func<bool> canInstall)
    {
        this.prepareInstall = prepareInstall;
        this.canInstall = canInstall;
        CheckCommand = new(() => RunAsync(async token =>
        {
            release = null; download = null; RaiseState();
            Status = "Checking for updates…";
            release = await service.CheckAsync(token);
            Status = release is null ? "You're up to date." : $"CardGrinder {release.Version} is available.";
            RaiseState();
        }), () => !Busy);
        DownloadCommand = new(() => RunAsync(async token =>
        {
            Status = "Downloading and verifying update…"; Progress = 0;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CardGrinder", "updates");
            download = await service.DownloadAsync(release!, Installed, root, new Progress<double>(value => Progress = value), token);
            Status = Installed ? "Update verified. Installation will stop farming and close CardGrinder. Your saved login and settings are kept."
                : "ZIP verified. Open its folder, close CardGrinder and extract the update over your portable copy. Your saved login and settings are kept.";
            RaiseState();
        }), () => !Busy && Available);
        InstallCommand = new(() => RunAsync(async token =>
        {
            await AppUpdateService.VerifyDownloadAsync(download!, token);
            if (!Installed)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetDirectoryName(download!.Path)! }, UseShellExecute = true });
                return;
            }
            await prepareInstall();
            // Show the normal installer wizard, preserving the current installation directory.
            var start = new ProcessStartInfo(download!.Path) { UseShellExecute = true };
            start.ArgumentList.Add("/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            Process.Start(start);
            await ((App)System.Windows.Application.Current).ShutDownSafelyAsync();
        }), () => !Busy && Ready && (!Installed || canInstall()));
        CancelCommand = new(() => operation?.Cancel(), () => Busy);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource(); operation = cancellation; Busy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = cancellation.IsCancellationRequested ? "Update operation cancelled." : "The update request timed out. Please try again."; }
        catch { Status = "The update could not be verified or opened. Check your connection and try again."; }
        finally { operation = null; Busy = false; }
    }
    private void RaiseState() { Raise(nameof(Available)); Raise(nameof(Ready)); Raise(nameof(Notes)); NotifyCommands(); }
    public void NotifyCommands() { CheckCommand?.Notify(); DownloadCommand?.Notify(); InstallCommand?.Notify(); CancelCommand?.Notify(); }
    private static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D7F46D04-CF21-47D4-B864-9498DC9E0FA2}_is1");
        return key?.GetValue("InstallLocation") is string directory && string.Equals(directory.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { operation?.Cancel(); service.Dispose(); }
}
