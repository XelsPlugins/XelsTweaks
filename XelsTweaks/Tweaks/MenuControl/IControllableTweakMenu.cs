using System.Collections.Generic;

namespace XelsTweaks.Tweaks.MenuControl;

internal interface IControllableTweakMenu
{
    string MenuId { get; }

    TweakMenuSnapshot GetMenuSnapshot();

    IReadOnlyList<TweakMenuAction> GetMenuActions();

    TweakMenuResult ExecuteMenuAction(string action);
}
