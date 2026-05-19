using System.Collections.Generic;

namespace XelsTweaks.Tweaks.MenuControl;

internal sealed record TweakMenuDescriptor(
    string Id,
    string Name,
    string Description,
    TweakMenuSnapshot Snapshot,
    IReadOnlyList<TweakMenuAction> Actions);
