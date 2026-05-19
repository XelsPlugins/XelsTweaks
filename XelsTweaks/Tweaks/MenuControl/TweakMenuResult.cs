namespace XelsTweaks.Tweaks.MenuControl;

internal sealed record TweakMenuResult(
    bool Success,
    string Message,
    TweakMenuSnapshot Snapshot);
