using System;
using System.Collections.Generic;
using System.Globalization;

namespace XelsTweaks.Tweaks;

internal enum TweakOptionKind
{
    Boolean,
    Choice,
    Integer,
    Text
}

internal sealed record TweakOptionChoice(string Value, string Label, string StoredValue)
{
    public TweakOptionChoice(string value, string label)
        : this(value, label, value)
    {
    }
}

internal sealed record TweakOptionDefinition(
    string Id,
    string Label,
    string Description,
    TweakOptionKind Kind,
    string DefaultValue,
    IReadOnlyList<TweakOptionChoice> Choices,
    string Group)
{
    public static TweakOptionDefinition Bool(
        string id,
        string label,
        string description,
        bool defaultValue,
        string group)
    {
        return new TweakOptionDefinition(
            id,
            label,
            description,
            TweakOptionKind.Boolean,
            defaultValue.ToString(CultureInfo.InvariantCulture),
            Array.Empty<TweakOptionChoice>(),
            group);
    }

    public static TweakOptionDefinition Choice(
        string id,
        string label,
        string description,
        string defaultValue,
        IReadOnlyList<TweakOptionChoice> choices,
        string group)
    {
        return new TweakOptionDefinition(
            id,
            label,
            description,
            TweakOptionKind.Choice,
            defaultValue,
            choices,
            group);
    }
}

internal sealed record TweakOptionValue(TweakOptionDefinition Definition, string Value, string StoredValue);
