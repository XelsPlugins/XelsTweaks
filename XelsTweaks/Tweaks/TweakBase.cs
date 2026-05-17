using System;
using System.Globalization;

namespace XelsTweaks.Tweaks;

internal abstract class TweakBase : IDisposable
{
    private readonly Action saveConfig;

    protected TweakBase(DalamudServices services, TweakState state, Action saveConfig)
    {
        this.Services = services;
        this.State = state;
        this.saveConfig = saveConfig;
    }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract TweakCategory Category { get; }
    public virtual bool DrawConfigWhenDisabled => false;
    public virtual TweakRequirement? Requirement => null;
    public bool IsEnabled { get; private set; }
    public string? LastError { get; internal set; }
    public bool IsRequirementMet => this.Requirement?.IsMet(this.Services) != false;

    protected DalamudServices Services { get; }
    protected TweakState State { get; }

    public void SetEnabled(bool enabled)
    {
        if (enabled == this.IsEnabled)
        {
            return;
        }

        if (enabled)
        {
            this.OnEnable();
            this.IsEnabled = true;
            this.LastError = null;
            return;
        }

        this.OnDisable();
        this.IsEnabled = false;
    }

    public virtual bool DrawConfig()
    {
        return false;
    }

    public void Dispose()
    {
        if (this.IsEnabled)
        {
            this.SetEnabled(false);
        }

        this.OnDispose();
    }

    protected virtual void OnEnable()
    {
    }

    protected virtual void OnDisable()
    {
    }

    protected virtual void OnDispose()
    {
    }

    protected bool GetBool(string key, bool defaultValue)
    {
        if (!this.State.Settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    protected void SetBool(string key, bool value)
    {
        this.State.Settings[key] = value.ToString(CultureInfo.InvariantCulture);
        this.saveConfig();
    }

    protected int GetInt(string key, int defaultValue)
    {
        if (!this.State.Settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    protected void SetInt(string key, int value)
    {
        this.State.Settings[key] = value.ToString(CultureInfo.InvariantCulture);
        this.saveConfig();
    }

    protected float GetFloat(string key, float defaultValue)
    {
        if (!this.State.Settings.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    protected void SetFloat(string key, float value)
    {
        this.State.Settings[key] = value.ToString(CultureInfo.InvariantCulture);
        this.saveConfig();
    }

    protected string GetString(string key, string defaultValue)
    {
        return this.State.Settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    protected void SetString(string key, string value)
    {
        this.State.Settings[key] = value;
        this.saveConfig();
    }
}
