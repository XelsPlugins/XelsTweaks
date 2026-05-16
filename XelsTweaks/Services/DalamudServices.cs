using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsTweaks.Services;

internal sealed class DalamudServices(
    IDalamudPluginInterface pluginInterface,
    ICommandManager commandManager,
    IPluginLog log,
    IChatGui chatGui,
    IFramework framework,
    IClientState clientState,
    ICondition condition,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IDataManager dataManager,
    ITextureProvider textureProvider)
{
    public IDalamudPluginInterface PluginInterface { get; } = pluginInterface;
    public ICommandManager CommandManager { get; } = commandManager;
    public IPluginLog Log { get; } = log;
    public IChatGui ChatGui { get; } = chatGui;
    public IFramework Framework { get; } = framework;
    public IClientState ClientState { get; } = clientState;
    public ICondition Condition { get; } = condition;
    public IObjectTable ObjectTable { get; } = objectTable;
    public ITargetManager TargetManager { get; } = targetManager;
    public IDataManager DataManager { get; } = dataManager;
    public ITextureProvider TextureProvider { get; } = textureProvider;
}

