using CardGrinder.Core;
using CardGrinder.Infrastructure;

namespace CardGrinder.ViewModels;

public class GameViewModel(GameProgress progress, Action selectionChanged) : ObservableObject
{
    private GameProgress progress = progress;
    private bool selected;
    public uint AppId => progress.Game.AppId;
    public string Name => progress.Game.Name;
    public virtual string? CoverUrl => progress.Game.CoverUrl;
    public int Remaining => progress.Game.Remaining;
    public double HoursPlayed => progress.Game.HoursPlayed;
    public string Details => $"APP {AppId}   ·   {HoursPlayed:0.0} hours played";
    public string ProgressText => progress.Received > 0 ? $"{progress.Received} received this session" : "No drops confirmed this session";
    public double Percent => progress.Percent;
    public string Status => progress.Active ? "Farming" : progress.Completed ? "Complete" : "Ready";
    public bool Active => progress.Active;
    public bool Completed => progress.Completed;
    public bool CanSelect => !Completed;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) selectionChanged(); } }
    public void Update(GameProgress value)
    {
        progress = value;
        if (Completed) Selected = false;
        Raise(string.Empty);
    }
}
