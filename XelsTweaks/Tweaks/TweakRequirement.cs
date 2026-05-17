using System;
using System.Linq;
using XelsTweaks.Services;

namespace XelsTweaks.Tweaks;

internal sealed record TweakRequirement(string PluginInternalName, string PluginName, string RepositoryUrl)
{
    public bool IsMet(DalamudServices services)
    {
        return services.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName.Equals(this.PluginInternalName, StringComparison.OrdinalIgnoreCase)
            && plugin.IsLoaded
            && !plugin.IsOutdated);
    }
}
