using System;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;

namespace XelsTweaks.Tweaks.Interface;

internal sealed class KeepTryOnOpenTweak : TweakBase
{
    public const string TweakId = "interface.keepTryOnOpen";

    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(750);

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
        this.listenersRegistered = true;
    }

    protected override void OnDisable()
    {
        if (!this.listenersRegistered)
        {
            return;
        }

        this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreHide, AgentId.Tryon, this.OnTryonPreHide);
        this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreHide, AgentId.Inspect, this.OnInspectPreHide);
        this.suppressTryonHideUntil = DateTimeOffset.MinValue;
        this.listenersRegistered = false;
    }

    private void OnInspectPreHide(AgentEvent eventType, AgentArgs args)
    {
        this.suppressTryonHideUntil = DateTimeOffset.UtcNow + SuppressionWindow;
    }

    private void OnTryonPreHide(AgentEvent eventType, AgentArgs args)
    {
        if (DateTimeOffset.UtcNow > this.suppressTryonHideUntil)
        {
            return;
        }

        args.PreventOriginal();
        this.suppressTryonHideUntil = DateTimeOffset.MinValue;
        this.Services.Log.Debug("Suppressed Fitting Room hide requested while Character Inspect was closing.");
    }
}
