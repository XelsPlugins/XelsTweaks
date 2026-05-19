using System;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Ipc;
using XelsTweaks.Tweaks.MenuControl;

namespace XelsTweaks.Services;

internal sealed class TweakMenuIpcService : IDisposable
{
    public const string ListChannel = "XelsTweaks.Menu.List";
    public const string GetStatusChannel = "XelsTweaks.Menu.GetStatus";
    public const string GetActionsChannel = "XelsTweaks.Menu.GetActions";
    public const string ExecuteActionChannel = "XelsTweaks.Menu.ExecuteAction";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TweakManager tweakManager;
    private readonly ICallGateProvider<string> listProvider;
    private readonly ICallGateProvider<string, string> getStatusProvider;
    private readonly ICallGateProvider<string, string> getActionsProvider;
    private readonly ICallGateProvider<string, string, string> executeActionProvider;

    public TweakMenuIpcService(DalamudServices services, TweakManager tweakManager)
    {
        this.tweakManager = tweakManager;
        this.listProvider = services.PluginInterface.GetIpcProvider<string>(ListChannel);
        this.getStatusProvider = services.PluginInterface.GetIpcProvider<string, string>(GetStatusChannel);
        this.getActionsProvider = services.PluginInterface.GetIpcProvider<string, string>(GetActionsChannel);
        this.executeActionProvider = services.PluginInterface.GetIpcProvider<string, string, string>(ExecuteActionChannel);

        this.listProvider.RegisterFunc(this.GetMenuListJson);
        this.getStatusProvider.RegisterFunc(this.GetStatusJson);
        this.getActionsProvider.RegisterFunc(this.GetActionsJson);
        this.executeActionProvider.RegisterFunc(this.ExecuteActionJson);
    }

    public void Dispose()
    {
        this.executeActionProvider.UnregisterFunc();
        this.getActionsProvider.UnregisterFunc();
        this.getStatusProvider.UnregisterFunc();
        this.listProvider.UnregisterFunc();
    }

    private string GetMenuListJson()
    {
        var descriptors = this.tweakManager.ControllableMenus
            .Select(this.CreateDescriptor)
            .ToArray();
        return JsonSerializer.Serialize(descriptors, JsonOptions);
    }

    private string GetStatusJson(string menuId)
    {
        var menu = this.tweakManager.FindMenuById(menuId);
        if (menu == null)
        {
            return JsonSerializer.Serialize(CreateFailure($"Unknown controllable menu: {menuId}"), JsonOptions);
        }

        return JsonSerializer.Serialize(menu.GetMenuSnapshot(), JsonOptions);
    }

    private string GetActionsJson(string menuId)
    {
        var menu = this.tweakManager.FindMenuById(menuId);
        if (menu == null)
        {
            return JsonSerializer.Serialize(CreateFailure($"Unknown controllable menu: {menuId}"), JsonOptions);
        }

        return JsonSerializer.Serialize(menu.GetMenuActions(), JsonOptions);
    }

    private string ExecuteActionJson(string menuId, string action)
    {
        var result = this.ExecuteAction(menuId, action);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private TweakMenuResult ExecuteAction(string menuId, string action)
    {
        var menu = this.tweakManager.FindMenuById(menuId);
        return menu == null
            ? CreateFailure($"Unknown controllable menu: {menuId}")
            : menu.ExecuteMenuAction(action);
    }

    private TweakMenuDescriptor CreateDescriptor(IControllableTweakMenu menu)
    {
        var tweak = (TweakBase)menu;
        return new TweakMenuDescriptor(
            menu.MenuId,
            tweak.Name,
            tweak.Description,
            menu.GetMenuSnapshot(),
            menu.GetMenuActions());
    }

    private static TweakMenuResult CreateFailure(string message)
    {
        return new TweakMenuResult(
            false,
            message,
            new TweakMenuSnapshot("Unknown", message, false, false, false, 0, 0, 0, null, message));
    }
}
