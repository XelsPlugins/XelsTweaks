using System;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class KeepMapOpenDuringNpcInteractionTweak : TweakBase
{
    public const string TweakId = "interface.keepMapOpenDuringNpcInteraction";

    private const string AreaMapAddonName = "AreaMap";
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(750);
    private static readonly AgentId[] InteractionAgentIds =
    [
        AgentId.Shop,
        AgentId.Repair,
        AgentId.RepairRequest,
        AgentId.FreeShop,
        AgentId.ShopExchangeCoin,
        AgentId.InclusionShop,
        AgentId.CollectablesShop,
        AgentId.FreeCompanyCreditShop,
        AgentId.MJIDisposeShop,
    ];
    private static readonly string[] InteractionAddonNames =
    [
        "Talk",
        "_MiniTalk",
        "MiniTalkPlayer",
        "_BattleTalk",
        "SelectYesno",
        "SelectString",
        "SelectIconString",
        "SelectOk",
        "Shop",
        "Repair",
        "RepairRequest",
        "InclusionShop",
        "CollectablesShop",
    ];
    private static readonly ConditionFlag[] InteractionConditionFlags =
    [
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied39,
    ];

    private DateTimeOffset suppressMapHideUntil = DateTimeOffset.MinValue;
    private bool mapVisibilitySnapshotActive;
    private bool mapVisibleBeforeInteraction;
    private bool restoreQueued;
    private bool listenersRegistered;

    public KeepMapOpenDuringNpcInteractionTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Keep Map Open";
    public override string Description => "Keeps the map open when starting NPC dialogue, shops, and repairs.";
    public override TweakCategory Category => TweakCategory.Interface;

    protected override void OnEnable()
    {
        this.Services.AgentLifecycle.RegisterListener(AgentEvent.PreHide, AgentId.Map, this.OnMapAgentPreHide);

        foreach (var agentId in InteractionAgentIds)
        {
            this.Services.AgentLifecycle.RegisterListener(AgentEvent.PreShow, agentId, this.OnInteractionAgentPreShow);
        }

        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreHide, AreaMapAddonName, this.OnMapAddonPreHide);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostHide, AreaMapAddonName, this.OnMapAddonPostHide);

        foreach (var addonName in InteractionAddonNames)
        {
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreOpen, addonName, this.OnInteractionAddonOpening);
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreShow, addonName, this.OnInteractionAddonOpening);
        }

        this.Services.Condition.ConditionChange += this.OnConditionChanged;
        this.listenersRegistered = true;

        if (this.IsAnyInteractionConditionActive())
        {
            this.StartSuppressionWindow();
        }
    }

    protected override void OnDisable()
    {
        if (!this.listenersRegistered)
        {
            return;
        }

        this.Services.Condition.ConditionChange -= this.OnConditionChanged;

        foreach (var addonName in InteractionAddonNames)
        {
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreShow, addonName, this.OnInteractionAddonOpening);
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreOpen, addonName, this.OnInteractionAddonOpening);
        }

        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostHide, AreaMapAddonName, this.OnMapAddonPostHide);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreHide, AreaMapAddonName, this.OnMapAddonPreHide);

        foreach (var agentId in InteractionAgentIds)
        {
            this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreShow, agentId, this.OnInteractionAgentPreShow);
        }

        this.Services.AgentLifecycle.UnregisterListener(AgentEvent.PreHide, AgentId.Map, this.OnMapAgentPreHide);

        this.suppressMapHideUntil = DateTimeOffset.MinValue;
        this.ClearMapVisibilitySnapshot();
        this.restoreQueued = false;
        this.listenersRegistered = false;
    }

    private void OnInteractionAgentPreShow(AgentEvent eventType, AgentArgs args)
    {
        this.StartSuppressionWindow();
    }

    private void OnInteractionAddonOpening(AddonEvent eventType, AddonArgs args)
    {
        this.StartSuppressionWindow();
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value && IsInteractionConditionFlag(flag))
        {
            this.StartSuppressionWindow();
        }
    }

    private void OnMapAgentPreHide(AgentEvent eventType, AgentArgs args)
    {
        if (!this.ShouldSuppressMapHide())
        {
            return;
        }

        args.PreventOriginal();
        this.Services.Log.Debug("Suppressed map agent hide while NPC interaction was starting.");
    }

    private void OnMapAddonPreHide(AddonEvent eventType, AddonArgs args)
    {
        if (!this.ShouldSuppressMapHide())
        {
            return;
        }

        args.PreventOriginal();
        this.Services.Log.Debug("Suppressed AreaMap addon hide while NPC interaction was starting.");
    }

    private void OnMapAddonPostHide(AddonEvent eventType, AddonArgs args)
    {
        if (this.ShouldSuppressMapHide())
        {
            this.QueueMapRestore();
        }
    }

    private bool ShouldSuppressMapHide()
    {
        return this.mapVisibilitySnapshotActive
            && this.mapVisibleBeforeInteraction
            && DateTimeOffset.UtcNow <= this.suppressMapHideUntil;
    }

    private void StartSuppressionWindow()
    {
        if (DateTimeOffset.UtcNow > this.suppressMapHideUntil)
        {
            this.ClearMapVisibilitySnapshot();
        }

        this.CaptureMapVisibility();
        this.suppressMapHideUntil = DateTimeOffset.UtcNow + SuppressionWindow;
    }

    private void CaptureMapVisibility()
    {
        if (this.mapVisibilitySnapshotActive)
        {
            return;
        }

        this.mapVisibleBeforeInteraction = this.IsAreaMapVisible();
        this.mapVisibilitySnapshotActive = true;
    }

    private void ClearMapVisibilitySnapshot()
    {
        this.mapVisibleBeforeInteraction = false;
        this.mapVisibilitySnapshotActive = false;
    }

    private void QueueMapRestore()
    {
        if (this.restoreQueued)
        {
            return;
        }

        this.restoreQueued = true;
        this.Services.Framework.RunOnTick(
            this.RestoreMap,
            default,
            1,
            CancellationToken.None);
    }

    private void RestoreMap()
    {
        this.restoreQueued = false;
        if (!this.IsEnabled || !this.ShouldSuppressMapHide())
        {
            return;
        }

        this.ShowAreaMap();
        this.Services.Log.Debug("Restored AreaMap after it hid while NPC interaction was starting.");
    }

    private bool IsAreaMapVisible()
    {
        var addon = this.Services.GameGui.GetAddonByName(AreaMapAddonName, 1);
        if (addon.IsNull || addon.Address == IntPtr.Zero)
        {
            return false;
        }

        var addonPtr = (AtkUnitBase*)addon.Address;
        return addonPtr->IsVisible;
    }

    private void ShowAreaMap()
    {
        var addon = this.Services.GameGui.GetAddonByName(AreaMapAddonName, 1);
        if (addon.IsNull || addon.Address == IntPtr.Zero)
        {
            return;
        }

        var addonPtr = (AtkUnitBase*)addon.Address;
        if (addonPtr->IsVisible)
        {
            return;
        }

        addonPtr->Show(false, 0);
    }

    private bool IsAnyInteractionConditionActive()
    {
        foreach (var flag in InteractionConditionFlags)
        {
            if (this.Services.Condition[flag])
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInteractionConditionFlag(ConditionFlag flag)
    {
        foreach (var interactionFlag in InteractionConditionFlags)
        {
            if (interactionFlag == flag)
            {
                return true;
            }
        }

        return false;
    }
}
