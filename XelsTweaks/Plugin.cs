using System;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XelsTweaks.Tweaks.MenuControl;

namespace XelsTweaks;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/xelstweaks";
    private const string ShortCommandName = "/xt";
    private const string ChatPrefix = "[XelsTweaks]";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IPartyList PartyList { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IAgentLifecycle AgentLifecycle { get; set; } = null!;
    [PluginService] private static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] private static IGameInventory GameInventory { get; set; } = null!;
    [PluginService] private static ISigScanner SigScanner { get; set; } = null!;

    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly TweakManager tweakManager;
    private readonly TweakMenuIpcService tweakMenuIpcService;
    private readonly WindowSystem windowSystem = new("XelsTweaks");
    private readonly ConfigWindow configWindow;
    private readonly SndDocumentationWindow sndDocumentationWindow;

    public Plugin()
    {
        this.services = new DalamudServices(
            PluginInterface,
            CommandManager,
            Log,
            ChatGui,
            Framework,
            ClientState,
            Condition,
            ObjectTable,
            PartyList,
            TargetManager,
            DataManager,
            TextureProvider,
            GameGui,
            ContextMenu,
            AgentLifecycle,
            AddonLifecycle,
            GameInventory,
            SigScanner);

        this.config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Migrate();
        this.config.Clamp();

        this.tweakManager = new TweakManager(this.config, this.services, this.SaveConfig);
        this.tweakManager.Initialize();
        this.tweakMenuIpcService = new TweakMenuIpcService(this.services, this.tweakManager);

        this.configWindow = new ConfigWindow(
            this.tweakManager,
            TextureProvider,
            Path.Combine(PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "logo.png"));
        this.windowSystem.AddWindow(this.configWindow);
        this.sndDocumentationWindow = new SndDocumentationWindow(this.tweakManager);
        this.windowSystem.AddWindow(this.sndDocumentationWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open XelsTweaks. Usage: /xelstweaks or /xt [list|on <id>|off <id>|toggle <id>|menu|snd|docs snd]"
        });
        CommandManager.AddHandler(ShortCommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open XelsTweaks. Usage: /xt or /xelstweaks [list|on <id>|off <id>|toggle <id>|menu|snd|docs snd]"
        });

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
        this.windowSystem.RemoveAllWindows();
        this.tweakMenuIpcService.Dispose();
        this.sndDocumentationWindow.Dispose();
        this.configWindow.Dispose();
        this.tweakManager.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length == 0 || args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            this.OpenConfig();
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                this.PrintTweakList();
                break;
            case "on":
            case "enable":
                this.SetTweakFromCommand(args, true);
                break;
            case "off":
            case "disable":
                this.SetTweakFromCommand(args, false);
                break;
            case "toggle":
                this.ToggleTweakFromCommand(args);
                break;
            case "menu":
                this.HandleMenuCommand(args);
                break;
            case "snd":
                this.OpenSndDocumentation();
                break;
            case "docs" when args.Length > 1 && args[1].Equals("snd", StringComparison.OrdinalIgnoreCase):
                this.OpenSndDocumentation();
                break;
            default:
                this.Print("Usage: /xelstweaks or /xt [list|on <id>|off <id>|toggle <id>|menu|snd|docs snd]");
                break;
        }
    }

    private void PrintTweakList()
    {
        foreach (var tweak in this.tweakManager.Tweaks.OrderBy(tweak => tweak.Category).ThenBy(tweak => tweak.Name))
        {
            var state = tweak.IsEnabled ? "on" : "off";
            this.Print($"{state} - {tweak.Id} - {tweak.Name}");
        }
    }

    private void SetTweakFromCommand(string[] args, bool enabled)
    {
        if (!this.TryGetCommandTweak(args, out var tweak))
        {
            return;
        }

        if (enabled && !tweak.IsRequirementMet && tweak.Requirement is { } requirement)
        {
            this.Print($"{tweak.Name} needs {requirement.PluginName}.");
            return;
        }

        this.tweakManager.SetEnabled(tweak, enabled);
        this.Print($"{tweak.Name} is {(tweak.IsEnabled ? "on" : "off")}.");
    }

    private void ToggleTweakFromCommand(string[] args)
    {
        if (!this.TryGetCommandTweak(args, out var tweak))
        {
            return;
        }

        this.tweakManager.SetEnabled(tweak, !tweak.IsEnabled);
        this.Print($"{tweak.Name} is {(tweak.IsEnabled ? "on" : "off")}.");
    }

    private bool TryGetCommandTweak(string[] args, out TweakBase tweak)
    {
        tweak = null!;
        if (args.Length < 2)
        {
            this.Print("Choose a tweak ID from /xelstweaks list or /xt list.");
            return false;
        }

        var id = args[1];
        var found = this.tweakManager.FindById(id);
        if (found == null)
        {
            this.Print($"Unknown tweak ID: {id}");
            return false;
        }

        tweak = found;
        return true;
    }

    private void HandleMenuCommand(string[] args)
    {
        if (args.Length < 2 || args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            this.PrintMenuList();
            return;
        }

        if (args.Length < 3)
        {
            this.Print("Usage: /xt menu <tweakId> [status|actions|action]");
            return;
        }

        var menuId = args[1];
        var menu = this.tweakManager.FindMenuById(menuId);
        if (menu == null)
        {
            this.Print($"Unknown controllable menu: {menuId}");
            return;
        }

        var action = args[2];
        if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            this.PrintMenuStatus(menu);
            return;
        }

        if (action.Equals("actions", StringComparison.OrdinalIgnoreCase))
        {
            this.PrintMenuActions(menu);
            return;
        }

        var result = menu.ExecuteMenuAction(action);
        this.Print(result.Message);
    }

    private void PrintMenuList()
    {
        var menus = this.tweakManager.ControllableMenus
            .OrderBy(menu => ((TweakBase)menu).Name)
            .ToArray();
        if (menus.Length == 0)
        {
            this.Print("No controllable menus are registered.");
            return;
        }

        foreach (var menu in menus)
        {
            var tweak = (TweakBase)menu;
            this.Print($"{menu.MenuId} - {tweak.Name}");
        }
    }

    private void PrintMenuStatus(IControllableTweakMenu menu)
    {
        var snapshot = menu.GetMenuSnapshot();
        var progress = snapshot.Total > 0
            ? $" Progress: {snapshot.Completed}/{snapshot.Total}."
            : string.Empty;
        var current = string.IsNullOrWhiteSpace(snapshot.CurrentItem)
            ? string.Empty
            : $" Current: {snapshot.CurrentItem}.";
        var error = string.IsNullOrWhiteSpace(snapshot.Error)
            ? string.Empty
            : $" Error: {snapshot.Error}.";
        this.Print($"{menu.MenuId}: {snapshot.State}. {snapshot.Status}{progress}{current}{error}");
    }

    private void PrintMenuActions(IControllableTweakMenu menu)
    {
        foreach (var action in menu.GetMenuActions())
        {
            var availability = action.Available
                ? "available"
                : $"unavailable: {action.DisabledReason}";
            this.Print($"{menu.MenuId} {action.Id} - {availability} - {action.Description}");
        }
    }

    private void OpenConfig()
    {
        this.configWindow.IsOpen = true;
    }

    private void OpenSndDocumentation()
    {
        this.sndDocumentationWindow.IsOpen = true;
    }

    private void SaveConfig()
    {
        this.config.Clamp();
        PluginInterface.SavePluginConfig(this.config);
    }

    private void Print(string message)
    {
        ChatGui.Print($"{ChatPrefix} {message}");
    }
}
