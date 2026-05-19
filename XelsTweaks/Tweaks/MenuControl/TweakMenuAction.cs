namespace XelsTweaks.Tweaks.MenuControl;

internal sealed record TweakMenuAction(
    string Id,
    string Label,
    string Description,
    string Requires,
    bool Available,
    string? DisabledReason);
