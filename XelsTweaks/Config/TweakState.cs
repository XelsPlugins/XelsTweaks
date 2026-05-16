using System.Collections.Generic;

namespace XelsTweaks.Config;

public sealed class TweakState
{
    public bool Enabled { get; set; }

    public Dictionary<string, string> Settings { get; set; } = [];

    public void Clamp()
    {
        this.Settings ??= [];
    }
}

