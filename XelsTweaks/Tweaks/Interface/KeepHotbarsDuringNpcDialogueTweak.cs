using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class KeepHotbarsDuringNpcDialogueTweak : TweakBase
{
    public const string TweakId = "interface.keepHotbarsDuringNpcDialogue";

    private const string ActionSettingPrefix = "action";
    private const string DialogueSettingPrefix = "dialogue";
    private const int MaxNodeTreeDepth = 8;
    private const int MaxSiblingNodes = 256;
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(750);
    private static readonly ConditionFlag[] DialogueConditionFlags =
    [
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied39,
    ];

    private static readonly AddonOption[] ActionAddonOptions =
    [
        new("_ActionBar", "Hotbar 1", true),
        new("_ActionBar01", "Hotbar 2", true),
        new("_ActionBar02", "Hotbar 3", true),
        new("_ActionBar03", "Hotbar 4", true),
        new("_ActionBar04", "Hotbar 5", true),
        new("_ActionBar05", "Hotbar 6", true),
        new("_ActionBar06", "Hotbar 7", true),
        new("_ActionBar07", "Hotbar 8", true),
        new("_ActionBar08", "Hotbar 9", true),
        new("_ActionBar09", "Hotbar 10", true),
        new("_ActionBarEx", "Extra hotbar", true),
        new("_ActionCross", "Cross hotbar", true),
        new("_ActionDoubleCrossL", "Expanded cross hotbar - left", true),
        new("_ActionDoubleCrossR", "Expanded cross hotbar - right", true),
        new("_ActionContents", "Duty and content action bar", false),
        new("_MainCommand", "Main command bar", false),
        new("_MainCross", "Controller main menu", false),
    ];

    private static readonly AddonOption[] DialogueAddonOptions =
    [
        new("Talk", "Standard NPC dialogue", true),
        new("_MiniTalk", "Small NPC dialogue", true),
        new("MiniTalkPlayer", "Player dialogue in scenes", true),
        new("_BattleTalk", "Large story or battle dialogue", true),
        new("SelectYesno", "Yes / No prompts", true),
        new("SelectString", "Choice lists", true),
        new("SelectIconString", "Choice lists with icons", true),
        new("SelectOk", "OK prompts", true),
    ];
    private static readonly IReadOnlyList<TweakOptionDefinition> CommandOptions = CreateCommandOptions();

    private readonly HashSet<string> actionAddonsVisibleBeforeDialogue = [];
    private DateTimeOffset suppressActionHideUntil = DateTimeOffset.MinValue;
    private bool restoreQueued;
    private bool listenersRegistered;
    private bool actionVisibilitySnapshotActive;

    public KeepHotbarsDuringNpcDialogueTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Keep Hotbars During NPC Dialogue";
    public override string Description => "Keeps selected action bars available while NPC dialogue is open.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;
    public override IReadOnlyList<TweakOptionDefinition> Options => CommandOptions;

    public override bool DrawConfig()
    {
        var changed = false;

        ImGui.TextWrapped("Keep these controls visible during dialogue:");
        changed |= this.DrawOptionCheckboxes(ActionAddonOptions, ActionSettingPrefix);

        ImGui.Spacing();
        ImGui.TextWrapped("Count these windows as dialogue:");
        ImGui.TextWrapped("Most users can leave these enabled. Turn one off only if it keeps hotbars visible somewhere you do not want them.");
        changed |= this.DrawOptionCheckboxes(DialogueAddonOptions, DialogueSettingPrefix);

        return changed;
    }

    protected override void OnEnable()
    {
        foreach (var option in ActionAddonOptions)
        {
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreHide, option.AddonName, this.OnActionAddonPreHide);
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostHide, option.AddonName, this.OnActionAddonPostHide);
        }

        foreach (var option in DialogueAddonOptions)
        {
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreOpen, option.AddonName, this.OnDialogueAddonOpening);
            this.Services.AddonLifecycle.RegisterListener(AddonEvent.PreShow, option.AddonName, this.OnDialogueAddonOpening);
        }

        this.Services.Condition.ConditionChange += this.OnConditionChanged;
        this.listenersRegistered = true;

        if (this.IsDialogueContextActive())
        {
            this.StartSuppressionWindow();
            this.QueueActionAddonRestore();
        }
    }

    protected override void OnDisable()
    {
        if (!this.listenersRegistered)
        {
            return;
        }

        this.Services.Condition.ConditionChange -= this.OnConditionChanged;

        foreach (var option in DialogueAddonOptions)
        {
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreShow, option.AddonName, this.OnDialogueAddonOpening);
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreOpen, option.AddonName, this.OnDialogueAddonOpening);
        }

        foreach (var option in ActionAddonOptions)
        {
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostHide, option.AddonName, this.OnActionAddonPostHide);
            this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PreHide, option.AddonName, this.OnActionAddonPreHide);
        }

        this.suppressActionHideUntil = DateTimeOffset.MinValue;
        this.restoreQueued = false;
        this.listenersRegistered = false;
        this.ClearActionAddonVisibilitySnapshot();
    }

    private void OnDialogueAddonOpening(AddonEvent eventType, AddonArgs args)
    {
        if (!this.IsDialogueAddonEnabled(args.AddonName))
        {
            return;
        }

        this.StartSuppressionWindow();
        this.QueueActionAddonRestore();
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!value || !IsDialogueConditionFlag(flag))
        {
            return;
        }

        this.StartSuppressionWindow();
        this.QueueActionAddonRestore();
    }

    private void OnActionAddonPreHide(AddonEvent eventType, AddonArgs args)
    {
        if (!this.IsActionAddonEnabled(args.AddonName)
            || !this.ShouldSuppressActionHide()
            || !this.ShouldRestoreActionAddon(args.AddonName))
        {
            return;
        }

        args.PreventOriginal();
        this.Services.Log.Debug("Suppressed action bar hide for {AddonName} while NPC dialogue is active.", args.AddonName);
    }

    private void OnActionAddonPostHide(AddonEvent eventType, AddonArgs args)
    {
        if (!this.IsActionAddonEnabled(args.AddonName)
            || !this.ShouldSuppressActionHide()
            || !this.ShouldRestoreActionAddon(args.AddonName))
        {
            return;
        }

        this.ShowActionAddon(args.AddonName);
        this.Services.Log.Debug("Restored action bar {AddonName} after it hid while NPC dialogue was active.", args.AddonName);
    }

    private bool ShouldSuppressActionHide()
    {
        if (DateTimeOffset.UtcNow <= this.suppressActionHideUntil)
        {
            return true;
        }

        this.suppressActionHideUntil = DateTimeOffset.MinValue;
        if (this.IsDialogueContextActive())
        {
            this.CaptureActionAddonVisibility();
            return true;
        }

        this.ClearActionAddonVisibilitySnapshot();
        return false;
    }

    private void StartSuppressionWindow()
    {
        if (DateTimeOffset.UtcNow > this.suppressActionHideUntil && !this.IsDialogueContextActive())
        {
            this.ClearActionAddonVisibilitySnapshot();
        }

        this.CaptureActionAddonVisibility();
        this.suppressActionHideUntil = DateTimeOffset.UtcNow + SuppressionWindow;
    }

    private void QueueActionAddonRestore()
    {
        if (this.restoreQueued)
        {
            return;
        }

        this.restoreQueued = true;
        this.Services.Framework.RunOnTick(
            this.RestoreActionAddons,
            default,
            1,
            CancellationToken.None);
    }

    private void RestoreActionAddons()
    {
        this.restoreQueued = false;
        if (!this.IsEnabled)
        {
            return;
        }

        if (!this.IsDialogueContextActive())
        {
            if (DateTimeOffset.UtcNow > this.suppressActionHideUntil)
            {
                this.ClearActionAddonVisibilitySnapshot();
            }

            return;
        }

        foreach (var option in ActionAddonOptions)
        {
            if (this.IsOptionEnabled(option, ActionSettingPrefix) && this.ShouldRestoreActionAddon(option.AddonName))
            {
                this.ShowActionAddon(option.AddonName);
            }
        }
    }

    private void CaptureActionAddonVisibility()
    {
        if (this.actionVisibilitySnapshotActive)
        {
            return;
        }

        this.actionAddonsVisibleBeforeDialogue.Clear();
        foreach (var option in ActionAddonOptions)
        {
            if (this.IsOptionEnabled(option, ActionSettingPrefix) && this.IsActionAddonVisible(option.AddonName))
            {
                this.actionAddonsVisibleBeforeDialogue.Add(option.AddonName);
            }
        }

        this.actionVisibilitySnapshotActive = true;
    }

    private void ClearActionAddonVisibilitySnapshot()
    {
        this.actionAddonsVisibleBeforeDialogue.Clear();
        this.actionVisibilitySnapshotActive = false;
    }

    private bool ShouldRestoreActionAddon(string addonName)
    {
        return this.actionVisibilitySnapshotActive
            && this.actionAddonsVisibleBeforeDialogue.Contains(addonName);
    }

    private void ShowActionAddon(string addonName)
    {
        var addon = this.Services.GameGui.GetAddonByName(addonName, 1);
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

    private bool IsDialogueContextActive()
    {
        return this.IsAnyDialogueAddonVisible();
    }

    private bool IsAnyDialogueAddonVisible()
    {
        foreach (var option in DialogueAddonOptions)
        {
            if (this.IsOptionEnabled(option, DialogueSettingPrefix) && this.IsDialogueAddonVisible(option.AddonName))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsActionAddonVisible(string addonName)
    {
        var addon = this.Services.GameGui.GetAddonByName(addonName, 1);
        if (addon.IsNull || addon.Address == IntPtr.Zero)
        {
            return false;
        }

        var addonPtr = (AtkUnitBase*)addon.Address;
        return addonPtr->IsVisible;
    }

    private bool IsDialogueAddonVisible(string addonName)
    {
        var addon = this.Services.GameGui.GetAddonByName(addonName, 1);
        if (addon.IsNull || addon.Address == IntPtr.Zero)
        {
            return false;
        }

        var addonPtr = (AtkUnitBase*)addon.Address;
        return addonPtr->IsVisible || HasVisibleNode(addonPtr->RootNode, 0);
    }

    private static bool HasVisibleNode(AtkResNode* node, int depth)
    {
        if (node == null || depth > MaxNodeTreeDepth)
        {
            return false;
        }

        var currentNode = node;
        for (var i = 0; currentNode != null && i < MaxSiblingNodes; i++)
        {
            if (currentNode->IsVisible())
            {
                return true;
            }

            if (HasVisibleNode(currentNode->ChildNode, depth + 1))
            {
                return true;
            }

            currentNode = currentNode->NextSiblingNode;
        }

        return false;
    }

    private static bool IsDialogueConditionFlag(ConditionFlag flag)
    {
        foreach (var dialogueFlag in DialogueConditionFlags)
        {
            if (dialogueFlag == flag)
            {
                return true;
            }
        }

        return false;
    }

    private bool DrawOptionCheckboxes(AddonOption[] options, string settingPrefix)
    {
        var changed = false;
        ImGui.PushID(settingPrefix);
        foreach (var option in options)
        {
            var enabled = this.IsOptionEnabled(option, settingPrefix);
            if (ImGui.Checkbox(option.Label, ref enabled))
            {
                this.SetBool(GetSettingKey(settingPrefix, option.AddonName), enabled);
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private bool IsActionAddonEnabled(string addonName)
    {
        return this.IsKnownOptionEnabled(ActionAddonOptions, ActionSettingPrefix, addonName);
    }

    private bool IsDialogueAddonEnabled(string addonName)
    {
        return this.IsKnownOptionEnabled(DialogueAddonOptions, DialogueSettingPrefix, addonName);
    }

    private bool IsKnownOptionEnabled(AddonOption[] options, string settingPrefix, string addonName)
    {
        foreach (var option in options)
        {
            if (option.AddonName.Equals(addonName, StringComparison.Ordinal))
            {
                return this.IsOptionEnabled(option, settingPrefix);
            }
        }

        return false;
    }

    private bool IsOptionEnabled(AddonOption option, string settingPrefix)
    {
        return this.GetBool(GetSettingKey(settingPrefix, option.AddonName), option.DefaultEnabled);
    }

    private static string GetSettingKey(string settingPrefix, string addonName)
    {
        return $"{settingPrefix}.{addonName}";
    }

    private static IReadOnlyList<TweakOptionDefinition> CreateCommandOptions()
    {
        var options = new List<TweakOptionDefinition>();
        foreach (var option in ActionAddonOptions)
        {
            options.Add(TweakOptionDefinition.Bool(
                GetSettingKey(ActionSettingPrefix, option.AddonName),
                option.Label,
                $"Keeps {option.Label} visible while dialogue is active.",
                option.DefaultEnabled,
                "Controls"));
        }

        foreach (var option in DialogueAddonOptions)
        {
            options.Add(TweakOptionDefinition.Bool(
                GetSettingKey(DialogueSettingPrefix, option.AddonName),
                option.Label,
                $"Treats {option.Label} as dialogue for hotbar visibility.",
                option.DefaultEnabled,
                "Dialogue windows"));
        }

        return options;
    }

    private readonly record struct AddonOption(string AddonName, string Label, bool DefaultEnabled);
}
