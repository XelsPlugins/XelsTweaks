using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class QuickInventoryDeleteTweak : TweakBase, IRequiresEnableAgreement
{
    public const string TweakId = "interface.quickInventoryDelete";

    private const string ModifierKeySetting = "modifierKey";
    private const string WaiverAcceptedSetting = "liabilityWaiverAccepted";
    private const string SelectYesNoAddonName = "SelectYesno";
    private const string ContextMenuAddonName = "ContextMenu";
    private const string ContextIconMenuAddonName = "ContextIconMenu";
    private const string DefaultModifierKey = nameof(VirtualKey.CONTROL);
    private const int PendingTimeoutMilliseconds = 15_000;
    private const int ActivationWindowMilliseconds = 750;
    private const int InventoryStabilityDelayMilliseconds = 750;
    private const ulong MaxHoveredItemId = 2_000_000;

    private static readonly VirtualKey[] ModifierKeys =
    [
        VirtualKey.CONTROL,
        VirtualKey.LCONTROL,
        VirtualKey.RCONTROL,
        VirtualKey.SHIFT,
        VirtualKey.LSHIFT,
        VirtualKey.RSHIFT,
        VirtualKey.MENU,
        VirtualKey.LMENU,
        VirtualKey.RMENU,
    ];

    private IReadOnlyList<VirtualKey>? validModifierKeys;
    private PendingDiscard? pendingDiscard;
    private bool rightButtonWasDown;
    private bool imguiModifierHeld;
    private bool imguiRightButtonWasDown;
    private DateTimeOffset recentActivationDeadline;
    private ulong recentActivationHoveredItemId;
    private DateTimeOffset inventoryStableAfter;
    private string lastDevStatus = "Waiting for activation.";

    public QuickInventoryDeleteTweak(DalamudServices services, TweakState state, System.Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Quick Inventory Delete";
    public override string Description => "Hold a modifier and right-click inventory items to discard them.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;
    public bool RequiresEnableAgreement => !this.GetBool(WaiverAcceptedSetting, false);
    public string EnableAgreementTitle => "Quick Inventory Delete Waiver";
    public string EnableAgreementCheckboxLabel => "I understand and accept responsibility for deleted items.";
    public string EnableAgreementText =>
        "Quick Inventory Delete can permanently discard items when you hold the configured modifier and right-click inventory entries. "
        + "Deleted items may not be recoverable. You are solely responsible for what you delete, including mistakes, misclicks, configuration choices, and any loss of items, currency, progress, or value. "
        + "XelsTweaks, this plugin, its maintainers, contributors, and related projects provide this tweak as-is and are not responsible or liable for deletion mistakes or losses caused by using it.";

    public override IReadOnlyList<TweakOptionDefinition> Options
        => this.CreateCommandOptions();

    public override bool DrawConfig()
    {
        var keys = this.GetValidModifierKeys();
        var currentKey = this.GetModifierKey();
        var currentIndex = Math.Max(0, keys.ToList().IndexOf(currentKey));
        var preview = keys.Count > 0 ? FormatModifierKey(keys[currentIndex]) : FormatModifierKey(currentKey);
        var changed = false;

        ImGui.TextWrapped("Hold the selected modifier and right-click an inventory item to discard it.");
        ImGui.TextWrapped("The game still decides which items can be discarded.");
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Modifier", preview))
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var selected = i == currentIndex;
                if (ImGui.Selectable(FormatModifierKey(key), selected))
                {
                    this.SetString(ModifierKeySetting, key.ToString());
                    changed = true;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (this.Services.PluginInterface.IsDev)
        {
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Debug"))
            {
                var waiverAccepted = !this.RequiresEnableAgreement;
                ImGui.TextWrapped($"Waiver accepted: {(waiverAccepted ? "yes" : "no")}");
                ImGui.TextWrapped($"Live modifier detected: {(this.IsActivationHeld() ? "yes" : "no")}");
                ImGui.TextWrapped($"Recent activation captured: {(DateTimeOffset.UtcNow <= this.recentActivationDeadline ? "yes" : "no")}");
                ImGui.TextWrapped($"Last quick-delete status: {this.lastDevStatus}");

                if (!waiverAccepted)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Button("Reset Waiver"))
                {
                    this.SetBool(WaiverAcceptedSetting, false);
                    this.LastError = null;
                    changed = true;
                }

                if (!waiverAccepted)
                {
                    ImGui.EndDisabled();
                }
            }
        }

        return changed;
    }

    public void AcceptEnableAgreement()
    {
        this.SetBool(WaiverAcceptedSetting, true);
        this.LastError = null;
    }

    protected override void OnEnable()
    {
        if (this.RequiresEnableAgreement)
        {
            throw new InvalidOperationException("Accept the Quick Inventory Delete waiver before enabling this tweak.");
        }

        this.ClearPending();
        this.Services.PluginInterface.UiBuilder.Draw += this.OnUiDraw;
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.GameInventory.InventoryChangedRaw += this.OnInventoryChangedRaw;
        this.Services.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    protected override void OnDisable()
    {
        this.Services.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        this.Services.GameInventory.InventoryChangedRaw -= this.OnInventoryChangedRaw;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.Services.PluginInterface.UiBuilder.Draw -= this.OnUiDraw;
        this.rightButtonWasDown = false;
        this.imguiModifierHeld = false;
        this.imguiRightButtonWasDown = false;
        this.recentActivationDeadline = default;
        this.recentActivationHoveredItemId = 0;
        this.inventoryStableAfter = default;
        this.ClearPending();
    }

    private void OnUiDraw()
    {
        var modifierHeld = IsImGuiModifierKeyDown(this.GetModifierKey());
        var rightButtonDown = ImGui.IsMouseDown(ImGuiMouseButton.Right);
        if (rightButtonDown && !this.imguiRightButtonWasDown && modifierHeld)
        {
            this.CaptureRecentActivation("ImGui");
        }

        this.imguiModifierHeld = modifierHeld;
        this.imguiRightButtonWasDown = rightButtonDown;
        this.TryProcessOpenInventoryContextMenu("ImGui activation");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.TrackActivationInput();
        this.TryProcessOpenInventoryContextMenu("key-state activation");
        this.UpdatePendingDiscard();
    }

    private void OnInventoryChangedRaw(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (this.pendingDiscard is not { } pending)
        {
            this.inventoryStableAfter = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(InventoryStabilityDelayMilliseconds);
            return;
        }

        foreach (var inventoryEvent in events)
        {
            var item = inventoryEvent.Item;
            if ((InventoryType)(int)item.ContainerType != pending.InventoryType || item.InventorySlot != pending.Slot)
            {
                continue;
            }

            if (item.IsEmpty || item.ItemId != pending.RawItemId)
            {
                this.ClearPending();
            }

            return;
        }
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!this.HasRecentActivation())
        {
            return;
        }

        if (args.MenuType != ContextMenuType.Inventory)
        {
            this.ConsumeRecentActivation();
            this.Services.Log.Debug(
                "Quick Inventory Delete ignored {MenuType} context menu from {AddonName}.",
                args.MenuType,
                args.AddonName ?? "unknown addon");
            return;
        }

        this.ConsumeRecentActivation();
        if (args.AgentPtr == IntPtr.Zero)
        {
            this.SetDevStatus("Inventory context menu had no agent pointer.");
            return;
        }

        this.TryDiscardFromInventoryContext((AgentInventoryContext*)args.AgentPtr, args, "context menu open");
    }

    private void TryProcessOpenInventoryContextMenu(string source)
    {
        if (!this.HasRecentActivation()
            || this.pendingDiscard != null
            || (!this.HasActivationHoveredItem()
                && !this.TryGetVisibleAddon(ContextMenuAddonName, out _)
                && !this.TryGetVisibleAddon(ContextIconMenuAddonName, out _)))
        {
            return;
        }

        var inventoryContext = AgentInventoryContext.Instance();
        if (inventoryContext == null)
        {
            this.SetDevStatus($"{source}: inventory context agent unavailable.");
            return;
        }

        if (this.TryDiscardFromInventoryContext(inventoryContext, null, source))
        {
            this.ConsumeRecentActivation();
        }
    }

    private bool TryDiscardFromInventoryContext(AgentInventoryContext* inventoryContext, IMenuOpenedArgs? args, string source)
    {
        if (DateTimeOffset.UtcNow < this.inventoryStableAfter)
        {
            this.SetDevStatus($"{source}: inventory changed recently; skipped for safety.");
            return false;
        }

        if (this.IsInventoryInteractionActive(inventoryContext, out var interactionReason))
        {
            this.SetDevStatus($"{source}: {interactionReason}; skipped for safety.");
            return false;
        }

        if (this.pendingDiscard != null)
        {
            this.SetDevStatus($"{source}: discard already pending.");
            return false;
        }

        if (this.TryGetAnyVisibleSelectYesNoAddon(out _))
        {
            this.SetDevStatus($"{source}: confirmation dialog already open.");
            this.Services.Log.Debug("Quick Inventory Delete skipped because a confirmation dialog is already open.");
            return false;
        }

        if (!this.TryGetTargetSlot(args, inventoryContext, out var slot)
            || !this.TryGetInventoryItemPointer(slot.InventoryType, slot.Slot, out var inventoryItem))
        {
            this.SetDevStatus($"{source}: could not resolve inventory slot.");
            this.Services.Log.Debug("Quick Inventory Delete could not resolve an inventory slot from {Source}.", source);
            return false;
        }

        if (!this.DoesActivationHoveredItemMatch(slot))
        {
            this.SetDevStatus($"{source}: hovered item did not match context target.");
            this.Services.Log.Debug(
                "Quick Inventory Delete skipped context target {InventoryType}:{Slot}; activation hover was {HoveredItemId}, target item was {TargetItemId}.",
                slot.InventoryType,
                slot.Slot,
                this.recentActivationHoveredItemId,
                slot.RawItemId);
            return false;
        }

        var currentRawItemId = inventoryItem->GetItemId();
        if (currentRawItemId == 0 || currentRawItemId != slot.RawItemId)
        {
            this.SetDevStatus($"{source}: stale slot {slot.InventoryType}:{slot.Slot}.");
            this.Services.Log.Debug(
                "Quick Inventory Delete skipped stale slot {InventoryType}:{Slot}; expected item {ExpectedItemId}, found {CurrentItemId}.",
                slot.InventoryType,
                slot.Slot,
                slot.RawItemId,
                currentRawItemId);
            return false;
        }

        var itemName = this.GetItemName(slot.BaseItemId);

        var pending = new PendingDiscard(
            slot.InventoryType,
            slot.Slot,
            slot.RawItemId,
            slot.BaseItemId,
            itemName,
            DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(PendingTimeoutMilliseconds));

        this.pendingDiscard = pending;
        return this.TryRequestNativeDiscard(pending, source);
    }

    private void UpdatePendingDiscard()
    {
        if (this.pendingDiscard is not { } pending)
        {
            return;
        }

        if (!this.IsSameItemStillPresent(pending))
        {
            this.SetDevStatus($"Removed {pending.ItemName} from {pending.InventoryType}:{pending.Slot}.");
            this.ClearPending();
            return;
        }

        if (!pending.ConfirmationClicked && this.TryConfirmDiscardPrompt(pending))
        {
            this.pendingDiscard = pending with { ConfirmationClicked = true };
            return;
        }

        if (DateTimeOffset.UtcNow <= pending.Deadline)
        {
            return;
        }

        this.Services.Log.Debug(
            "Quick Inventory Delete timed out waiting for discard confirmation/removal for {ItemName} at {InventoryType}:{Slot}.",
            pending.ItemName,
            pending.InventoryType,
            pending.Slot);
        this.SetDevStatus($"Timed out waiting for discard confirmation/removal for {pending.ItemName}.");
        this.ClearPending();
    }

    private bool TryRequestNativeDiscard(PendingDiscard pending, string source)
    {
        if (!this.TryGetInventoryItemPointer(pending.InventoryType, pending.Slot, out var inventoryItem)
            || inventoryItem->GetItemId() != pending.RawItemId)
        {
            this.SetDevStatus($"{source}: item changed before discard request.");
            this.ClearPending();
            return false;
        }

        if (this.TryGetAnyVisibleSelectYesNoAddon(out _))
        {
            this.SetDevStatus($"{source}: waiting for existing confirmation dialog.");
            return false;
        }

        try
        {
            var requestContext = AgentInventoryContext.Instance();
            requestContext->DiscardItem(inventoryItem, inventoryItem->Container, inventoryItem->Slot, 0);

            this.pendingDiscard = pending;
            this.SetDevStatus($"{source}: requested discard for {pending.ItemName}.");
            this.Services.Log.Debug(
                "Quick Inventory Delete requested discard for {ItemName} at {InventoryType}:{Slot}.",
                pending.ItemName,
                pending.InventoryType,
                pending.Slot);
            return true;
        }
        catch (Exception ex)
        {
            this.ClearPending();
            this.SetDevStatus($"{source}: discard request failed.");
            this.Services.Log.Warning(ex, "Quick Inventory Delete failed to request discard.");
            return false;
        }
    }

    private bool TryConfirmDiscardPrompt(PendingDiscard pending)
    {
        var inventoryContext = AgentInventoryContext.Instance();
        if (inventoryContext != null && this.IsInventoryInteractionActive(inventoryContext, out var interactionReason))
        {
            this.SetDevStatus($"Waiting to confirm {pending.ItemName}: {interactionReason}.");
            return false;
        }

        if (!this.TryGetDiscardSelectYesNoAddon(out var addon))
        {
            return false;
        }

        var prompt = ReadSelectYesNoPrompt((AddonSelectYesno*)addon);
        if (!IsDiscardPrompt(prompt))
        {
            return false;
        }

        var selectYesNo = (AddonSelectYesno*)addon;
        if (selectYesNo->YesButton == null)
        {
            return false;
        }

        selectYesNo->YesButton->AtkComponentBase.SetEnabledState(true);
        addon->FireCallbackInt(0);
        this.SetDevStatus($"Confirmed discard for {pending.ItemName}.");
        this.Services.Log.Debug("Quick Inventory Delete confirmed discard for {ItemName}.", pending.ItemName);
        return true;
    }

    private bool TryGetTargetSlot(IMenuOpenedArgs? args, AgentInventoryContext* inventoryContext, out InventorySlot slot)
    {
        if (args?.Target is MenuTargetInventory target && target.TargetItem is { IsEmpty: false } targetItem)
        {
            slot = new InventorySlot(
                (InventoryType)(int)targetItem.ContainerType,
                (uint)targetItem.InventorySlot,
                targetItem.ItemId,
                targetItem.BaseItemId);
            return slot.RawItemId != 0;
        }

        if (inventoryContext->TargetInventoryId != InventoryType.Invalid
            && inventoryContext->TargetInventorySlotId >= 0
            && inventoryContext->TargetInventorySlot != null
            && !inventoryContext->TargetInventorySlot->IsEmpty())
        {
            var inventoryItem = inventoryContext->TargetInventorySlot;
            var rawItemId = inventoryItem->GetItemId();
            slot = new InventorySlot(
                inventoryContext->TargetInventoryId,
                (uint)inventoryContext->TargetInventorySlotId,
                rawItemId,
                GetBaseItemId(rawItemId));
            return rawItemId != 0;
        }

        var dummyItem = &inventoryContext->TargetDummyItem;
        if (!dummyItem->IsEmpty()
            && inventoryContext->TargetInventoryId != InventoryType.Invalid
            && inventoryContext->TargetInventorySlotId >= 0)
        {
            var rawItemId = dummyItem->GetItemId();
            slot = new InventorySlot(
                inventoryContext->TargetInventoryId,
                (uint)inventoryContext->TargetInventorySlotId,
                rawItemId,
                GetBaseItemId(rawItemId));
            return rawItemId != 0;
        }

        slot = default;
        return false;
    }

    private bool TryGetInventoryItemPointer(InventoryType inventoryType, uint slotIndex, out InventoryItem* inventoryItem)
    {
        inventoryItem = null;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return false;
        }

        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null || slotIndex >= container->GetSize())
        {
            return false;
        }

        inventoryItem = container->GetInventorySlot((int)slotIndex);
        return inventoryItem != null && !inventoryItem->IsEmpty();
    }

    private bool IsInventoryInteractionActive(AgentInventoryContext* inventoryContext, out string reason)
    {
        if (inventoryContext->BlockedInventoryId != InventoryType.Invalid && inventoryContext->BlockedInventorySlotId >= 0)
        {
            reason = $"inventory slot {inventoryContext->BlockedInventoryId}:{inventoryContext->BlockedInventorySlotId} is still active";
            return true;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null)
        {
            foreach (var operation in inventoryManager->PendingOperations)
            {
                if (!operation.IsEmpty)
                {
                    reason = $"inventory operation {operation.Type} is still pending";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    private bool IsSameItemStillPresent(PendingDiscard pending)
    {
        return this.TryGetInventoryItemPointer(pending.InventoryType, pending.Slot, out var inventoryItem)
            && inventoryItem->GetItemId() == pending.RawItemId;
    }

    private bool IsActivationHeld()
    {
        return this.imguiModifierHeld || this.IsModifierKeyDown(this.GetModifierKey());
    }

    private void TrackActivationInput()
    {
        var rightButtonDown = this.IsPhysicalKeyDown(VirtualKey.RBUTTON);
        if (rightButtonDown && !this.rightButtonWasDown && this.IsActivationHeld())
        {
            this.CaptureRecentActivation("key state");
        }

        this.rightButtonWasDown = rightButtonDown;
    }

    private void CaptureRecentActivation(string source)
    {
        this.recentActivationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ActivationWindowMilliseconds);
        this.recentActivationHoveredItemId = this.Services.GameGui.HoveredItem;
        this.Services.Log.Debug(
            "Quick Inventory Delete captured {Modifier}+right-click activation from {Source}; hovered item={HoveredItemId}.",
            FormatModifierKey(this.GetModifierKey()),
            source,
            this.recentActivationHoveredItemId);
    }

    private bool HasRecentActivation()
    {
        return this.IsActivationHeld() || DateTimeOffset.UtcNow <= this.recentActivationDeadline;
    }

    private void ConsumeRecentActivation()
    {
        this.recentActivationDeadline = default;
        this.recentActivationHoveredItemId = 0;
    }

    private bool HasActivationHoveredItem()
    {
        return this.recentActivationHoveredItemId > 0 && this.recentActivationHoveredItemId < MaxHoveredItemId;
    }

    private bool DoesActivationHoveredItemMatch(InventorySlot slot)
    {
        if (!this.HasActivationHoveredItem())
        {
            return true;
        }

        var hoveredItemId = (uint)this.recentActivationHoveredItemId;
        return hoveredItemId == slot.RawItemId || hoveredItemId == slot.BaseItemId;
    }

    private bool IsModifierKeyDown(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.CONTROL => this.IsPhysicalKeyDown(VirtualKey.CONTROL)
                || this.IsPhysicalKeyDown(VirtualKey.LCONTROL)
                || this.IsPhysicalKeyDown(VirtualKey.RCONTROL),
            VirtualKey.SHIFT => this.IsPhysicalKeyDown(VirtualKey.SHIFT)
                || this.IsPhysicalKeyDown(VirtualKey.LSHIFT)
                || this.IsPhysicalKeyDown(VirtualKey.RSHIFT),
            VirtualKey.MENU => this.IsPhysicalKeyDown(VirtualKey.MENU)
                || this.IsPhysicalKeyDown(VirtualKey.LMENU)
                || this.IsPhysicalKeyDown(VirtualKey.RMENU),
            _ => this.IsPhysicalKeyDown(key),
        };
    }

    private static bool IsImGuiModifierKeyDown(VirtualKey key)
    {
        var io = ImGui.GetIO();
        return key switch
        {
            VirtualKey.CONTROL or VirtualKey.LCONTROL or VirtualKey.RCONTROL => io.KeyCtrl,
            VirtualKey.SHIFT or VirtualKey.LSHIFT or VirtualKey.RSHIFT => io.KeyShift,
            VirtualKey.MENU or VirtualKey.LMENU or VirtualKey.RMENU => io.KeyAlt,
            _ => false,
        };
    }

    private bool IsPhysicalKeyDown(VirtualKey key)
    {
        try
        {
            if (this.Services.KeyState[key])
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        return IsAsyncKeyDown(key);
    }

    private VirtualKey GetModifierKey()
    {
        var stored = this.GetString(ModifierKeySetting, DefaultModifierKey);
        if (Enum.TryParse<VirtualKey>(stored, true, out var key) && this.IsValidModifierKey(key))
        {
            return key;
        }

        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyCode)
            && Enum.IsDefined(typeof(VirtualKey), keyCode))
        {
            key = (VirtualKey)keyCode;
            if (this.IsValidModifierKey(key))
            {
                return key;
            }
        }

        return VirtualKey.CONTROL;
    }

    private IReadOnlyList<VirtualKey> GetValidModifierKeys()
    {
        this.validModifierKeys ??= ModifierKeys
            .Where(this.IsValidModifierKey)
            .ToArray();
        return this.validModifierKeys;
    }

    private bool IsValidModifierKey(VirtualKey key)
    {
        if (!ModifierKeys.Contains(key))
        {
            return false;
        }

        return this.Services.KeyState.IsVirtualKeyValid(key);
    }

    private IReadOnlyList<TweakOptionDefinition> CreateCommandOptions()
    {
        var choices = this.GetValidModifierKeys()
            .Select(key => new TweakOptionChoice(key.ToString(), FormatModifierKey(key), key.ToString()))
            .ToArray();

        return
        [
            TweakOptionDefinition.Choice(
                ModifierKeySetting,
                "Modifier key",
                "Key held while right-clicking an inventory item to discard it.",
                DefaultModifierKey,
                choices,
                "Activation")
        ];
    }

    private string GetItemName(uint itemId)
    {
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        return itemSheet.TryGetRow(itemId, out var itemRow)
            ? itemRow.Name.ToString()
            : $"item {itemId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatModifierKey(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.CONTROL => "Ctrl",
            VirtualKey.LCONTROL => "Left Ctrl",
            VirtualKey.RCONTROL => "Right Ctrl",
            VirtualKey.SHIFT => "Shift",
            VirtualKey.LSHIFT => "Left Shift",
            VirtualKey.RSHIFT => "Right Shift",
            VirtualKey.MENU => "Alt",
            VirtualKey.LMENU => "Left Alt",
            VirtualKey.RMENU => "Right Alt",
            _ => key.GetFancyName(),
        };
    }

    private static uint GetBaseItemId(uint itemId)
    {
        return itemId == 0
            ? 0
            : ItemUtil.GetBaseId(itemId).ItemId;
    }

    private bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon)
    {
        var handle = this.Services.GameGui.GetAddonByName(addonName, 1);
        if (handle.IsNull || handle.Address == IntPtr.Zero)
        {
            addon = null;
            return false;
        }

        addon = (AtkUnitBase*)handle.Address;
        return addon->IsVisible && addon->IsReady;
    }

    private bool TryGetAnyVisibleSelectYesNoAddon(out AtkUnitBase* addon)
    {
        for (var i = 1; i < 100; i++)
        {
            var handle = this.Services.GameGui.GetAddonByName(SelectYesNoAddonName, i);
            if (handle.IsNull || handle.Address == IntPtr.Zero)
            {
                break;
            }

            addon = (AtkUnitBase*)handle.Address;
            if (addon->IsVisible && addon->IsReady)
            {
                return true;
            }
        }

        addon = null;
        return false;
    }

    private bool TryGetDiscardSelectYesNoAddon(out AtkUnitBase* addon)
    {
        for (var i = 1; i < 100; i++)
        {
            var handle = this.Services.GameGui.GetAddonByName(SelectYesNoAddonName, i);
            if (handle.IsNull || handle.Address == IntPtr.Zero)
            {
                break;
            }

            addon = (AtkUnitBase*)handle.Address;
            if (!addon->IsVisible || !addon->IsReady)
            {
                continue;
            }

            var prompt = ReadSelectYesNoPrompt((AddonSelectYesno*)addon);
            if (IsDiscardPrompt(prompt))
            {
                return true;
            }
        }

        addon = null;
        return false;
    }

    private void ClearPending()
    {
        this.pendingDiscard = null;
        this.ConsumeRecentActivation();
    }

    private void SetDevStatus(string status)
    {
        this.lastDevStatus = status;
    }

    private static string ReadSelectYesNoPrompt(AddonSelectYesno* addon)
    {
        var unitBase = (AtkUnitBase*)addon;
        if (unitBase->AtkValues != null && unitBase->AtkValuesCount > 0)
        {
            var valueText = ReadAtkValueString(unitBase->AtkValues[0]);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                return valueText;
            }
        }

        return addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString();
    }

    private static string ReadAtkValueString(AtkValue value)
    {
        return value.Type switch
        {
            AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString => value.String.ToString(),
            AtkValueType.WideString => value.WideString == null ? string.Empty : new string(value.WideString),
            _ => string.Empty
        };
    }

    private static bool IsDiscardPrompt(string prompt)
    {
        return prompt.Contains("discard", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsyncKeyDown(VirtualKey key)
    {
        try
        {
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly record struct PendingDiscard(
        InventoryType InventoryType,
        uint Slot,
        uint RawItemId,
        uint BaseItemId,
        string ItemName,
        DateTimeOffset Deadline,
        bool ConfirmationClicked = false);

    private readonly record struct InventorySlot(
        InventoryType InventoryType,
        uint Slot,
        uint RawItemId,
        uint BaseItemId);
}
