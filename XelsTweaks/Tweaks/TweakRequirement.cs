using System;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using XelsTweaks.Services;

namespace XelsTweaks.Tweaks;

internal sealed record TweakRequirement(string PluginInternalName, string PluginName, string RepositoryUrl)
{
    public bool IsMet(DalamudServices services)
    {
        return services.PluginInterface.InstalledPlugins.Any(this.IsSatisfiedBy);
    }

    private bool IsSatisfiedBy(IExposedPlugin plugin)
    {
        return plugin.InternalName.Equals(this.PluginInternalName, StringComparison.OrdinalIgnoreCase)
            && !plugin.IsOutdated
            && !plugin.IsBanned
            && !plugin.IsDecommissioned
            && IsLoadAllowed(plugin)
            && !IsDisabled(plugin);
    }

    private static bool IsLoadAllowed(IExposedPlugin plugin)
    {
        return !TryGetBooleanProperty(GetLocalPlugin(plugin), "ApplicableForLoad", out var applicableForLoad)
            || applicableForLoad;
    }

    private static bool IsDisabled(IExposedPlugin plugin)
    {
        var localPlugin = GetLocalPlugin(plugin);
        return IsBooleanPropertyTrue(plugin, "Disabled")
            || IsBooleanPropertyTrue(plugin.Manifest, "Disabled")
            || IsBooleanPropertyTrue(localPlugin, "Disabled")
            || IsBooleanPropertyTrue(GetPropertyValue(localPlugin, "Manifest"), "Disabled");
    }

    private static object? GetLocalPlugin(IExposedPlugin plugin)
    {
        return GetPropertyValue(plugin, "LocalPlugin");
    }

    private static bool IsBooleanPropertyTrue(object? instance, string propertyName)
    {
        return TryGetBooleanProperty(instance, propertyName, out var value) && value;
    }

    private static bool TryGetBooleanProperty(object? instance, string propertyName, out bool value)
    {
        value = false;
        var propertyValue = GetPropertyValue(instance, propertyName);
        if (propertyValue is not bool boolValue)
        {
            return false;
        }

        value = boolValue;
        return true;
    }

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        try
        {
            return instance?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
