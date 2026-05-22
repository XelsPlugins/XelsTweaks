using System;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.ClientState.Conditions;

namespace XelsTweaks.Tweaks.Interface;

internal sealed class KeepTryOnOpenTweak : TweakBase
{
    public const string TweakId = "interface.keepTryOnOpen";

    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(750);
    private static readonly ConditionFlag[] ForcedTryOnCloseConditionFlags =
    [
        ConditionFlag.InCombat,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied39,
    ];

    private DateTimeOffset suppressTryonHideUntil = DateTimeOffset.MinValue;
    private bool listenersRegistered;

    public KeepTryOnOpenTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Keep Fitting Room Open";
    public override string Description => "Keeps the Fitting Room open after closing an inspected player's character window.";
    public override TweakCategory Category => TweakCategory.Interface;

    protected override void OnEnable()
    {
        this.Services.AgentLifecycle.RegisterListener(AgentEvent.PreHide, AgentId.Inspect, this.OnInspectPreHide);
        this.Services.AgentLifecycle.RegisterListener(AgentEvent.PreHide, AgentId.Tryon, this.OnTryonPreHide);
        this.Services.Condition.ConditionChange += this.OnConditionChanged;
        this.listenersRegistered = true;
    }

    protected override void OnDisable()
    {
        if (!this.listenersRegistered)
        {
            return;
        }

        this.Services.Condition.ConditionChange -= this.OnConditionChanged;
        this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreHide, AgentId.Tryon, this.OnTryonPreHide);
        this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreHide, AgentId.Inspect, this.OnInspectPreHide);
        this.suppressTryonHideUntil = DateTimeOffset.MinValue;
        this.listenersRegistered = false;
    }

    private void OnInspectPreHide(AgentEvent eventType, AgentArgs args)
    {
        if (this.IsForcedTryOnCloseConditionActive())
        {
            this.suppressTryonHideUntil = DateTimeOffset.MinValue;
            return;
        }

        this.suppressTryonHideUntil = DateTimeOffset.UtcNow + SuppressionWindow;
    }

    private void OnTryonPreHide(AgentEvent eventType, AgentArgs args)
    {
        if (DateTimeOffset.UtcNow > this.suppressTryonHideUntil)
        {
            return;
        }

        if (this.IsForcedTryOnCloseConditionActive())
        {
            this.suppressTryonHideUntil = DateTimeOffset.MinValue;
            return;
        }

        args.PreventOriginal();
        this.suppressTryonHideUntil = DateTimeOffset.MinValue;
        this.Services.Log.Debug("Suppressed Fitting Room hide requested while Character Inspect was closing.");
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value && IsForcedTryOnCloseConditionFlag(flag))
        {
            this.suppressTryonHideUntil = DateTimeOffset.MinValue;
        }
    }

    private bool IsForcedTryOnCloseConditionActive()
    {
        foreach (var flag in ForcedTryOnCloseConditionFlags)
        {
            if (this.Services.Condition[flag])
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForcedTryOnCloseConditionFlag(ConditionFlag flag)
    {
        foreach (var forcedCloseFlag in ForcedTryOnCloseConditionFlags)
        {
            if (forcedCloseFlag == flag)
            {
                return true;
            }
        }

        return false;
    }
}
