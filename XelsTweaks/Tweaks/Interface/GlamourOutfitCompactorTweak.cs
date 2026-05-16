using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class GlamourOutfitCompactorTweak : TweakBase
{
    public const string TweakId = "interface.glamourOutfitCompactor";

    private const string ConfirmDyedOutfitsKey = "confirmDyedOutfits";
    private const string AttemptNativeStoreClickKey = "attemptNativeStoreClick";
    private const string ShowOverlayOnlyWhenEligibleKey = "showOverlayOnlyWhenEligible";
    private const string DresserAddonName = "MiragePrismPrismBox";
    private const string PlateAddonName = "MiragePrismMiragePlate";
    private const string SetConvertAddonName = "MiragePrismPrismSetConvert";
    private const string SetConvertConfirmAddonName = "MiragePrismPrismSetConvertC";
    private const string ContextIconMenuAddonName = "ContextIconMenu";
    private const string OpenSetConvertSignature = "40 53 41 55 48 81 EC ?? ?? ?? ?? 0F B7 84 24";
    private const uint GlamourPrismItemId = 21800;
    private const uint StoreAsGlamourButtonId = 27;
    private const uint ConfirmStoreAsOutfitCheckBoxId = 4;
    private const uint ConfirmYesButtonId = 6;
    private const int SetConvertHandOverCallbackId = 13;
    private const uint SetConvertContextMenuActionId = 1021003;
    private const int SetConvertUiItemCountOffset = 20;
    private const int SetConvertUiItemsOffset = 21;
    private const int SetConvertUiItemStride = 7;
    private const int MaxRestoreAttempts = 6;

    private static readonly TimeSpan CandidateRefreshDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(18);
    private static readonly Vector4 WarningColor = new(1f, 0.74f, 0.25f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 SuccessColor = new(0.48f, 0.95f, 0.56f, 1f);

    private readonly List<OutfitCandidate> candidates = [];
    private readonly List<QueuedOutfit> queue = [];
    private delegate* unmanaged<AgentMiragePrismPrismSetConvert*, uint, InventoryType, int, int, bool, void> openSetConvertAddon;
    private uint[] lastPrismBoxItemIds = [];
    private QueueStep step = QueueStep.Idle;
    private QueuedOutfit? currentOutfit;
    private int completedOutfits;
    private int skippedOutfits;
    private uint? waitingForRestoredItemId;
    private uint? restoreRetryItemId;
    private int restoreRetryAttempts;
    private int storedSetRestoreRetryAttempts;
    private int? pendingSetConvertSlot;
    private uint? pendingSetConvertItemId;
    private DateTimeOffset nextActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset stepStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextCandidateRefreshAt = DateTimeOffset.MinValue;
    private string status = "Open the Glamour Dresser to scan for eligible outfits.";
    private string? lastError;
    private bool candidatesDirty = true;
    private bool queuePaused;
    private int confirmCheckBoxAttempts;

    public GlamourOutfitCompactorTweak(DalamudServices services, TweakState state, System.Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Glamour Outfit Compactor";
    public override string Description => "Adds a Glamour Dresser button that restores eligible outfit pieces and stores them back as outfits.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;

    private bool ConfirmDyedOutfits => this.GetBool(ConfirmDyedOutfitsKey, true);
    private bool AttemptNativeStoreClick => this.GetBool(AttemptNativeStoreClickKey, true);
    private bool ShowOverlayOnlyWhenEligible => this.GetBool(ShowOverlayOnlyWhenEligibleKey, true);
    private bool IsQueueActive => this.step is not QueueStep.Idle and not QueueStep.Complete and not QueueStep.Error;

    public override bool DrawConfig()
    {
        var changed = false;

        var confirmDyedOutfits = this.ConfirmDyedOutfits;
        if (ImGui.Checkbox("Confirm outfits with dyed pieces", ref confirmDyedOutfits))
        {
            this.SetBool(ConfirmDyedOutfitsKey, confirmDyedOutfits);
            changed = true;
        }

        var attemptNativeStoreClick = this.AttemptNativeStoreClick;
        if (ImGui.Checkbox("Attempt the native Store click automatically", ref attemptNativeStoreClick))
        {
            this.SetBool(AttemptNativeStoreClickKey, attemptNativeStoreClick);
            changed = true;
        }

        var showOverlayOnlyWhenEligible = this.ShowOverlayOnlyWhenEligible;
        if (ImGui.Checkbox("Hide overlay when no eligible outfits are found", ref showOverlayOnlyWhenEligible))
        {
            this.SetBool(ShowOverlayOnlyWhenEligibleKey, showOverlayOnlyWhenEligible);
            changed = true;
        }

        ImGui.TextColored(WarningColor, "Risk note:");
        ImGui.SameLine();
        ImGui.TextWrapped("This is user-triggered Glamour Dresser inventory automation. Confirmed dyed pieces may lose dye state when converted into an outfit. Complete loose dresser pieces are restored before storing the completed outfit; partial stored outfits may be merged only when the stored outfit already exists in the dresser.");

        return changed;
    }

    protected override void OnEnable()
    {
        if (this.openSetConvertAddon == null)
        {
            this.openSetConvertAddon = (delegate* unmanaged<AgentMiragePrismPrismSetConvert*, uint, InventoryType, int, int, bool, void>)this.Services.SigScanner.ScanText(OpenSetConvertSignature);
        }

        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.PluginInterface.UiBuilder.Draw += this.DrawOverlay;
        this.Services.GameInventory.InventoryChangedRaw += this.OnInventoryChanged;
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostOpen, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostClose, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostClose, SetConvertAddonName, this.OnSetConvertClosed);
        this.MarkCandidatesDirty();
    }

    protected override void OnDisable()
    {
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostClose, SetConvertAddonName, this.OnSetConvertClosed);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostClose, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostOpen, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.GameInventory.InventoryChangedRaw -= this.OnInventoryChanged;
        this.Services.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.ResetQueue("Disabled.");
        this.candidates.Clear();
        this.lastPrismBoxItemIds = [];
    }

    private void OnDresserAddonChanged(AddonEvent eventType, AddonArgs args)
    {
        this.MarkCandidatesDirty();

        if (eventType == AddonEvent.PostClose && this.IsQueueActive)
        {
            this.FailQueue("Glamour Dresser closed; stopped the outfit conversion queue.");
        }
    }

    private void OnSetConvertClosed(AddonEvent eventType, AddonArgs args)
    {
        if (this.step == QueueStep.WaitingForStore)
        {
            return;
        }

        if (this.IsQueueActive)
        {
            this.FailQueue("Outfit Glamour Creation closed before the queued outfit was stored.");
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        this.MarkCandidatesDirty();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.IsDresserOpen())
        {
            if (!this.IsQueueActive)
            {
                this.status = "Open the Glamour Dresser to scan for eligible outfits.";
                this.candidates.Clear();
                this.lastPrismBoxItemIds = [];
            }

            return;
        }

        this.RefreshCandidatesIfNeeded();

        if (!this.IsQueueActive || this.queuePaused)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < this.nextActionAt)
        {
            return;
        }

        if (now - this.stepStartedAt > StepTimeout)
        {
            var detail = this.step == QueueStep.FillingSetConvert && this.currentOutfit != null
                ? $" {this.GetSetConvertFillDiagnostic(this.currentOutfit)}"
                : string.Empty;
            this.FailQueue($"Timed out while {this.GetStepDescription(this.step)}.{detail}");
            return;
        }

        this.AdvanceQueue();
    }

    private void DrawOverlay()
    {
        if (!this.IsDresserOpen() || this.IsPlateOpen())
        {
            return;
        }

        this.RefreshCandidatesIfNeeded();

        if (this.ShowOverlayOnlyWhenEligible
            && this.candidates.Count == 0
            && this.step == QueueStep.Idle)
        {
            return;
        }

        var dresserAddon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
        if (dresserAddon.IsNull)
        {
            return;
        }

        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport().Pos + dresserAddon.Position + new Vector2(MathF.Max(0f, dresserAddon.ScaledWidth - 12f), 9f),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(350f, 0f), ImGuiCond.Always);

        if (!ImGui.Begin("Glamour Outfit Compactor###XelsTweaksGlamourOutfitCompactor", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        this.DrawOverlayContents();
        ImGui.End();
    }

    private void DrawOverlayContents()
    {
        ImGui.TextUnformatted("Glamour Outfit Compactor");
        ImGui.Separator();

        ImGui.TextUnformatted($"Eligible outfits: {this.candidates.Count}");
        if (this.step == QueueStep.Idle && this.candidates.Count > 0)
        {
            var glamourPrismCost = this.candidates.Sum(candidate => candidate.GlamourPrismCost);
            var glamourPrismsAvailable = this.CountInventoryItem(GlamourPrismItemId);
            if (glamourPrismsAvailable < glamourPrismCost)
            {
                ImGui.TextColored(WarningColor, $"Glamour Prisms: {glamourPrismsAvailable} / {glamourPrismCost}");
            }
            else
            {
                ImGui.TextUnformatted($"Glamour Prisms: {glamourPrismsAvailable} / {glamourPrismCost}");
            }
        }

        if (this.queue.Count > 0)
        {
            ImGui.TextUnformatted($"Progress: {this.completedOutfits} / {this.queue.Count}");
        }

        if (this.lastError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);
            ImGui.TextWrapped(this.lastError);
            ImGui.PopStyleColor();
        }
        else if (this.step == QueueStep.Complete)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, SuccessColor);
            ImGui.TextWrapped(this.status);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextWrapped(this.status);
        }

        ImGui.Spacing();

        switch (this.step)
        {
            case QueueStep.Idle:
                if (this.candidates.Count == 0)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Button("Convert eligible outfits"))
                {
                    this.StartQueue();
                }

                if (this.candidates.Count == 0)
                {
                    ImGui.EndDisabled();
                }

                ImGui.SameLine();
                if (ImGui.Button("Rescan"))
                {
                    this.MarkCandidatesDirty();
                    this.RefreshCandidatesIfNeeded(true);
                }

                break;

            case QueueStep.WaitingForDyedConfirmation:
                ImGui.TextColored(WarningColor, "This outfit may include dyed pieces.");
                if (ImGui.Button("Continue"))
                {
                    this.EnterStep(QueueStep.RestoringItems, $"Restoring {this.currentOutfit?.Name ?? "outfit"}.");
                }

                ImGui.SameLine();
                if (ImGui.Button("Skip outfit"))
                {
                    this.SkipCurrentOutfit("Skipped dyed outfit.");
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    this.ResetQueue("Cancelled.");
                }

                break;

            case QueueStep.Error:
                if (ImGui.Button("Reset"))
                {
                    this.ResetQueue("Idle.");
                    this.MarkCandidatesDirty();
                }

                break;

            case QueueStep.Complete:
                if (ImGui.Button("Reset"))
                {
                    this.ResetQueue("Idle.");
                    this.MarkCandidatesDirty();
                }

                break;

            default:
                if (ImGui.Button(this.queuePaused ? "Resume" : "Pause"))
                {
                    this.queuePaused = !this.queuePaused;
                    this.status = this.queuePaused ? "Paused." : $"Resuming {this.currentOutfit?.Name ?? "queue"}.";
                    this.nextActionAt = DateTimeOffset.UtcNow + ActionDelay;
                    this.stepStartedAt = DateTimeOffset.UtcNow;
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    this.ResetQueue("Cancelled.");
                }

                break;
        }

        if (this.currentOutfit != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Current: {this.currentOutfit.Name}");
            ImGui.TextDisabled($"{this.currentOutfit.SelectionItems.Count} piece{(this.currentOutfit.SelectionItems.Count == 1 ? string.Empty : "s")}{(this.currentOutfit.IsMerge ? " (merge)" : string.Empty)}");
        }

        if (this.skippedOutfits > 0)
        {
            ImGui.TextDisabled($"Skipped: {this.skippedOutfits}");
        }
    }

    private void StartQueue()
    {
        this.RefreshCandidatesIfNeeded(true);
        this.queue.Clear();
        this.queue.AddRange(this.candidates.Select(candidate => new QueuedOutfit(candidate)));
        this.completedOutfits = 0;
        this.skippedOutfits = 0;
        this.currentOutfit = null;
        this.waitingForRestoredItemId = null;
        this.queuePaused = false;
        this.lastError = null;

        if (this.queue.Count == 0)
        {
            this.ResetQueue("No eligible outfits found.");
            return;
        }

        if (this.TryStopQueueWithoutEnoughGlamourPrisms())
        {
            return;
        }

        var glamourPrismCost = this.GetRemainingGlamourPrismCost();
        this.status = $"Queued {this.queue.Count} eligible outfit{(this.queue.Count == 1 ? string.Empty : "s")}.";
        if (glamourPrismCost > 0)
        {
            this.status += $" Requires {glamourPrismCost} Glamour Prism{(glamourPrismCost == 1 ? string.Empty : "s")}.";
        }

        this.BeginNextOutfit();
    }

    private void BeginNextOutfit()
    {
        this.waitingForRestoredItemId = null;
        this.ClearRestoreRetryState();
        this.pendingSetConvertSlot = null;
        this.pendingSetConvertItemId = null;

        if (this.queue.Count == 0)
        {
            this.currentOutfit = null;
            var skippedMessage = this.skippedOutfits == 0
                ? string.Empty
                : $" Skipped {this.skippedOutfits} outfit{(this.skippedOutfits == 1 ? string.Empty : "s")}.";
            this.EnterStep(QueueStep.Complete, $"Converted {this.completedOutfits} outfit{(this.completedOutfits == 1 ? string.Empty : "s")}.{skippedMessage}");
            this.MarkCandidatesDirty();
            return;
        }

        this.currentOutfit = this.queue[0];
        this.queue.RemoveAt(0);

        if (this.TrySkipCurrentOutfitWithoutEnoughInventorySpace(this.currentOutfit))
        {
            return;
        }

        if (this.TryStopQueueWithoutEnoughGlamourPrisms())
        {
            return;
        }

        if (this.currentOutfit.RequiresConfirmation && this.ConfirmDyedOutfits)
        {
            this.EnterStep(QueueStep.WaitingForDyedConfirmation, $"{this.currentOutfit.Name} may include dyed pieces. Confirm before continuing.");
            return;
        }

        this.EnterStep(QueueStep.RestoringItems, $"Restoring {this.currentOutfit.Name}.");
    }

    private void AdvanceQueue()
    {
        if (this.currentOutfit == null)
        {
            this.BeginNextOutfit();
            return;
        }

        switch (this.step)
        {
            case QueueStep.RestoringItems:
                this.RestoreNextItem(this.currentOutfit);
                break;
            case QueueStep.WaitingForRestore:
                this.WaitForRestoredItem(this.currentOutfit);
                break;
            case QueueStep.WaitingForSetRestore:
                this.WaitForStoredSetRestore(this.currentOutfit);
                break;
            case QueueStep.OpeningSetConvert:
                this.OpenSetConvert(this.currentOutfit);
                break;
            case QueueStep.FillingSetConvert:
                this.FillSetConvert(this.currentOutfit);
                break;
            case QueueStep.ValidatingSetConvert:
                this.ValidateSetConvert(this.currentOutfit);
                break;
            case QueueStep.StoringOutfit:
                this.StoreOutfit(this.currentOutfit);
                break;
            case QueueStep.ConfirmingStore:
                this.ConfirmStoreOutfit(this.currentOutfit);
                break;
            case QueueStep.WaitingForStore:
                this.WaitForStoredOutfit(this.currentOutfit);
                break;
        }
    }

    private void RestoreNextItem(QueuedOutfit outfit)
    {
        if (!this.HasStartedRestoring(outfit) && this.TrySkipCurrentOutfitWithoutEnoughInventorySpace(outfit))
        {
            return;
        }

        if (outfit.IsMerge && !outfit.StoredSetRestored)
        {
            this.RestoreStoredSetItems(outfit);
            return;
        }

        while (outfit.NextRestoreIndex < outfit.RestoreItems.Count
            && this.TryFindInventoryItem(outfit.RestoreItems[outfit.NextRestoreIndex].ItemId, out var existingSlot))
        {
            this.AddRestoredSlot(outfit, existingSlot);
            outfit.NextRestoreIndex++;
        }

        if (outfit.NextRestoreIndex >= outfit.RestoreItems.Count)
        {
            if (!this.TryValidateRestoredInventory(outfit, out var error))
            {
                this.FailQueue(error ?? $"Could not find every restored piece for {outfit.Name} in inventory.");
                return;
            }

            this.EnterStep(QueueStep.OpeningSetConvert, $"Opening Outfit Glamour Creation for {outfit.Name}.");
            return;
        }

        var item = outfit.RestoreItems[outfit.NextRestoreIndex];
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.status = "Waiting for Glamour Dresser data before restoring an outfit piece.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var itemIndex = this.FindPrismBoxItemIndex(item.ItemId);
        if (itemIndex < 0)
        {
            this.ScheduleItemRestoreRetry(
                item.ItemId,
                $"Waiting for {item.ItemId} to be available in the Glamour Dresser before restoring {outfit.Name}.",
                $"Could not find {item.ItemId} in the Glamour Dresser anymore.");
            return;
        }

        if (!manager->RestorePrismBoxItem((uint)itemIndex))
        {
            this.ScheduleItemRestoreRetry(
                item.ItemId,
                $"The game refused to restore {item.ItemId}; waiting briefly before retrying.",
                "The game refused to restore an outfit piece. Inventory may be full or a unique item may already be owned.");
            return;
        }

        this.ClearRestoreRetryState();
        this.waitingForRestoredItemId = item.ItemId;
        this.EnterStep(QueueStep.WaitingForRestore, $"Waiting for {item.ItemId} to return to inventory.");
    }

    private void RestoreStoredSetItems(QueuedOutfit outfit)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.status = "Waiting for Glamour Dresser data before restoring existing outfit pieces.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var setItemIndex = this.FindPrismBoxItemIndex(outfit.SetItemId);
        if (setItemIndex < 0)
        {
            this.ScheduleStoredSetRestoreRetry(
                outfit,
                $"Waiting for stored outfit {outfit.Name} to be available in the Glamour Dresser.",
                $"Could not find the stored outfit {outfit.SetItemId} in the Glamour Dresser anymore.");
            return;
        }

        byte* restoreBits = stackalloc byte[2];
        for (var i = 0; i < 2; i++)
        {
            restoreBits[i] = 0;
        }

        foreach (var slotIndex in outfit.StoredSlotIndexes)
        {
            if (!manager->IsSetSlotUnlocked((uint)setItemIndex, slotIndex))
            {
                this.FailQueue($"Stored outfit {outfit.Name} changed before it could be merged.");
                return;
            }

            restoreBits[slotIndex / 8] |= (byte)(1 << (slotIndex % 8));
        }

        if (!manager->RestorePrismBoxSetItem((uint)setItemIndex, restoreBits))
        {
            this.ScheduleStoredSetRestoreRetry(
                outfit,
                $"The game refused to restore existing pieces for {outfit.Name}; waiting briefly before retrying.",
                "The game refused to restore the existing stored outfit. Inventory may be full or a unique item may already be owned.");
            return;
        }

        this.ClearRestoreRetryState();
        outfit.StoredSetRestored = true;
        this.EnterStep(QueueStep.WaitingForSetRestore, $"Waiting for existing {outfit.Name} outfit pieces to return to inventory.");
    }

    private void WaitForStoredSetRestore(QueuedOutfit outfit)
    {
        foreach (var itemId in outfit.StoredSetItemIds)
        {
            if (!this.TryFindInventoryItem(itemId, out var restoredSlot))
            {
                this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
                return;
            }

            this.AddRestoredSlot(outfit, restoredSlot);
        }

        if (this.FindPrismBoxItemIndex(outfit.SetItemId) >= 0)
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.EnterStep(QueueStep.RestoringItems, $"Restoring loose pieces for {outfit.Name}.");
    }

    private void WaitForRestoredItem(QueuedOutfit outfit)
    {
        if (this.waitingForRestoredItemId == null)
        {
            this.EnterStep(QueueStep.RestoringItems, $"Restoring {outfit.Name}.");
            return;
        }

        if (!this.TryFindInventoryItem(this.waitingForRestoredItemId.Value, out var restoredSlot))
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.AddRestoredSlot(outfit, restoredSlot);
        outfit.NextRestoreIndex++;
        this.waitingForRestoredItemId = null;
        this.ClearRestoreRetryState();
        this.EnterStep(QueueStep.RestoringItems, $"Restoring {outfit.Name}.");
    }

    private void AddRestoredSlot(QueuedOutfit outfit, InventorySlot slot)
    {
        if (outfit.RestoredSlots.Any(existing => existing.InventoryType == slot.InventoryType && existing.Slot == slot.Slot))
        {
            return;
        }

        outfit.RestoredSlots.Add(slot);
    }

    private bool TryValidateRestoredInventory(QueuedOutfit outfit, out string? error)
    {
        foreach (var item in outfit.SelectionItems)
        {
            if (!this.TryFindInventoryItem(item.ItemId, out var slot))
            {
                error = $"Missing restored item {item.ItemId} from inventory. Stopped before opening Outfit Glamour Creation.";
                return false;
            }

            this.AddRestoredSlot(outfit, slot);
        }

        error = null;
        return true;
    }

    private void OpenSetConvert(QueuedOutfit outfit)
    {
        var sourceItem = outfit.SelectionItems.FirstOrDefault(item => this.TryFindInventoryItem(item.ItemId, out _));
        if (sourceItem == null || !this.TryFindInventoryItem(sourceItem.ItemId, out var sourceSlot))
        {
            this.FailQueue("Could not find a restored outfit piece in inventory.");
            return;
        }

        var dresserAddon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
        if (dresserAddon.IsNull || dresserAddon.Address == IntPtr.Zero)
        {
            this.FailQueue("Glamour Dresser addon is not available.");
            return;
        }

        var agent = AgentMiragePrismPrismSetConvert.Instance();
        if (agent == null)
        {
            this.FailQueue("Outfit Glamour Creation agent is not available.");
            return;
        }

        try
        {
            this.Services.Log.Debug(
                "Glamour Outfit Compactor opening Outfit Glamour Creation for {OutfitName} from item {ItemId} at {InventoryType}:{Slot}",
                outfit.Name,
                sourceSlot.ItemId,
                sourceSlot.InventoryType,
                sourceSlot.Slot);
            this.openSetConvertAddon(agent, sourceSlot.ItemId, sourceSlot.InventoryType, (int)sourceSlot.Slot, dresserAddon.Id, true);
        }
        catch (Exception ex)
        {
            this.FailQueue("Failed to open Outfit Glamour Creation for the restored outfit piece.");
            this.Services.Log.Warning(ex, "Failed to open Outfit Glamour Creation for {OutfitName} from item {ItemId}", outfit.Name, sourceSlot.ItemId);
            return;
        }

        this.EnterStep(QueueStep.FillingSetConvert, $"Filling Outfit Glamour Creation for {outfit.Name}.");
    }

    private void FillSetConvert(QueuedOutfit outfit)
    {
        if (!this.IsSetConvertOpen())
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (!this.TryFillSetConvertItems(outfit, out var error))
        {
            if (error == null)
            {
                this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
                return;
            }

            this.FailQueue(error);
            return;
        }

        this.EnterStep(QueueStep.ValidatingSetConvert, $"Validating selected pieces for {outfit.Name}.");
    }

    private void ValidateSetConvert(QueuedOutfit outfit)
    {
        if (!this.IsSetConvertOpen())
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryValidateSetConvertItems(outfit, out var error))
        {
            this.EnterStep(QueueStep.StoringOutfit, $"Storing {outfit.Name} as an outfit.");
            return;
        }

        if (error != null)
        {
            this.FailQueue(error);
            return;
        }

        if (!this.TryFillSetConvertItems(outfit, out error))
        {
            if (error == null)
            {
                this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
                return;
            }

            this.FailQueue(error);
            return;
        }

        this.status = $"Revalidating selected pieces for {outfit.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
    }

    private void StoreOutfit(QueuedOutfit outfit)
    {
        if (!this.AttemptNativeStoreClick)
        {
            this.FailQueue("Automatic native Store click is disabled. The restored items are selected in the Outfit Glamour Creation window.");
            return;
        }

        if (!this.TryValidateSetConvertItems(outfit, out var error))
        {
            if (error != null)
            {
                this.FailQueue(error);
                return;
            }

            this.EnterStep(QueueStep.ValidatingSetConvert, $"Revalidating selected pieces for {outfit.Name}.");
            return;
        }

        var setConvertAddon = this.Services.GameGui.GetAddonByName(SetConvertAddonName, 1);
        if (setConvertAddon.IsNull || setConvertAddon.Address == IntPtr.Zero || !setConvertAddon.IsReady || !setConvertAddon.IsVisible)
        {
            this.FailQueue("Outfit Glamour Creation addon is not ready for the native Store button.");
            return;
        }

        if (!this.TryClickButton((AtkUnitBase*)setConvertAddon.Address, StoreAsGlamourButtonId))
        {
            this.FailQueue("The native Store as Glamour button was not available.");
            return;
        }

        this.EnterStep(QueueStep.ConfirmingStore, $"Confirming storage for {outfit.Name}.");
    }

    private void ConfirmStoreOutfit(QueuedOutfit outfit)
    {
        var manager = MirageManager.Instance();
        if (manager != null && manager->PrismBoxLoaded && manager->PrismBoxItemIds.Contains(outfit.SetItemId))
        {
            this.EnterStep(QueueStep.WaitingForStore, $"Waiting for {outfit.Name} to appear in the Glamour Dresser.");
            return;
        }

        var confirmAddon = this.Services.GameGui.GetAddonByName(SetConvertConfirmAddonName, 1);
        if (confirmAddon.IsNull || confirmAddon.Address == IntPtr.Zero || !confirmAddon.IsReady || !confirmAddon.IsVisible)
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var addon = (AtkUnitBase*)confirmAddon.Address;
        if (this.TryClickButton(
            addon,
            ConfirmYesButtonId,
            () => this.EnterStep(QueueStep.WaitingForStore, $"Waiting for {outfit.Name} to appear in the Glamour Dresser.")))
        {
            return;
        }

        if (this.confirmCheckBoxAttempts < 2 && this.TryClickCheckBox(addon, ConfirmStoreAsOutfitCheckBoxId))
        {
            this.confirmCheckBoxAttempts++;
            this.status = "Waiting for the native confirmation to enable Yes after selecting Store as Outfit Glamour.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryForceEnableConfirmYes(addon, ConfirmStoreAsOutfitCheckBoxId, ConfirmYesButtonId))
        {
            this.status = "Using guarded fallback to enable the native Outfit Glamour Creation confirmation.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryNotifyCheckedCheckBox(addon, ConfirmStoreAsOutfitCheckBoxId))
        {
            this.status = "Waiting for the native confirmation to enable Yes after selecting Store as Outfit Glamour.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.status = "Waiting for the native Outfit Glamour Creation confirmation controls.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
    }

    private void WaitForStoredOutfit(QueuedOutfit outfit)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (!manager->PrismBoxItemIds.Contains(outfit.SetItemId))
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (outfit.RestoredSlots.Count == 0 || outfit.RestoredSlots.Any(slot => !this.IsInventorySlotConsumed(slot)))
        {
            this.status = $"Waiting for restored pieces to leave inventory after storing {outfit.Name}.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.completedOutfits++;
        this.MarkCandidatesDirty();
        this.BeginNextOutfit();
    }

    private bool TryFillSetConvertItems(QueuedOutfit outfit, out string? error)
    {
        error = null;

        var setConvertAddon = this.Services.GameGui.GetAddonByName(SetConvertAddonName, 1);
        if (setConvertAddon.IsNull || setConvertAddon.Address == IntPtr.Zero || !setConvertAddon.IsReady || !setConvertAddon.IsVisible)
        {
            return false;
        }

        var addon = (AtkUnitBase*)setConvertAddon.Address;
        if (!this.TryReadSetConvertUiItems(addon, out var dataItems, out error))
        {
            return false;
        }

        var expectedItems = outfit.SelectionItems.Select(item => item.ItemId).ToHashSet();
        var setItems = outfit.SetItemIds.ToHashSet();

        if (dataItems.Length == 0)
        {
            return false;
        }

        if (dataItems.Any(item => !setItems.Contains(item.ItemId)))
        {
            error = "Outfit Glamour Creation opened for a different outfit. Stopped before storing.";
            return false;
        }

        if (this.pendingSetConvertSlot != null && this.TryGetContextIconMenu(out var contextMenu))
        {
            var pendingSlot = this.pendingSetConvertSlot.Value;
            var pendingItemId = this.pendingSetConvertItemId.GetValueOrDefault();
            this.FireContextIconMenuCallback(contextMenu);
            this.Services.Log.Debug(
                "Glamour Outfit Compactor selected handover context menu for {OutfitName}: item {ItemId}, UI slot {Slot}",
                outfit.Name,
                pendingItemId,
                pendingSlot);
            this.pendingSetConvertSlot = null;
            this.pendingSetConvertItemId = null;
            return false;
        }

        if (expectedItems.Any(itemId => dataItems.All(item => item.ItemId != itemId)))
        {
            return false;
        }

        foreach (var item in dataItems)
        {
            if (!expectedItems.Contains(item.ItemId))
            {
                continue;
            }

            if (!this.TryFindInventoryItem(item.ItemId, out var slot))
            {
                error = $"Missing restored item {item.ItemId} from inventory.";
                return false;
            }

            if (this.IsSetConvertUiItemSelected(item, slot))
            {
                continue;
            }

            this.FireSetConvertHandOverCallback(addon, item.Index);
            this.pendingSetConvertSlot = item.Index;
            this.pendingSetConvertItemId = item.ItemId;
            this.status = $"Selecting restored item {item.ItemId} for {outfit.Name}.";
            this.Services.Log.Debug(
                "Glamour Outfit Compactor requested native handover for {OutfitName}: item {ItemId}, UI slot {UiSlot}, inventory {InventoryType}:{InventorySlot}",
                outfit.Name,
                item.ItemId,
                item.Index,
                slot.InventoryType,
                slot.Slot);
            return false;
        }

        this.Services.Log.Debug(
            "Glamour Outfit Compactor selected all {ItemCount} pieces for {OutfitName}",
            expectedItems.Count,
            outfit.Name);
        return true;
    }

    private string GetSetConvertFillDiagnostic(QueuedOutfit outfit)
    {
        var setConvertAddon = this.Services.GameGui.GetAddonByName(SetConvertAddonName, 1);
        if (setConvertAddon.IsNull || setConvertAddon.Address == IntPtr.Zero)
        {
            return "Outfit Glamour Creation addon was not available.";
        }

        var addon = (AtkUnitBase*)setConvertAddon.Address;
        if (!this.TryReadSetConvertUiItems(addon, out var dataItems, out var error))
        {
            return error ?? "Outfit Glamour Creation UI data was not loaded.";
        }

        var expectedItems = outfit.SelectionItems.Select(item => item.ItemId).ToArray();
        var rowDetails = dataItems
            .Select(item => $"{item.Index}:{item.ItemId}@{item.InventoryType}:{item.Slot}/flag{item.Flag}")
            .ToArray();
        var dataItemIds = dataItems.Select(item => item.ItemId).ToArray();

        if (dataItems.Length == 0)
        {
            return $"Native outfit UI data had no item rows; expected {string.Join(", ", expectedItems)}.";
        }

        var missingItems = expectedItems
            .Where(itemId => !dataItemIds.Contains(itemId))
            .ToArray();
        if (missingItems.Length > 0)
        {
            return $"Native outfit UI data was missing {string.Join(", ", missingItems)}; rows were {string.Join(", ", rowDetails)}.";
        }

        return $"Native outfit UI rows were present: {string.Join(", ", rowDetails)}.";
    }

    private bool TryValidateSetConvertItems(QueuedOutfit outfit, out string? error)
    {
        error = null;

        var setConvertAddon = this.Services.GameGui.GetAddonByName(SetConvertAddonName, 1);
        if (setConvertAddon.IsNull || setConvertAddon.Address == IntPtr.Zero || !setConvertAddon.IsReady || !setConvertAddon.IsVisible)
        {
            return false;
        }

        var addon = (AtkUnitBase*)setConvertAddon.Address;
        if (!this.TryReadSetConvertUiItems(addon, out var dataItems, out error))
        {
            return false;
        }

        var expectedItems = outfit.SelectionItems.Select(item => item.ItemId).ToHashSet();
        var setItems = outfit.SetItemIds.ToHashSet();

        if (dataItems.Length == 0)
        {
            return false;
        }

        if (dataItems.Any(item => !setItems.Contains(item.ItemId)))
        {
            error = "Outfit Glamour Creation opened for a different outfit. Stopped before storing.";
            return false;
        }

        if (dataItems.Any(item => !expectedItems.Contains(item.ItemId) && item.InventoryType != (uint)InventoryType.Invalid))
        {
            return false;
        }

        foreach (var expectedItemId in expectedItems)
        {
            var selectedItem = dataItems.FirstOrDefault(item => item.ItemId == expectedItemId);
            if (selectedItem.ItemId == 0 || selectedItem.InventoryType == (uint)InventoryType.Invalid)
            {
                return false;
            }

            if (!this.TryFindInventoryItem(expectedItemId, out var slot))
            {
                error = $"Missing restored item {expectedItemId} from inventory.";
                return false;
            }

            if (!this.IsSetConvertUiItemSelected(selectedItem, slot))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadSetConvertUiItems(AtkUnitBase* addon, out SetConvertUiItem[] items, out string? error)
    {
        items = [];
        error = null;

        if (!this.TryReadAtkUInt(addon, SetConvertUiItemCountOffset, out var itemCountValue))
        {
            return false;
        }

        var itemCount = (int)itemCountValue;
        if (itemCount <= 0)
        {
            return false;
        }

        var requiredValueCount = SetConvertUiItemsOffset + (itemCount * SetConvertUiItemStride);
        if (addon->AtkValuesCount < requiredValueCount)
        {
            error = $"Outfit Glamour Creation UI data had {addon->AtkValuesCount} values; expected at least {requiredValueCount}.";
            return false;
        }

        var readItems = new List<SetConvertUiItem>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            var offset = SetConvertUiItemsOffset + (i * SetConvertUiItemStride);
            if (!this.TryReadAtkUInt(addon, offset, out var itemId))
            {
                continue;
            }

            if (itemId == 0)
            {
                continue;
            }

            if (!this.TryReadAtkUInt(addon, offset + 4, out var inventoryType)
                || !this.TryReadAtkUInt(addon, offset + 5, out var slot)
                || !this.TryReadAtkUInt(addon, offset + 6, out var flag))
            {
                error = $"Outfit Glamour Creation UI row {i} was not readable.";
                return false;
            }

            readItems.Add(new SetConvertUiItem(i, itemId, inventoryType, slot, flag));
        }

        items = readItems.ToArray();
        return true;
    }

    private bool TryReadAtkUInt(AtkUnitBase* addon, int index, out uint value)
    {
        value = 0;
        if (index < 0 || (uint)index >= addon->AtkValuesCount)
        {
            return false;
        }

        var atkValue = addon->AtkValues[index];
        if (atkValue.Type == AtkValueType.UInt)
        {
            value = atkValue.UInt;
            return true;
        }

        if (atkValue.Type == AtkValueType.Int && atkValue.Int >= 0)
        {
            value = (uint)atkValue.Int;
            return true;
        }

        return false;
    }

    private bool IsSetConvertUiItemSelected(SetConvertUiItem item, InventorySlot slot)
    {
        return item.InventoryType == (uint)slot.InventoryType && item.Slot == slot.Slot;
    }

    private bool TryGetContextIconMenu(out AtkUnitBase* addon)
    {
        var contextMenu = this.Services.GameGui.GetAddonByName(ContextIconMenuAddonName, 1);
        if (contextMenu.IsNull || contextMenu.Address == IntPtr.Zero || !contextMenu.IsReady || !contextMenu.IsVisible)
        {
            addon = null;
            return false;
        }

        addon = (AtkUnitBase*)contextMenu.Address;
        return true;
    }

    private void FireSetConvertHandOverCallback(AtkUnitBase* addon, int slot)
    {
        var values = stackalloc AtkValue[2];
        values[0] = this.CreateAtkInt(SetConvertHandOverCallbackId);
        values[1] = this.CreateAtkInt(slot);
        addon->FireCallback(2, values, true);
    }

    private void FireContextIconMenuCallback(AtkUnitBase* addon)
    {
        var values = stackalloc AtkValue[5];
        values[0] = this.CreateAtkInt(0);
        values[1] = this.CreateAtkInt(0);
        values[2] = this.CreateAtkUInt(SetConvertContextMenuActionId);
        values[3] = this.CreateAtkUInt(0);
        values[4] = this.CreateAtkInt(0);
        addon->FireCallback(5, values, true);
    }

    private AtkValue CreateAtkInt(int value)
    {
        return new AtkValue
        {
            Type = AtkValueType.Int,
            Int = value
        };
    }

    private AtkValue CreateAtkUInt(uint value)
    {
        return new AtkValue
        {
            Type = AtkValueType.UInt,
            UInt = value
        };
    }

    private void ScheduleItemRestoreRetry(uint itemId, string retryStatus, string finalError)
    {
        if (this.restoreRetryItemId != itemId)
        {
            this.restoreRetryItemId = itemId;
            this.restoreRetryAttempts = 0;
        }

        this.restoreRetryAttempts++;
        if (this.restoreRetryAttempts >= MaxRestoreAttempts)
        {
            this.FailQueue($"{finalError} Retried {MaxRestoreAttempts} times.");
            return;
        }

        this.status = $"{retryStatus} Retrying attempt {this.restoreRetryAttempts + 1}/{MaxRestoreAttempts}.";
        this.nextActionAt = DateTimeOffset.UtcNow + RestoreRetryDelay;
        this.MarkCandidatesDirty();
        this.Services.Log.Debug(
            "Glamour Outfit Compactor restore retry for item {ItemId}: attempt {NextAttempt}/{MaxAttempts}",
            itemId,
            this.restoreRetryAttempts + 1,
            MaxRestoreAttempts);
    }

    private void ScheduleStoredSetRestoreRetry(QueuedOutfit outfit, string retryStatus, string finalError)
    {
        this.storedSetRestoreRetryAttempts++;
        if (this.storedSetRestoreRetryAttempts >= MaxRestoreAttempts)
        {
            this.FailQueue($"{finalError} Retried {MaxRestoreAttempts} times.");
            return;
        }

        this.status = $"{retryStatus} Retrying attempt {this.storedSetRestoreRetryAttempts + 1}/{MaxRestoreAttempts}.";
        this.nextActionAt = DateTimeOffset.UtcNow + RestoreRetryDelay;
        this.MarkCandidatesDirty();
        this.Services.Log.Debug(
            "Glamour Outfit Compactor stored outfit restore retry for {OutfitName} ({SetItemId}): attempt {NextAttempt}/{MaxAttempts}",
            outfit.Name,
            outfit.SetItemId,
            this.storedSetRestoreRetryAttempts + 1,
            MaxRestoreAttempts);
    }

    private void ClearRestoreRetryState()
    {
        this.restoreRetryItemId = null;
        this.restoreRetryAttempts = 0;
        this.storedSetRestoreRetryAttempts = 0;
    }

    private void SkipCurrentOutfit(string message)
    {
        this.skippedOutfits++;
        this.status = message;
        this.currentOutfit = null;
        this.ClearRestoreRetryState();
        this.pendingSetConvertSlot = null;
        this.pendingSetConvertItemId = null;
        this.BeginNextOutfit();
    }

    private void ResetQueue(string message)
    {
        this.queue.Clear();
        this.currentOutfit = null;
        this.completedOutfits = 0;
        this.skippedOutfits = 0;
        this.waitingForRestoredItemId = null;
        this.ClearRestoreRetryState();
        this.pendingSetConvertSlot = null;
        this.pendingSetConvertItemId = null;
        this.queuePaused = false;
        this.confirmCheckBoxAttempts = 0;
        this.step = QueueStep.Idle;
        this.stepStartedAt = DateTimeOffset.UtcNow;
        this.nextActionAt = DateTimeOffset.MinValue;
        this.status = message;
        this.lastError = null;
    }

    private void FailQueue(string error)
    {
        this.queue.Clear();
        this.waitingForRestoredItemId = null;
        this.ClearRestoreRetryState();
        this.pendingSetConvertSlot = null;
        this.pendingSetConvertItemId = null;
        this.queuePaused = false;
        this.confirmCheckBoxAttempts = 0;
        this.step = QueueStep.Error;
        this.stepStartedAt = DateTimeOffset.UtcNow;
        this.nextActionAt = DateTimeOffset.MinValue;
        this.lastError = error;
        this.status = error;
        this.Services.Log.Warning("Glamour Outfit Compactor stopped: {Error}", error);
    }

    private void EnterStep(QueueStep nextStep, string nextStatus)
    {
        var now = DateTimeOffset.UtcNow;
        this.step = nextStep;
        this.status = nextStatus;
        this.stepStartedAt = now;
        this.nextActionAt = now + ActionDelay;
        if (nextStep != QueueStep.ConfirmingStore)
        {
            this.confirmCheckBoxAttempts = 0;
        }
    }

    private void RefreshCandidatesIfNeeded(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < this.nextCandidateRefreshAt)
        {
            return;
        }

        if (!force && !this.candidatesDirty && !this.PrismBoxSnapshotChanged())
        {
            return;
        }

        this.nextCandidateRefreshAt = now + CandidateRefreshDelay;
        this.candidatesDirty = false;
        this.RebuildCandidates();
    }

    private void RebuildCandidates()
    {
        this.candidates.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.lastPrismBoxItemIds = [];
            if (this.step == QueueStep.Idle)
            {
                this.status = "Waiting for Glamour Dresser data.";
            }

            return;
        }

        this.lastPrismBoxItemIds = manager->PrismBoxItemIds.ToArray();

        var itemIndexes = new Dictionary<uint, int>();
        var setSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItem>();
        var lookupSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItemLookup>();
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();

        for (var i = 0; i < this.lastPrismBoxItemIds.Length; i++)
        {
            var itemId = this.lastPrismBoxItemIds[i];
            if (itemId == 0)
            {
                continue;
            }

            itemIndexes.TryAdd(itemId, i);
        }

        var rawCandidates = new List<OutfitCandidate>();
        var checkedSetIds = new HashSet<uint>();
        foreach (var itemId in itemIndexes.Keys)
        {
            if (!lookupSheet.TryGetRow(itemId, out var lookupRow))
            {
                continue;
            }

            foreach (var setItem in lookupRow.Item)
            {
                var setItemId = setItem.RowId;
                if (setItemId == 0 || !setItem.IsValid || !setSheet.TryGetRow(setItemId, out var setRow))
                {
                    continue;
                }

                var setSlots = this.GetSetItems(setRow);
                if (setSlots.Length == 0)
                {
                    continue;
                }

                if (setSlots.All(slot => slot.ItemId != itemId))
                {
                    continue;
                }

                if (!checkedSetIds.Add(setItemId))
                {
                    continue;
                }

                if (!itemSheet.TryGetRow(setItemId, out var itemRow))
                {
                    continue;
                }

                var name = itemRow.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var setItemIds = setSlots.Select(slot => slot.ItemId).ToArray();
                var rowIdIsSetPiece = setItemIds.Contains(setItemId);
                var storedSetIndex = -1;
                var hasStoredOutfit = !rowIdIsSetPiece && itemIndexes.TryGetValue(setItemId, out storedSetIndex);
                var selectionItems = new List<CandidateItem>();
                var restoreItems = new List<CandidateItem>();
                var storedSlotIndexes = new List<int>();
                var storedSetItemIds = new List<uint>();
                var missingSlot = false;

                foreach (var setSlot in setSlots)
                {
                    var hasLooseItem = this.TryCreateCandidateItem(setSlot.ItemId, itemIndexes, manager, out var looseItem);
                    var storedInOutfit = hasStoredOutfit && manager->IsSetSlotUnlocked((uint)storedSetIndex, setSlot.SlotIndex);

                    if (!hasLooseItem && !storedInOutfit)
                    {
                        missingSlot = true;
                        break;
                    }

                    selectionItems.Add(looseItem ?? new CandidateItem(setSlot.ItemId, false));

                    if (storedInOutfit)
                    {
                        storedSlotIndexes.Add(setSlot.SlotIndex);
                        storedSetItemIds.Add(setSlot.ItemId);
                    }

                    if (hasLooseItem && !storedInOutfit)
                    {
                        restoreItems.Add(looseItem!);
                    }
                }

                if (missingSlot)
                {
                    continue;
                }

                if (hasStoredOutfit)
                {
                    if (storedSlotIndexes.Count == 0 || restoreItems.Count == 0)
                    {
                        continue;
                    }
                }
                else if (restoreItems.Count != setSlots.Length)
                {
                    continue;
                }

                rawCandidates.Add(new OutfitCandidate(
                    setItemId,
                    name,
                    setItemIds,
                    selectionItems,
                    restoreItems,
                    storedSlotIndexes.ToArray(),
                    storedSetItemIds.ToArray(),
                    selectionItems.Any(item => item.Dyed) || hasStoredOutfit));
            }
        }

        var reservedItems = new HashSet<uint>();
        foreach (var candidate in rawCandidates
            .OrderByDescending(candidate => candidate.SelectionItems.Count)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SetItemId))
        {
            var candidateReservedItems = candidate.RestoreItems.Select(item => item.ItemId).ToList();
            if (candidate.IsMerge)
            {
                candidateReservedItems.Add(candidate.SetItemId);
            }

            if (candidateReservedItems.Any(reservedItems.Contains))
            {
                continue;
            }

            this.candidates.Add(candidate);
            foreach (var itemId in candidateReservedItems)
            {
                reservedItems.Add(itemId);
            }
        }

        if (this.step == QueueStep.Idle)
        {
            var mergeCount = this.candidates.Count(candidate => candidate.IsMerge);
            if (this.candidates.Count == 0)
            {
                this.status = "No complete outfit candidates found.";
            }
            else
            {
                this.status = $"{this.candidates.Count} complete outfit{(this.candidates.Count == 1 ? string.Empty : "s")} can be compacted{(mergeCount == 0 ? "." : $" ({mergeCount} merge{(mergeCount == 1 ? string.Empty : "s")}).")}";
            }
        }
    }

    private bool TryCreateCandidateItem(uint itemId, Dictionary<uint, int> itemIndexes, MirageManager* manager, out CandidateItem? item)
    {
        item = null;
        if (itemId == 0 || !itemIndexes.TryGetValue(itemId, out var index))
        {
            return false;
        }

        var dyed = manager->PrismBoxStain0Ids[index] != 0 || manager->PrismBoxStain1Ids[index] != 0;
        item = new CandidateItem(itemId, dyed);
        return true;
    }

    private SetItemSlot[] GetSetItems(MirageStoreSetItem setRow)
    {
        uint[] itemIds =
        [
            setRow.MainHand.RowId,
            setRow.OffHand.RowId,
            setRow.Head.RowId,
            setRow.Body.RowId,
            setRow.Hands.RowId,
            setRow.Legs.RowId,
            setRow.Feet.RowId,
            setRow.Earrings.RowId,
            setRow.Necklace.RowId,
            setRow.Bracelets.RowId,
            setRow.Ring.RowId
        ];

        var items = new List<SetItemSlot>();
        for (var i = 0; i < itemIds.Length; i++)
        {
            if (itemIds[i] != 0)
            {
                items.Add(new SetItemSlot(i, itemIds[i]));
            }
        }

        return items.ToArray();
    }

    private bool PrismBoxSnapshotChanged()
    {
        var manager = MirageManager.Instance();
        return manager == null
            || !manager->PrismBoxLoaded
            || !manager->PrismBoxItemIds.SequenceEqual(this.lastPrismBoxItemIds);
    }

    private int FindPrismBoxItemIndex(uint itemId)
    {
        var manager = MirageManager.Instance();
        return manager == null || !manager->PrismBoxLoaded
            ? -1
            : manager->PrismBoxItemIds.IndexOf(itemId);
    }

    private bool HasStartedRestoring(QueuedOutfit outfit)
    {
        return outfit.StoredSetRestored || outfit.RestoredSlots.Count > 0 || outfit.NextRestoreIndex > 0 || this.waitingForRestoredItemId != null;
    }

    private bool TryStopQueueWithoutEnoughGlamourPrisms()
    {
        var neededPrisms = this.GetRemainingGlamourPrismCost();
        if (neededPrisms == 0)
        {
            return false;
        }

        var availablePrisms = this.CountInventoryItem(GlamourPrismItemId);
        if (availablePrisms >= neededPrisms)
        {
            return false;
        }

        var remainingOutfits = (this.currentOutfit == null ? 0 : 1) + this.queue.Count;
        this.FailQueue($"Need {neededPrisms} Glamour Prism{(neededPrisms == 1 ? string.Empty : "s")} to finish {remainingOutfits} queued outfit{(remainingOutfits == 1 ? string.Empty : "s")}, but only {availablePrisms} available.");
        return true;
    }

    private int GetRemainingGlamourPrismCost()
    {
        return (this.currentOutfit?.GlamourPrismCost ?? 0) + this.queue.Sum(outfit => outfit.GlamourPrismCost);
    }

    private bool TrySkipCurrentOutfitWithoutEnoughInventorySpace(QueuedOutfit outfit)
    {
        if (this.HasEnoughInventorySpaceForOutfit(outfit, out var neededSlots, out var availableSlots))
        {
            return false;
        }

        this.SkipCurrentOutfit($"Skipped {outfit.Name}; needs {neededSlots} free inventory slot{(neededSlots == 1 ? string.Empty : "s")}, but only {availableSlots} available.");
        return true;
    }

    private bool HasEnoughInventorySpaceForOutfit(QueuedOutfit outfit, out int neededSlots, out int availableSlots)
    {
        neededSlots = outfit.SelectionItems.Count(item => !this.TryFindInventoryItem(item.ItemId, out _));
        availableSlots = this.CountAvailableInventorySlots();

        return availableSlots >= neededSlots;
    }

    private int CountAvailableInventorySlots()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return 0;
        }

        var availableSlots = 0;
        for (var inventoryTypeValue = (int)InventoryType.Inventory1; inventoryTypeValue <= (int)InventoryType.Inventory4; inventoryTypeValue++)
        {
            var inventoryType = (InventoryType)inventoryTypeValue;
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
            {
                var inventorySlot = container->GetInventorySlot(slotIndex);
                if (inventorySlot != null && inventorySlot->GetItemId() == 0)
                {
                    availableSlots++;
                }
            }
        }

        return availableSlots;
    }

    private int CountInventoryItem(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return 0;
        }

        var itemCount = 0;
        for (var inventoryTypeValue = (int)InventoryType.Inventory1; inventoryTypeValue <= (int)InventoryType.Inventory4; inventoryTypeValue++)
        {
            var inventoryType = (InventoryType)inventoryTypeValue;
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
            {
                var inventorySlot = container->GetInventorySlot(slotIndex);
                if (inventorySlot != null && inventorySlot->GetItemId() == itemId)
                {
                    itemCount += (int)inventorySlot->GetQuantity();
                }
            }
        }

        return itemCount;
    }

    private bool TryFindInventoryItem(uint itemId, out InventorySlot slot)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            slot = default;
            return false;
        }

        for (var inventoryTypeValue = (int)InventoryType.Inventory1; inventoryTypeValue <= (int)InventoryType.Inventory4; inventoryTypeValue++)
        {
            var inventoryType = (InventoryType)inventoryTypeValue;
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
            {
                var inventorySlot = container->GetInventorySlot(slotIndex);
                if (inventorySlot == null || inventorySlot->GetItemId() != itemId)
                {
                    continue;
                }

                slot = new InventorySlot(itemId, inventorySlot->GetInventoryType(), inventorySlot->GetSlot());
                return true;
            }
        }

        slot = default;
        return false;
    }

    private bool IsInventorySlotConsumed(InventorySlot slot)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return false;
        }

        var container = inventoryManager->GetInventoryContainer(slot.InventoryType);
        if (container == null || slot.Slot >= container->GetSize())
        {
            return false;
        }

        var inventorySlot = container->GetInventorySlot((int)slot.Slot);
        return inventorySlot != null && inventorySlot->GetItemId() != slot.ItemId;
    }

    private bool IsDresserOpen()
    {
        var addon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    private bool IsPlateOpen()
    {
        var addon = this.Services.GameGui.GetAddonByName(PlateAddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    private bool IsSetConvertOpen()
    {
        var addon = this.Services.GameGui.GetAddonByName(SetConvertAddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
    }

    private bool TryClickButton(AtkUnitBase* addon, uint buttonId, System.Action? beforeClick = null)
    {
        if (addon == null)
        {
            return false;
        }

        var button = addon->GetComponentButtonById(buttonId);
        if (button == null || !button->IsEnabled || button->AtkResNode == null || !button->AtkResNode->IsVisible())
        {
            return false;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null)
        {
            return false;
        }

        var eventPointer = this.FindEvent(ownerNode->AtkResNode.AtkEventManager.Event, AtkEventType.MouseClick);
        if (eventPointer == null)
        {
            return false;
        }

        beforeClick?.Invoke();
        addon->ReceiveEvent(eventPointer->State.EventType, (int)eventPointer->Param, eventPointer);
        return true;
    }

    private bool TryClickCheckBox(AtkUnitBase* addon, uint checkBoxId)
    {
        if (addon == null)
        {
            return false;
        }

        var componentNode = addon->GetComponentNodeById(checkBoxId);
        if (componentNode == null || componentNode->Component == null)
        {
            return false;
        }

        var checkBox = componentNode->GetAsAtkComponentCheckBox();
        if (checkBox == null
            || checkBox->AtkComponentButton.IsChecked
            || !checkBox->AtkComponentButton.IsEnabled
            || checkBox->OwnerNode == null
            || !checkBox->OwnerNode->AtkResNode.IsVisible())
        {
            return false;
        }

        var eventPointer = this.FindEvent(checkBox->OwnerNode->AtkResNode.AtkEventManager.Event, AtkEventType.ButtonClick);
        this.SendComponentEvent(
            componentNode->Component,
            componentNode,
            AtkEventType.ButtonClick,
            eventPointer != null && eventPointer->State.EventType == AtkEventType.ButtonClick ? eventPointer->Param : 0);

        return true;
    }

    private bool TryNotifyCheckedCheckBox(AtkUnitBase* addon, uint checkBoxId)
    {
        if (addon == null)
        {
            return false;
        }

        var componentNode = addon->GetComponentNodeById(checkBoxId);
        if (componentNode == null)
        {
            return false;
        }

        var checkBox = componentNode->GetAsAtkComponentCheckBox();
        if (checkBox == null
            || !checkBox->AtkComponentButton.IsChecked
            || !checkBox->AtkComponentButton.IsEnabled
            || checkBox->OwnerNode == null
            || !checkBox->OwnerNode->AtkResNode.IsVisible())
        {
            return false;
        }

        var eventPointer = this.FindEvent(checkBox->OwnerNode->AtkResNode.AtkEventManager.Event, AtkEventType.ButtonClick);
        var syntheticEvent = stackalloc AtkEvent[1];
        syntheticEvent->Target = (AtkEventTarget*)componentNode;
        syntheticEvent->Listener = (AtkEventListener*)addon;
        syntheticEvent->Param = eventPointer != null ? eventPointer->Param : 0;
        syntheticEvent->State.EventType = AtkEventType.ButtonClick;

        var eventData = stackalloc AtkEventData[1];
        addon->ReceiveEvent(AtkEventType.ButtonClick, (int)syntheticEvent->Param, syntheticEvent, eventData);
        return true;
    }

    private bool TryForceEnableConfirmYes(AtkUnitBase* addon, uint checkBoxId, uint yesButtonId)
    {
        if (addon == null)
        {
            return false;
        }

        var componentNode = addon->GetComponentNodeById(checkBoxId);
        if (componentNode == null)
        {
            return false;
        }

        var checkBox = componentNode->GetAsAtkComponentCheckBox();
        var yesButton = addon->GetComponentButtonById(yesButtonId);
        if (checkBox == null
            || yesButton == null
            || !checkBox->AtkComponentButton.IsEnabled
            || checkBox->OwnerNode == null
            || !checkBox->OwnerNode->AtkResNode.IsVisible()
            || yesButton->AtkResNode == null
            || !yesButton->AtkResNode->IsVisible())
        {
            return false;
        }

        checkBox->AtkComponentButton.SetChecked(true);
        yesButton->AtkComponentBase.SetEnabledState(true);
        return true;
    }

    private void SendComponentEvent(AtkComponentBase* component, AtkComponentNode* target, AtkEventType eventType, uint eventParam)
    {
        var syntheticEvent = stackalloc AtkEvent[1];
        syntheticEvent->Target = (AtkEventTarget*)target;
        syntheticEvent->Listener = (AtkEventListener*)component;
        syntheticEvent->Param = eventParam;
        syntheticEvent->State.EventType = eventType;

        var eventData = stackalloc AtkEventData[1];
        component->ReceiveEvent(eventType, (int)eventParam, syntheticEvent, eventData);
    }

    private AtkEvent* FindEvent(AtkEvent* firstEvent, AtkEventType eventType)
    {
        var current = firstEvent;
        for (var i = 0; i < 16 && current != null; i++)
        {
            if (current->State.EventType == eventType)
            {
                return current;
            }

            current = current->NextEvent;
        }

        return firstEvent;
    }

    private void MarkCandidatesDirty()
    {
        this.candidatesDirty = true;
        this.nextCandidateRefreshAt = DateTimeOffset.MinValue;
    }

    private string GetStepDescription(QueueStep queueStep)
    {
        return queueStep switch
        {
            QueueStep.WaitingForDyedConfirmation => "waiting for dyed outfit confirmation",
            QueueStep.RestoringItems => "restoring outfit pieces",
            QueueStep.WaitingForRestore => "waiting for inventory updates",
            QueueStep.WaitingForSetRestore => "waiting for stored outfit pieces",
            QueueStep.OpeningSetConvert => "opening Outfit Glamour Creation",
            QueueStep.FillingSetConvert => "filling Outfit Glamour Creation",
            QueueStep.ValidatingSetConvert => "validating selected outfit pieces",
            QueueStep.StoringOutfit => "pressing the native Store action",
            QueueStep.ConfirmingStore => "confirming the native Store action",
            QueueStep.WaitingForStore => "waiting for the outfit to be stored",
            _ => "processing the queue"
        };
    }

    private enum QueueStep
    {
        Idle,
        WaitingForDyedConfirmation,
        RestoringItems,
        WaitingForRestore,
        WaitingForSetRestore,
        OpeningSetConvert,
        FillingSetConvert,
        ValidatingSetConvert,
        StoringOutfit,
        ConfirmingStore,
        WaitingForStore,
        Complete,
        Error
    }

    private sealed class OutfitCandidate
    {
        public OutfitCandidate(
            uint setItemId,
            string name,
            uint[] setItemIds,
            List<CandidateItem> selectionItems,
            List<CandidateItem> restoreItems,
            int[] storedSlotIndexes,
            uint[] storedSetItemIds,
            bool requiresConfirmation)
        {
            this.SetItemId = setItemId;
            this.Name = name;
            this.SetItemIds = setItemIds;
            this.SelectionItems = selectionItems;
            this.RestoreItems = restoreItems;
            this.StoredSlotIndexes = storedSlotIndexes;
            this.StoredSetItemIds = storedSetItemIds;
            this.RequiresConfirmation = requiresConfirmation;
        }

        public uint SetItemId { get; }
        public string Name { get; }
        public uint[] SetItemIds { get; }
        public List<CandidateItem> SelectionItems { get; }
        public List<CandidateItem> RestoreItems { get; }
        public int[] StoredSlotIndexes { get; }
        public uint[] StoredSetItemIds { get; }
        public bool RequiresConfirmation { get; }
        public int GlamourPrismCost => this.SelectionItems.Count;
        public bool IsMerge => this.StoredSlotIndexes.Length > 0;
    }

    private sealed class QueuedOutfit
    {
        public QueuedOutfit(OutfitCandidate candidate)
        {
            this.SetItemId = candidate.SetItemId;
            this.Name = candidate.Name;
            this.SetItemIds = candidate.SetItemIds;
            this.SelectionItems = candidate.SelectionItems;
            this.RestoreItems = candidate.RestoreItems;
            this.StoredSlotIndexes = candidate.StoredSlotIndexes;
            this.StoredSetItemIds = candidate.StoredSetItemIds;
            this.RequiresConfirmation = candidate.RequiresConfirmation;
            this.GlamourPrismCost = candidate.GlamourPrismCost;
        }

        public uint SetItemId { get; }
        public string Name { get; }
        public uint[] SetItemIds { get; }
        public List<CandidateItem> SelectionItems { get; }
        public List<CandidateItem> RestoreItems { get; }
        public int[] StoredSlotIndexes { get; }
        public uint[] StoredSetItemIds { get; }
        public bool RequiresConfirmation { get; }
        public int GlamourPrismCost { get; }
        public bool IsMerge => this.StoredSlotIndexes.Length > 0;
        public List<InventorySlot> RestoredSlots { get; } = [];
        public int NextRestoreIndex { get; set; }
        public bool StoredSetRestored { get; set; }
    }

    private sealed class CandidateItem
    {
        public CandidateItem(uint itemId, bool dyed)
        {
            this.ItemId = itemId;
            this.Dyed = dyed;
        }

        public uint ItemId { get; }
        public bool Dyed { get; }
    }

    private readonly struct SetItemSlot
    {
        public SetItemSlot(int slotIndex, uint itemId)
        {
            this.SlotIndex = slotIndex;
            this.ItemId = itemId;
        }

        public int SlotIndex { get; }
        public uint ItemId { get; }
    }

    private readonly struct InventorySlot
    {
        public InventorySlot(uint itemId, InventoryType inventoryType, uint slot)
        {
            this.ItemId = itemId;
            this.InventoryType = inventoryType;
            this.Slot = slot;
        }

        public uint ItemId { get; }
        public InventoryType InventoryType { get; }
        public uint Slot { get; }
    }

    private readonly struct SetConvertUiItem
    {
        public SetConvertUiItem(int index, uint itemId, uint inventoryType, uint slot, uint flag)
        {
            this.Index = index;
            this.ItemId = itemId;
            this.InventoryType = inventoryType;
            this.Slot = slot;
            this.Flag = flag;
        }

        public int Index { get; }
        public uint ItemId { get; }
        public uint InventoryType { get; }
        public uint Slot { get; }
        public uint Flag { get; }
    }
}
