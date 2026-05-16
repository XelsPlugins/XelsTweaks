using System.Collections.Generic;
using Dalamud.Configuration;

namespace XelsTweaks.Config;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Dictionary<string, TweakState> Tweaks { get; set; } = [];

    public void Migrate()
    {
        if (this.Version < 1)
        {
            this.Version = 1;
        }
    }

    public void Clamp()
    {
        this.Tweaks ??= [];

        foreach (var state in this.Tweaks.Values)
        {
            state.Clamp();
        }
    }

    public TweakState GetOrCreateTweakState(string id)
    {
        this.Tweaks ??= [];

        if (!this.Tweaks.TryGetValue(id, out var state))
        {
            state = new TweakState();
            this.Tweaks[id] = state;
        }

        state.Clamp();
        return state;
    }
}

