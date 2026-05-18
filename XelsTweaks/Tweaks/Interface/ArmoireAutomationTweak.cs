using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class ArmoireAutomationTweak : TweakBase
{
    public const string TweakId = "interface.armoireAutomation";

    private const string HideWhenNoWorkKey = "hideWhenNoWork";
    private const string CabinetAddonName = "Cabinet";
    private const string DresserAddonName = "MiragePrismPrismBox";
    private const string PlateAddonName = "MiragePrismMiragePlate";
    private const int CabinetStoreItemCountOffset = 0;
    private const int CabinetStoreItemStartOffset = 12;
    private const int CabinetStoreItemStride = 7;

    private static readonly TimeSpan CandidateRefreshDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(18);
    private static readonly Vector4 WarningColor = new(1f, 0.74f, 0.25f, 1f);
    private static readonly InventoryType[] InventoryBagTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    private readonly HashSet<uint> skippedCabinetIds = [];
    private readonly HashSet<DresserSkipKey> skippedDresserTasks = [];
    private Dictionary<uint, uint>? cabinetIdsByItemId;
    private QueueMode mode = QueueMode.Idle;
    private CabinetStoreCandidate? pendingCabinetStore;
    private PendingDresserRestore? pendingDresserRestore;
    private DateTimeOffset nextActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset stepStartedAt = DateTimeOffset.MinValue;
    private string status = "Open the Armoire or Glamour Dresser to move Armoire-eligible items.";
    private int completed;
    private int skipped;
    private int totalQueued;

    public ArmoireAutomationTweak(DalamudServices services, TweakState state, System.Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Armoire Automation";
    public override string Description => "Adds Armoire and Glamour Dresser buttons for moving Armoire-eligible items.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;

    private bool IsQueueActive => this.mode is QueueMode.StoringCabinet or QueueMode.WaitingForCabinetStore or QueueMode.RestoringDresser or QueueMode.WaitingForDresserRestore;
    private bool HideWhenNoWork => this.GetBool(HideWhenNoWorkKey, true);

    public override bool DrawConfig()
    {
        var changed = false;

        var hideWhenNoWork = this.HideWhenNoWork;
        if (ImGui.Checkbox("Hide quick-action windows when there is nothing to do", ref hideWhenNoWork))
        {
            this.SetBool(HideWhenNoWorkKey, hideWhenNoWork);
            changed = true;
        }

        ImGui.TextColored(WarningColor, "Important:");
        DrawImportantBullet("Restoring from the Glamour Dresser needs free inventory space and stops when your inventory is full.");
        DrawImportantBullet("Skipped or failed items are left where they are.");
        return changed;
    }

    private static void DrawImportantBullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    protected override void OnEnable()
    {
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.PluginInterface.UiBuilder.Draw += this.DrawOverlay;
    }

    protected override void OnDisable()
    {
        this.Services.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.ResetQueue("Disabled.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.IsQueueActive)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < this.nextActionAt)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - this.stepStartedAt > StepTimeout)
        {
            if (this.mode == QueueMode.WaitingForCabinetStore)
            {
                this.SkipPendingCabinetStore("Timed out waiting for the Armoire to finish storing the current item.");
                return;
            }

            if (this.mode == QueueMode.WaitingForDresserRestore)
            {
                this.SkipPendingDresserRestore("Timed out waiting for the Glamour Dresser to restore the current item.");
                return;
            }

            this.FailQueue($"Timed out while {this.GetCurrentActionDescription()}.");
            return;
        }

        switch (this.mode)
        {
            case QueueMode.StoringCabinet:
                this.AdvanceCabinetStore();
                break;
            case QueueMode.WaitingForCabinetStore:
                this.WaitForCabinetStore();
                break;
            case QueueMode.RestoringDresser:
                this.AdvanceDresserRestore();
                break;
            case QueueMode.WaitingForDresserRestore:
                this.WaitForDresserRestore();
                break;
        }
    }

    private void DrawOverlay()
    {
        if (this.IsCabinetOpen())
        {
            this.DrawCabinetOverlay();
            return;
        }

        if (this.IsDresserOpen() && !this.IsPlateOpen())
        {
            this.DrawDresserOverlay();
        }
    }

    private void DrawCabinetOverlay()
    {
        var cabinetAddon = this.Services.GameGui.GetAddonByName(CabinetAddonName, 1);
        if (cabinetAddon.IsNull)
        {
            return;
        }

        if (this.HideWhenNoWork && !this.IsQueueActive && !this.TryGetNextCabinetStoreCandidate(out _))
        {
            return;
        }

        var style = ImGui.GetStyle();
        var height = ImGui.GetTextLineHeight() + (style.FramePadding.Y * 2f) + (style.WindowPadding.Y * 2f);
        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport().Pos + cabinetAddon.Position + new Vector2(4f, 3f - height),
            ImGuiCond.Always);

        if (!ImGui.Begin(
            "###XelsTweaksArmoireAutomationCabinet",
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        this.DrawCabinetOverlayContents();
        ImGui.End();
    }

    private void DrawDresserOverlay()
    {
        var dresserAddon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
        if (dresserAddon.IsNull)
        {
            return;
        }

        if (this.HideWhenNoWork && !this.IsQueueActive && !this.TryFindNextDresserRestoreTask(out _))
        {
            return;
        }

        var style = ImGui.GetStyle();
        var height = ImGui.GetTextLineHeight() + (style.FramePadding.Y * 2f) + (style.WindowPadding.Y * 2f);
        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport().Pos + dresserAddon.Position + new Vector2(4f, 3f - height),
            ImGuiCond.Always);

        if (!ImGui.Begin(
            "###XelsTweaksArmoireAutomationDresser",
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        this.DrawDresserOverlayContents();
        ImGui.End();
    }

    private void DrawCabinetOverlayContents()
    {
        var isBusy = this.IsQueueActive;
        var hasCandidate = this.TryGetNextCabinetStoreCandidate(out var candidate);
        if (!isBusy && !hasCandidate)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No storable items are listed.");
            return;
        }

        if (isBusy || !hasCandidate)
        {
            ImGui.BeginDisabled();
        }

        var buttonLabel = isBusy
            ? $"Store All ({this.completed}/{this.totalQueued})###XelsTweaksArmoireStoreAll"
            : "Store All###XelsTweaksArmoireStoreAll";
        if (ImGui.Button(buttonLabel))
        {
            this.StartCabinetStore();
        }

        if (isBusy || !hasCandidate)
        {
            ImGui.EndDisabled();
        }

        if (isBusy)
        {
            if (this.pendingCabinetStore is { } pending)
            {
                ImGui.SameLine();
                this.DrawItemInline(pending.ItemId, pending.Name);
            }
            else if (hasCandidate)
            {
                ImGui.SameLine();
                this.DrawItemInline(candidate.ItemId, candidate.Name);
            }
        }
        else if (!hasCandidate)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No storable items are listed.");
        }
        else
        {
            ImGui.SameLine();
            this.DrawItemInline(candidate.ItemId, candidate.Name);
        }
    }

    private void DrawDresserOverlayContents()
    {
        var isBusy = this.IsQueueActive;
        var hasCandidate = this.TryFindNextDresserRestoreTask(out var candidate);
        if (!isBusy && !hasCandidate)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No Armoire-eligible dresser items found.");
            return;
        }

        if (isBusy || !hasCandidate)
        {
            ImGui.BeginDisabled();
        }

        var buttonLabel = isBusy
            ? $"Restore Armoire Items ({this.completed}/{this.totalQueued})###XelsTweaksDresserRestoreArmoireItems"
            : "Restore Armoire Items###XelsTweaksDresserRestoreArmoireItems";
        if (ImGui.Button(buttonLabel))
        {
            this.StartDresserRestore();
        }

        if (isBusy || !hasCandidate)
        {
            ImGui.EndDisabled();
        }

        if (isBusy)
        {
            if (this.pendingDresserRestore is { } pending)
            {
                ImGui.SameLine();
                this.DrawItemInline(pending.Task.ItemId, pending.Task.Name);
            }
            else if (hasCandidate)
            {
                ImGui.SameLine();
                this.DrawItemInline(candidate.ItemId, candidate.Name);
            }
        }
        else if (!hasCandidate)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No Armoire-eligible dresser items found.");
        }
        else
        {
            ImGui.SameLine();
            this.DrawItemInline(candidate.ItemId, candidate.Name);
        }
    }

    private void StartCabinetStore()
    {
        this.skippedCabinetIds.Clear();
        this.pendingCabinetStore = null;
        this.pendingDresserRestore = null;
        this.completed = 0;
        this.skipped = 0;
        this.totalQueued = this.CountCabinetStoreCandidates();
        this.status = "Storing listed Armoire items.";
        this.EnterStep(QueueMode.StoringCabinet);
    }

    private void AdvanceCabinetStore()
    {
        if (!this.IsCabinetOpen())
        {
            this.FailQueue("Armoire closed; stopped storing items.");
            return;
        }

        if (this.IsCabinetConfirmationPending())
        {
            this.status = "Waiting for the Armoire confirmation to close.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (!this.TryGetNextCabinetStoreCandidate(out var candidate))
        {
            this.CompleteQueue(this.skipped == 0
                ? $"Stored {this.completed} listed item{Plural(this.completed)}."
                : $"Stored {this.completed} listed item{Plural(this.completed)}; skipped {this.skipped}.");
            return;
        }

        var uiState = UIState.Instance();
        if (uiState == null)
        {
            this.FailQueue("Could not access Armoire state.");
            return;
        }

        this.status = $"Storing {candidate.Name}.";
        if (!uiState->Cabinet.StoreCabinetItem(candidate.CabinetId))
        {
            this.skippedCabinetIds.Add(candidate.CabinetId);
            this.skipped++;
            this.status = $"Skipped {candidate.Name}; the Armoire did not accept it.";
            this.nextActionAt = DateTimeOffset.UtcNow + ActionDelay;
            this.stepStartedAt = DateTimeOffset.UtcNow;
            return;
        }

        this.pendingCabinetStore = candidate;
        this.EnterStep(QueueMode.WaitingForCabinetStore);
    }

    private void WaitForCabinetStore()
    {
        if (!this.IsCabinetOpen())
        {
            this.FailQueue("Armoire closed; stopped storing items.");
            return;
        }

        if (this.pendingCabinetStore == null)
        {
            this.EnterStep(QueueMode.StoringCabinet);
            return;
        }

        var pending = this.pendingCabinetStore.Value;
        var uiState = UIState.Instance();
        if (uiState != null && uiState->Cabinet.IsItemInCabinet(pending.CabinetId))
        {
            this.completed++;
            this.pendingCabinetStore = null;
            this.status = $"Stored {pending.Name}.";
            this.EnterStep(QueueMode.StoringCabinet, ActionDelay);
            return;
        }

        if (!this.IsCabinetStoreCandidateStillListed(pending.CabinetId))
        {
            this.completed++;
            this.pendingCabinetStore = null;
            this.status = $"Stored {pending.Name}.";
            this.EnterStep(QueueMode.StoringCabinet, ActionDelay);
            return;
        }

        this.status = $"Waiting for the Armoire to store {pending.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
    }

    private void StartDresserRestore()
    {
        this.skippedDresserTasks.Clear();
        this.pendingCabinetStore = null;
        this.pendingDresserRestore = null;
        this.completed = 0;
        this.skipped = 0;
        this.totalQueued = Math.Min(this.CountDresserRestoreCandidates(), this.CountAvailableInventorySlots());
        this.status = "Restoring Armoire-eligible items from the Glamour Dresser.";
        this.EnterStep(QueueMode.RestoringDresser);
    }

    private void AdvanceDresserRestore()
    {
        if (!this.IsDresserOpen() || this.IsPlateOpen())
        {
            this.FailQueue("Glamour Dresser closed; stopped restoring items.");
            return;
        }

        if (this.CountAvailableInventorySlots() <= 0)
        {
            this.CompleteQueue(this.skipped == 0
                ? $"Inventory is full. Restored {this.completed} item{Plural(this.completed)}."
                : $"Inventory is full. Restored {this.completed} item{Plural(this.completed)}; skipped {this.skipped}.");
            return;
        }

        if (!this.TryFindNextDresserRestoreTask(out var task))
        {
            this.CompleteQueue(this.skipped == 0
                ? $"Restored {this.completed} Armoire-eligible item{Plural(this.completed)}."
                : $"Restored {this.completed} Armoire-eligible item{Plural(this.completed)}; skipped {this.skipped}.");
            return;
        }

        var inventoryCountBeforeRestore = this.CountInventoryBagItems(task.ItemId);
        var sent = task.Kind == DresserTaskKind.SetPiece
            ? this.TryRestoreDresserSetPiece(task)
            : this.TryRestoreDresserItem(task);
        if (!sent)
        {
            this.skippedDresserTasks.Add(task.SkipKey);
            this.skipped++;
            this.status = $"Skipped {task.Name}; the Glamour Dresser did not restore it.";
            this.nextActionAt = DateTimeOffset.UtcNow + ActionDelay;
            this.stepStartedAt = DateTimeOffset.UtcNow;
            return;
        }

        this.pendingDresserRestore = new PendingDresserRestore(task, inventoryCountBeforeRestore);
        this.status = $"Restoring {task.Name}.";
        this.EnterStep(QueueMode.WaitingForDresserRestore);
    }

    private void WaitForDresserRestore()
    {
        if (!this.IsDresserOpen() || this.IsPlateOpen())
        {
            this.FailQueue("Glamour Dresser closed; stopped restoring items.");
            return;
        }

        if (this.pendingDresserRestore == null)
        {
            this.EnterStep(QueueMode.RestoringDresser);
            return;
        }

        var pending = this.pendingDresserRestore.Value;
        if (this.CountInventoryBagItems(pending.Task.ItemId) > pending.InventoryCountBefore
            || !this.IsDresserRestoreTaskStillPresent(pending.Task))
        {
            this.completed++;
            this.pendingDresserRestore = null;
            this.status = $"Restored {pending.Task.Name}.";
            this.EnterStep(QueueMode.RestoringDresser, ActionDelay);
            return;
        }

        this.status = $"Waiting for the Glamour Dresser to restore {pending.Task.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
    }

    private void SkipPendingCabinetStore(string reason)
    {
        if (this.pendingCabinetStore is { } pending)
        {
            this.skippedCabinetIds.Add(pending.CabinetId);
            this.skipped++;
        }

        this.pendingCabinetStore = null;
        this.status = reason;
        this.EnterStep(QueueMode.StoringCabinet, ActionDelay);
    }

    private void SkipPendingDresserRestore(string reason)
    {
        if (this.pendingDresserRestore is { } pending)
        {
            this.skippedDresserTasks.Add(pending.Task.SkipKey);
            this.skipped++;
        }

        this.pendingDresserRestore = null;
        this.status = reason;
        this.EnterStep(QueueMode.RestoringDresser, ActionDelay);
    }

    private int CountCabinetStoreCandidates()
    {
        var count = 0;
        var skipped = new HashSet<uint>(this.skippedCabinetIds);
        while (this.TryGetNextCabinetStoreCandidate(skipped, out var candidate))
        {
            skipped.Add(candidate.CabinetId);
            count++;
        }

        return count;
    }

    private bool TryGetNextCabinetStoreCandidate(out CabinetStoreCandidate candidate)
    {
        return this.TryGetNextCabinetStoreCandidate(this.skippedCabinetIds, out candidate);
    }

    private bool TryGetNextCabinetStoreCandidate(IReadOnlySet<uint> skippedIds, out CabinetStoreCandidate candidate)
    {
        candidate = default;

        var stage = AtkStage.Instance();
        if (stage == null)
        {
            return false;
        }

        var numberArray = stage->GetNumberArrayData(NumberArrayType.CabinetStore);
        if (numberArray == null)
        {
            return false;
        }

        var itemCount = numberArray->IntArray[CabinetStoreItemCountOffset];
        if (itemCount <= 0)
        {
            return false;
        }

        var agent = AgentCabinet.Instance();
        if (agent == null || agent->Items == null)
        {
            return false;
        }

        var uiState = UIState.Instance();
        if (uiState == null || !uiState->Cabinet.IsCabinetLoaded())
        {
            return false;
        }

        var cabinetSheet = this.Services.DataManager.GetExcelSheet<CabinetSheet>();
        for (var i = 0; i < itemCount; i++)
        {
            var cabinetItemIndex = numberArray->IntArray[CabinetStoreItemStartOffset + (i * CabinetStoreItemStride)];
            if (cabinetItemIndex < 0)
            {
                continue;
            }

            var cabinetId = agent->Items[cabinetItemIndex].Id;
            if (cabinetId == 0)
            {
                break;
            }

            if (skippedIds.Contains(cabinetId)
                || !cabinetSheet.TryGetRow(cabinetId, out var row)
                || row.Item.RowId == 0
                || !row.Item.IsValid
                || uiState->Cabinet.IsItemInCabinet(cabinetId))
            {
                continue;
            }

            candidate = new CabinetStoreCandidate(cabinetId, row.Item.RowId, this.GetItemName(row.Item.RowId));
            return true;
        }

        return false;
    }

    private bool IsCabinetStoreCandidateStillListed(uint cabinetId)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            return false;
        }

        var numberArray = stage->GetNumberArrayData(NumberArrayType.CabinetStore);
        if (numberArray == null)
        {
            return false;
        }

        var itemCount = numberArray->IntArray[CabinetStoreItemCountOffset];
        if (itemCount <= 0)
        {
            return false;
        }

        var agent = AgentCabinet.Instance();
        if (agent == null || agent->Items == null)
        {
            return false;
        }

        for (var i = 0; i < itemCount; i++)
        {
            var cabinetItemIndex = numberArray->IntArray[CabinetStoreItemStartOffset + (i * CabinetStoreItemStride)];
            if (cabinetItemIndex < 0)
            {
                continue;
            }

            if (agent->Items[cabinetItemIndex].Id == cabinetId)
            {
                return true;
            }
        }

        return false;
    }

    private int CountDresserRestoreCandidates()
    {
        var count = 0;
        var skipped = new HashSet<DresserSkipKey>(this.skippedDresserTasks);
        while (this.TryFindNextDresserRestoreTask(skipped, out var task))
        {
            skipped.Add(task.SkipKey);
            count++;
        }

        return count;
    }

    private bool TryFindNextDresserRestoreTask(out DresserRestoreTask task)
    {
        return this.TryFindNextDresserRestoreTask(this.skippedDresserTasks, out task);
    }

    private bool TryFindNextDresserRestoreTask(IReadOnlySet<DresserSkipKey> skippedTasks, out DresserRestoreTask task)
    {
        task = default;

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            return false;
        }

        var setSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItem>();
        var cabinetIds = this.GetCabinetIdsByItemId();
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var rawItemId = manager->PrismBoxItemIds[i];
            var itemId = GetBaseItemId(rawItemId);
            if (itemId == 0 || IsHighQualityItem(rawItemId))
            {
                continue;
            }

            if (setSheet.TryGetRow(itemId, out var setRow))
            {
                foreach (var slot in this.GetSetItems(setRow))
                {
                    if (!this.IsDresserSetSlotStored(manager, i, slot.SlotIndex)
                        || !cabinetIds.ContainsKey(slot.ItemId))
                    {
                        continue;
                    }

                    var key = new DresserSkipKey(DresserTaskKind.SetPiece, itemId, slot.SlotIndex, slot.ItemId);
                    if (skippedTasks.Contains(key))
                    {
                        continue;
                    }

                    task = new DresserRestoreTask(
                        DresserTaskKind.SetPiece,
                        i,
                        itemId,
                        slot.SlotIndex,
                        slot.ItemId,
                        this.GetItemName(slot.ItemId),
                        key);
                    return true;
                }

                continue;
            }

            if (!cabinetIds.ContainsKey(itemId))
            {
                continue;
            }

            var itemKey = new DresserSkipKey(DresserTaskKind.Item, itemId, -1, itemId);
            if (skippedTasks.Contains(itemKey))
            {
                continue;
            }

            task = new DresserRestoreTask(
                DresserTaskKind.Item,
                i,
                itemId,
                -1,
                itemId,
                this.GetItemName(itemId),
                itemKey);
            return true;
        }

        return false;
    }

    private bool TryRestoreDresserItem(DresserRestoreTask task)
    {
        var manager = MirageManager.Instance();
        if (manager == null
            || task.Index < 0
            || task.Index >= manager->PrismBoxItemIds.Length
            || GetBaseItemId(manager->PrismBoxItemIds[task.Index]) != task.ContainerItemId)
        {
            return false;
        }

        return manager->RestorePrismBoxItem((uint)task.Index);
    }

    private bool TryRestoreDresserSetPiece(DresserRestoreTask task)
    {
        var manager = MirageManager.Instance();
        if (manager == null
            || task.Index < 0
            || task.Index >= manager->PrismBoxItemIds.Length
            || task.SlotIndex < 0
            || GetBaseItemId(manager->PrismBoxItemIds[task.Index]) != task.ContainerItemId
            || !this.IsDresserSetSlotStored(manager, task.Index, task.SlotIndex))
        {
            return false;
        }

        var restoreBits = stackalloc byte[2];
        restoreBits[0] = 0;
        restoreBits[1] = 0;
        if (task.SlotIndex < 8)
        {
            restoreBits[0] = (byte)(1 << task.SlotIndex);
        }
        else
        {
            restoreBits[1] = (byte)(1 << (task.SlotIndex - 8));
        }

        return manager->RestorePrismBoxSetItem((uint)task.Index, restoreBits);
    }

    private bool IsDresserRestoreTaskStillPresent(DresserRestoreTask task)
    {
        var skipped = new HashSet<DresserSkipKey>(this.skippedDresserTasks);
        return this.TryFindNextDresserRestoreTask(skipped, out var nextTask)
            && nextTask.SkipKey.Equals(task.SkipKey);
    }

    private bool IsDresserSetSlotStored(MirageManager* manager, int itemIndex, int slotIndex)
    {
        if (slotIndex < 0 || itemIndex < 0 || itemIndex >= manager->PrismBoxItemIds.Length)
        {
            return false;
        }

        var stainBits = slotIndex < 8
            ? manager->PrismBoxStain0Ids[itemIndex]
            : manager->PrismBoxStain1Ids[itemIndex];
        var bit = 1 << (slotIndex % 8);

        return (stainBits & bit) == 0;
    }

    private Dictionary<uint, uint> GetCabinetIdsByItemId()
    {
        if (this.cabinetIdsByItemId != null)
        {
            return this.cabinetIdsByItemId;
        }

        var cabinetIds = new Dictionary<uint, uint>();
        foreach (var row in this.Services.DataManager.GetExcelSheet<CabinetSheet>())
        {
            if (row.Item.RowId == 0 || !row.Item.IsValid)
            {
                continue;
            }

            cabinetIds.TryAdd(row.Item.RowId, row.RowId);
        }

        this.cabinetIdsByItemId = cabinetIds;
        return cabinetIds;
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

    private int CountAvailableInventorySlots()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return 0;
        }

        var availableSlots = 0;
        foreach (var inventoryType in InventoryBagTypes)
        {
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

    private int CountInventoryBagItems(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return 0;
        }

        var itemCount = 0;
        foreach (var inventoryType in InventoryBagTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
            {
                var inventorySlot = container->GetInventorySlot(slotIndex);
                if (inventorySlot != null && GetBaseItemId(inventorySlot->GetItemId()) == itemId)
                {
                    itemCount++;
                }
            }
        }

        return itemCount;
    }

    private string GetItemName(uint itemId)
    {
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var row))
        {
            return $"Item {itemId}";
        }

        var name = row.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
    }

    private void DrawItemInline(uint itemId, string name)
    {
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        if (itemSheet.TryGetRow(itemId, out var row) && row.Icon != 0)
        {
            var texture = this.Services.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)row.Icon, false, false, null));
            var wrap = texture.GetWrapOrDefault();
            if (wrap?.Handle != null)
            {
                var iconSize = new Vector2(ImGui.GetFrameHeight());
                ImGui.Image(wrap.Handle, iconSize);
                ImGui.SameLine();
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(name);
    }

    private void EnterStep(QueueMode nextMode, TimeSpan? delay = null)
    {
        this.mode = nextMode;
        this.nextActionAt = DateTimeOffset.UtcNow + (delay ?? TimeSpan.Zero);
        this.stepStartedAt = DateTimeOffset.UtcNow;
    }

    private void CompleteQueue(string message)
    {
        this.mode = QueueMode.Complete;
        this.pendingCabinetStore = null;
        this.pendingDresserRestore = null;
        this.status = message;
    }

    private void FailQueue(string message)
    {
        this.mode = QueueMode.Error;
        this.pendingCabinetStore = null;
        this.pendingDresserRestore = null;
        this.status = message;
    }

    private void ResetQueue(string message)
    {
        this.mode = QueueMode.Idle;
        this.pendingCabinetStore = null;
        this.pendingDresserRestore = null;
        this.skippedCabinetIds.Clear();
        this.skippedDresserTasks.Clear();
        this.completed = 0;
        this.skipped = 0;
        this.totalQueued = 0;
        this.status = message;
        this.nextActionAt = DateTimeOffset.MinValue;
        this.stepStartedAt = DateTimeOffset.MinValue;
    }

    private string GetCurrentActionDescription()
    {
        return this.mode switch
        {
            QueueMode.StoringCabinet => "storing Armoire items",
            QueueMode.RestoringDresser => "restoring Glamour Dresser items",
            QueueMode.WaitingForCabinetStore => "waiting for the Armoire",
            QueueMode.WaitingForDresserRestore => "waiting for the Glamour Dresser",
            _ => "moving items"
        };
    }

    private bool IsCabinetOpen()
    {
        var addon = this.Services.GameGui.GetAddonByName(CabinetAddonName, 1);
        return !addon.IsNull && addon.IsReady && addon.IsVisible;
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

    private bool IsCabinetConfirmationPending()
    {
        var agent = AgentCabinet.Instance();
        return agent != null && agent->ConfirmationAddonId != 0;
    }

    private static uint GetBaseItemId(uint itemId)
    {
        return itemId == 0
            ? 0
            : ItemUtil.GetBaseId(itemId).ItemId;
    }

    private static bool IsHighQualityItem(uint itemId)
    {
        return itemId != 0 && ItemUtil.IsHighQuality(itemId);
    }

    private static string Plural(int count)
    {
        return count == 1 ? string.Empty : "s";
    }

    private enum QueueMode
    {
        Idle,
        StoringCabinet,
        WaitingForCabinetStore,
        RestoringDresser,
        WaitingForDresserRestore,
        Complete,
        Error
    }

    private enum DresserTaskKind
    {
        Item,
        SetPiece
    }

    private readonly record struct CabinetStoreCandidate(uint CabinetId, uint ItemId, string Name);

    private readonly record struct DresserSkipKey(DresserTaskKind Kind, uint ContainerItemId, int SlotIndex, uint ItemId);

    private readonly record struct DresserRestoreTask(
        DresserTaskKind Kind,
        int Index,
        uint ContainerItemId,
        int SlotIndex,
        uint ItemId,
        string Name,
        DresserSkipKey SkipKey);

    private readonly record struct PendingDresserRestore(DresserRestoreTask Task, int InventoryCountBefore);

    private readonly record struct SetItemSlot(int SlotIndex, uint ItemId);
}
