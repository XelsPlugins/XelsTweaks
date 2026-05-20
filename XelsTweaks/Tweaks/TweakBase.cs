using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    public virtual IReadOnlyList<TweakOptionDefinition> Options => [];
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

    public bool TryGetOptionValue(string optionId, out TweakOptionValue optionValue)
    {
        optionValue = null!;
        if (!this.TryGetOption(optionId, out var option))
        {
            return false;
        }

        var storedValue = this.GetOptionStoredValue(option);
        optionValue = new TweakOptionValue(option, FormatOptionValue(option, storedValue), storedValue);
        return true;
    }

    public bool TrySetOptionValue(string optionId, string value, out string message)
    {
        message = string.Empty;
        if (!this.TryGetOption(optionId, out var option))
        {
            message = $"Unknown option for {this.Name}: {optionId}";
            return false;
        }

        if (!TryNormalizeOptionValue(option, value, out var storedValue, out var error))
        {
            message = error;
            return false;
        }

        var currentValue = this.GetOptionStoredValue(option);
        if (string.Equals(currentValue, storedValue, StringComparison.Ordinal))
        {
            message = $"{this.Name}: {option.Label} is already {FormatOptionValue(option, storedValue)}.";
            return true;
        }

        this.State.Settings[option.Id] = storedValue;
        this.saveConfig();
        this.OnOptionChanged(option);
        message = $"{this.Name}: {option.Label} is {FormatOptionValue(option, storedValue)}.";
        return true;
    }

    public bool TryToggleOptionValue(string optionId, out string message)
    {
        message = string.Empty;
        if (!this.TryGetOption(optionId, out var option))
        {
            message = $"Unknown option for {this.Name}: {optionId}";
            return false;
        }

        if (option.Kind != TweakOptionKind.Boolean)
        {
            message = $"{option.Label} is not a toggle option.";
            return false;
        }

        var current = ParseBool(this.GetOptionStoredValue(option), false);
        return this.TrySetOptionValue(option.Id, current ? "off" : "on", out message);
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

    protected virtual void OnOptionChanged(TweakOptionDefinition option)
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

    private bool TryGetOption(string optionId, out TweakOptionDefinition option)
    {
        option = this.Options.FirstOrDefault(option => option.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))!;
        return option != null;
    }

    private string GetOptionStoredValue(TweakOptionDefinition option)
    {
        return this.State.Settings.TryGetValue(option.Id, out var value)
            ? value
            : option.DefaultValue;
    }

    private static bool TryNormalizeOptionValue(
        TweakOptionDefinition option,
        string value,
        out string storedValue,
        out string error)
    {
        storedValue = string.Empty;
        error = string.Empty;

        switch (option.Kind)
        {
            case TweakOptionKind.Boolean:
                if (!TryParseBool(value, out var boolValue))
                {
                    error = $"{option.Label} expects on/off, true/false, yes/no, or 1/0.";
                    return false;
                }

                storedValue = boolValue.ToString(CultureInfo.InvariantCulture);
                return true;
            case TweakOptionKind.Choice:
                var choice = option.Choices.FirstOrDefault(choice =>
                    choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)
                    || choice.Label.Equals(value, StringComparison.OrdinalIgnoreCase)
                    || choice.StoredValue.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (choice == null)
                {
                    var choices = string.Join(", ", option.Choices.Select(choice => choice.Value));
                    error = $"{option.Label} expects one of: {choices}.";
                    return false;
                }

                storedValue = choice.StoredValue;
                return true;
            case TweakOptionKind.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    error = $"{option.Label} expects a whole number.";
                    return false;
                }

                storedValue = intValue.ToString(CultureInfo.InvariantCulture);
                return true;
            case TweakOptionKind.Text:
                storedValue = value;
                return true;
            default:
                error = $"{option.Label} cannot be changed from chat commands.";
                return false;
        }
    }

    private static string FormatOptionValue(TweakOptionDefinition option, string storedValue)
    {
        return option.Kind switch
        {
            TweakOptionKind.Boolean => ParseBool(storedValue, ParseBool(option.DefaultValue, false)) ? "on" : "off",
            TweakOptionKind.Choice => option.Choices.FirstOrDefault(choice => choice.StoredValue.Equals(storedValue, StringComparison.OrdinalIgnoreCase))?.Value ?? storedValue,
            _ => storedValue
        };
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
            case "enable":
            case "enabled":
                result = true;
                return true;
            case "0":
            case "false":
            case "off":
            case "no":
            case "disable":
            case "disabled":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        return TryParseBool(value, out var result) ? result : defaultValue;
    }
}
