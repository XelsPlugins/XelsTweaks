using System;
using System.Collections.Generic;
using System.Linq;
using XelsTweaks.Tweaks.Utility;

namespace XelsTweaks.Tweaks;

internal sealed class TweakManager : IDisposable
{
    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly Action saveConfig;
    private readonly List<TweakBase> tweaks = [];

    public TweakManager(Configuration config, DalamudServices services, Action saveConfig)
    {
        this.config = config;
        this.services = services;
        this.saveConfig = saveConfig;
    }

    public IReadOnlyList<TweakBase> Tweaks => this.tweaks;

    public void Initialize()
    {
        this.RegisterTweaks();

        var changed = false;
        foreach (var tweak in this.tweaks)
        {
            if (!this.config.GetOrCreateTweakState(tweak.Id).Enabled)
            {
                continue;
            }

            if (!this.TrySetEnabled(tweak, true))
            {
                changed = true;
            }
        }

        if (changed)
        {
            this.saveConfig();
        }
    }

    public TweakBase? FindById(string id)
    {
        return this.tweaks.FirstOrDefault(tweak => tweak.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetEnabled(TweakBase tweak, bool enabled)
    {
        var changed = this.TrySetEnabled(tweak, enabled);
        this.saveConfig();
        return changed;
    }

    public void Dispose()
    {
        foreach (var tweak in this.tweaks)
        {
            try
            {
                tweak.Dispose();
            }
            catch (Exception ex)
            {
                this.services.Log.Error(ex, "Failed to dispose tweak {Id}", tweak.Id);
            }
        }
    }

    private void RegisterTweaks()
    {
        this.Register(new AutoLoginTweak(
            this.services,
            this.config.GetOrCreateTweakState(AutoLoginTweak.TweakId),
            this.saveConfig));
    }

    private void Register(TweakBase tweak)
    {
        if (this.FindById(tweak.Id) != null)
        {
            throw new InvalidOperationException($"Duplicate tweak ID: {tweak.Id}");
        }

        this.tweaks.Add(tweak);
    }

    private bool TrySetEnabled(TweakBase tweak, bool enabled)
    {
        var previous = tweak.IsEnabled;
        var state = this.config.GetOrCreateTweakState(tweak.Id);

        try
        {
            tweak.SetEnabled(enabled);
            state.Enabled = tweak.IsEnabled;
            return previous != tweak.IsEnabled;
        }
        catch (Exception ex)
        {
            this.services.Log.Error(ex, "Failed to {Action} tweak {Id}", enabled ? "enable" : "disable", tweak.Id);
            tweak.LastError = ex.Message;
            state.Enabled = false;

            try
            {
                tweak.SetEnabled(false);
            }
            catch (Exception disableEx)
            {
                this.services.Log.Error(disableEx, "Failed to clean up tweak {Id} after an error", tweak.Id);
            }

            return previous;
        }
    }
}
