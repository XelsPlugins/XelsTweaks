namespace XelsTweaks.Tweaks.MenuControl;

internal sealed record TweakMenuSnapshot(
    string State,
    string Status,
    bool IsVisible,
    bool IsBusy,
    bool IsPaused,
    int Completed,
    int Total,
    int Skipped,
    string? CurrentItem,
    string? Error);
