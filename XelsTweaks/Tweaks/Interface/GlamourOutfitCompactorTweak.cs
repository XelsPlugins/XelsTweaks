using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class GlamourOutfitCompactorTweak : TweakBase
{
    public const string TweakId = "interface.glamourOutfitCompactor";

    private const string ConfirmDyedOutfitsKey = "confirmDyedOutfits";
    private const string ShowOverlayOnlyWhenEligibleKey = "showOverlayOnlyWhenEligible";
    private const string NewInventoryOutfitPolicyKey = "newInventoryOutfitMode";
    private const string RestoreDuplicateItemsKey = "restoreDuplicateItems";
    private const string DresserAddonName = "MiragePrismPrismBox";
    private const string PlateAddonName = "MiragePrismMiragePlate";
    private const string SetConvertAddonName = "MiragePrismPrismSetConvert";
    private const string SetConvertAlternateAddonName = "MiragePrismPrismBoxCrystallize";
    private const string SetConvertConfirmAddonName = "MiragePrismPrismSetConvertC";
    private const string SelectYesNoAddonName = "SelectYesno";
    private const string SelectYesNoAlternateAddonName = "SelectYesNo";
    private const string ContextMenuAddonName = "ContextMenu";
    private const string ContextIconMenuAddonName = "ContextIconMenu";
    private const string OpenSetConvertSignature = "40 53 41 55 48 81 EC ?? ?? ?? ?? 0F B7 84 24";
    private const string AddendumPromptFirstLine = "An outfit glamour matching this gear is available.";
    private const string AddendumPromptSecondLine = "Add to pre-existing outfit glamour?";
    private const uint SelectYesNoYesButtonId = 8;
    private const uint GlamourPrismItemId = 21800;
    private const uint SetConvertRefreshFlags = 4;
    private const uint StoreAsGlamourButtonId = 27;
    private const uint ConfirmStoreAsOutfitCheckBoxId = 4;
    private const uint ConfirmYesButtonId = 6;
    private const int SetConvertHandOverCallbackId = 13;
    private const uint SetConvertContextMenuActionId = 1021003;
    private const string AddMissingOutfitGearContextMenuText = "Add Missing Outfit Gear";
    private const int SetConvertUiItemCountOffset = 20;
    private const int SetConvertUiItemsOffset = 21;
    private const int SetConvertUiItemStride = 7;
    private const int MaxDiagnosticAtkValues = 24;
    private const int MaxContextMenuEntries = 12;
    private const int MaxRestoreAttempts = 6;
    private const string SetConvertTargetUnavailableError = "Outfit creation did not offer the expected outfit for this piece.";

    private static readonly TimeSpan CandidateRefreshDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(18);
    private static readonly Vector4 WarningColor = new(1f, 0.74f, 0.25f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 SuccessColor = new(0.48f, 0.95f, 0.56f, 1f);
    private static readonly InventoryType[] InventoryBagTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    private readonly List<OutfitCandidate> candidates = [];
    private readonly List<DuplicateItemCandidate> duplicateCandidates = [];
    private readonly List<QueuedOutfit> queue = [];
    private readonly List<QueuedDuplicateItem> duplicateQueue = [];
    private readonly HashSet<SetConvertSourceKey> unsupportedSetConvertSources = [];
    private uint[] lastPrismBoxItemIds = [];
    private QueueStep step = QueueStep.Idle;
    private QueuedOutfit? currentOutfit;
    private QueuedDuplicateItem? currentDuplicateItem;
    private int completedOutfits;
    private int skippedOutfits;
    private int completedDuplicateItems;
    private int skippedDuplicateItems;
    private int totalQueuedOutfits;
    private int totalQueuedDuplicateItems;
    private uint? waitingForRestoredItemId;
    private DuplicateItemKey? waitingForDuplicateItemKey;
    private int waitingForDuplicateInventoryCount;
    private uint? restoreRetryItemId;
    private int restoreRetryAttempts;
    private int? pendingSetConvertSlot;
    private uint? pendingSetConvertItemId;
    private DateTimeOffset nextActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset stepStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextCandidateRefreshAt = DateTimeOffset.MinValue;
    private string status = "Open the Glamour Dresser to find outfits that can be updated.";
    private string? lastError;
    private bool candidatesDirty = true;
    private bool queuePaused;
    private int confirmCheckBoxAttempts;
    private nint openSetConvertAddress;
    private bool useCurrentSetConvertOpenSignature;

    public GlamourOutfitCompactorTweak(DalamudServices services, TweakState state, System.Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Glamour Outfit Cleanup";
    public override string Description => "Adds a Glamour Dresser button that moves loose matching pieces into outfit glamours.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;

    private bool ConfirmDyedOutfits => this.GetBool(ConfirmDyedOutfitsKey, true);
    private bool ShowOverlayOnlyWhenEligible => this.GetBool(ShowOverlayOnlyWhenEligibleKey, true);
    private bool RestoreDuplicateItems => this.GetBool(RestoreDuplicateItemsKey, false);
    private NewInventoryOutfitPolicy CurrentNewInventoryOutfitPolicy => (NewInventoryOutfitPolicy)Math.Clamp(
        this.GetInt(NewInventoryOutfitPolicyKey, (int)NewInventoryOutfitPolicy.Off),
        (int)NewInventoryOutfitPolicy.Off,
        (int)NewInventoryOutfitPolicy.PartialAndFullSets);
    private bool IsQueueActive => this.step is not QueueStep.Idle and not QueueStep.Complete and not QueueStep.Error;

    public override bool DrawConfig()
    {
        var changed = false;

        var confirmDyedOutfits = this.ConfirmDyedOutfits;
        if (ImGui.Checkbox("Ask before adding dyed pieces", ref confirmDyedOutfits))
        {
            this.SetBool(ConfirmDyedOutfitsKey, confirmDyedOutfits);
            changed = true;
        }

        var restoreDuplicateItems = this.RestoreDuplicateItems;
        if (ImGui.Checkbox("Restore duplicate dresser items", ref restoreDuplicateItems))
        {
            this.SetBool(RestoreDuplicateItemsKey, restoreDuplicateItems);
            changed = true;
            this.MarkCandidatesDirty();
        }

        var newInventoryOutfitMode = this.CurrentNewInventoryOutfitPolicy;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("New outfits from inventory", FormatNewInventoryOutfitPolicy(newInventoryOutfitMode)))
        {
            foreach (var mode in Enum.GetValues<NewInventoryOutfitPolicy>())
            {
                var isSelected = mode == newInventoryOutfitMode;
                if (ImGui.Selectable(FormatNewInventoryOutfitPolicy(mode), isSelected))
                {
                    this.SetInt(NewInventoryOutfitPolicyKey, (int)mode);
                    changed = true;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        var showOverlayOnlyWhenEligible = this.ShowOverlayOnlyWhenEligible;
        if (ImGui.Checkbox("Hide the dresser overlay when there are no outfits to update", ref showOverlayOnlyWhenEligible))
        {
            this.SetBool(ShowOverlayOnlyWhenEligibleKey, showOverlayOnlyWhenEligible);
            changed = true;
        }

        ImGui.TextColored(WarningColor, "Important:");
        ImGui.SameLine();
        ImGui.TextWrapped("Cleanup only runs after you press the overlay button. Loose matching pieces may be moved into new or existing outfit glamours. Creating new outfits from inventory can use more Glamour Dresser slots. HQ pieces are ignored because their quality cannot be lowered while the Glamour Dresser is open. Dyed pieces can lose dye when stored, so leave Ask before adding dyed pieces on if you want to review those first. Duplicate dresser items are only matched when both dyes are the same.");

        return changed;
    }

    protected override void OnEnable()
    {
        this.InitializeSetConvertOpenInterop();
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.PluginInterface.UiBuilder.Draw += this.DrawOverlay;
        this.Services.GameInventory.InventoryChangedRaw += this.OnInventoryChanged;
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostOpen, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostClose, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostClose, SetConvertAddonName, this.OnSetConvertClosed);
        this.Services.AddonLifecycle.RegisterListener(AddonEvent.PostClose, SetConvertAlternateAddonName, this.OnSetConvertClosed);
        this.MarkCandidatesDirty();
    }

    protected override void OnDisable()
    {
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostClose, SetConvertAlternateAddonName, this.OnSetConvertClosed);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostClose, SetConvertAddonName, this.OnSetConvertClosed);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostClose, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.AddonLifecycle.UnregisterListener(AddonEvent.PostOpen, DresserAddonName, this.OnDresserAddonChanged);
        this.Services.GameInventory.InventoryChangedRaw -= this.OnInventoryChanged;
        this.Services.PluginInterface.UiBuilder.Draw -= this.DrawOverlay;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.ResetQueue("Disabled.");
        this.candidates.Clear();
        this.duplicateCandidates.Clear();
        this.lastPrismBoxItemIds = [];
        this.openSetConvertAddress = nint.Zero;
        this.useCurrentSetConvertOpenSignature = false;
    }

    private void OnDresserAddonChanged(AddonEvent eventType, AddonArgs args)
    {
        this.MarkCandidatesDirty();

        if (eventType == AddonEvent.PostClose && this.IsQueueActive)
        {
            this.FailQueue("Glamour Dresser closed; stopped updating outfits.");
        }
    }

    private void OnSetConvertClosed(AddonEvent eventType, AddonArgs args)
    {
        if (this.step == QueueStep.WaitingForStore)
        {
            return;
        }

        if (this.currentOutfit != null && this.IsQueueActive && !this.IsSetConvertOpen())
        {
            this.FailQueue("Outfit creation closed before the current outfit was stored.");
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
                this.status = "Open the Glamour Dresser to find outfits that can be updated.";
                this.candidates.Clear();
                this.duplicateCandidates.Clear();
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

        if (this.IsTimedQueueStep() && now - this.stepStartedAt > StepTimeout)
        {
            var detail = string.Empty;
            if ((this.step == QueueStep.WaitingForSetConvertOpen
                    || this.step == QueueStep.FillingSetConvert)
                && this.currentOutfit != null)
            {
                var diagnostic = this.GetSetConvertFillDiagnostic(this.currentOutfit);
                this.Services.Log.Warning(
                    "Glamour Outfit Compactor timed out while {Step}: {Diagnostic}",
                    this.GetStepDescription(this.step),
                    diagnostic);
                detail = this.step switch
                {
                    QueueStep.WaitingForSetConvertOpen => " The outfit creation window did not open for the expected outfit.",
                    _ => " The outfit creation window did not finish selecting all pieces."
                };
            }

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
            && this.duplicateCandidates.Count == 0
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

        if (!ImGui.Begin("Glamour Outfit Cleanup###XelsTweaksGlamourOutfitCompactor", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        this.DrawOverlayContents();
        ImGui.End();
    }

    private void DrawOverlayContents()
    {
        if (this.step == QueueStep.Idle && this.candidates.Count > 0)
        {
            var glamourPrismCost = this.candidates.Sum(candidate => candidate.GlamourPrismCost);
            var glamourPrismsAvailable = this.CountInventoryItem(GlamourPrismItemId);
            var glamourPrismText = $"Glamour Prisms: {glamourPrismCost} / {glamourPrismsAvailable}";
            if (glamourPrismsAvailable < glamourPrismCost)
            {
                ImGui.TextColored(WarningColor, glamourPrismText);
            }
            else
            {
                ImGui.TextUnformatted(glamourPrismText);
            }
        }

        var totalQueuedWork = this.totalQueuedOutfits + this.totalQueuedDuplicateItems;
        if (totalQueuedWork > 0)
        {
            ImGui.TextUnformatted($"Progress: {this.completedOutfits + this.completedDuplicateItems} / {totalQueuedWork}");
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
                if (this.candidates.Count == 0 && this.duplicateCandidates.Count == 0)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Button("Run cleanup"))
                {
                    this.StartQueue();
                }

                if (this.candidates.Count == 0 && this.duplicateCandidates.Count == 0)
                {
                    ImGui.EndDisabled();
                }

                break;

            case QueueStep.WaitingForDyedConfirmation:
                ImGui.TextColored(WarningColor, "This outfit includes dyed pieces.");
                if (ImGui.Button("Continue"))
                {
                    this.EnterStep(QueueStep.RestoringItems, $"Restoring pieces for {this.currentOutfit?.Name ?? "outfit"}.");
                }

                ImGui.SameLine();
                if (ImGui.Button("Skip outfit"))
                {
                    this.SkipCurrentOutfit("Skipped outfit with dyed pieces.");
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
                    this.ResetQueue("Ready.");
                    this.MarkCandidatesDirty();
                }

                break;

            case QueueStep.Complete:
                if (ImGui.Button("Ok"))
                {
                    this.ResetQueue("Ready.");
                    this.MarkCandidatesDirty();
                }

                break;

            default:
                if (ImGui.Button(this.queuePaused ? "Resume" : "Pause"))
                {
                    this.queuePaused = !this.queuePaused;
                    this.status = this.queuePaused
                        ? "Paused."
                        : $"Resuming {this.currentOutfit?.Name ?? this.currentDuplicateItem?.Name ?? "cleanup"}.";
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
            ImGui.TextDisabled($"Working on: {this.currentOutfit.Name}");
            ImGui.TextDisabled($"{this.currentOutfit.SelectionItems.Count} piece{(this.currentOutfit.SelectionItems.Count == 1 ? string.Empty : "s")} to add{(this.currentOutfit.IsMerge ? " to an existing outfit" : string.Empty)}");
        }

        if (this.currentDuplicateItem != null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Restoring duplicate: {this.currentDuplicateItem.Name}");
        }

        var skippedWork = this.skippedOutfits + this.skippedDuplicateItems;
        if (skippedWork > 0)
        {
            ImGui.TextDisabled($"Skipped: {skippedWork}");
        }
    }

    private void StartQueue()
    {
        this.RefreshCandidatesIfNeeded(true);
        this.queue.Clear();
        this.queue.AddRange(this.candidates.Select(candidate => new QueuedOutfit(candidate)));
        this.duplicateQueue.Clear();
        this.duplicateQueue.AddRange(this.CreateDuplicateCleanupQueue());
        this.completedOutfits = 0;
        this.skippedOutfits = 0;
        this.completedDuplicateItems = 0;
        this.skippedDuplicateItems = 0;
        this.totalQueuedOutfits = this.queue.Count;
        this.totalQueuedDuplicateItems = this.duplicateQueue.Count;
        this.currentOutfit = null;
        this.currentDuplicateItem = null;
        this.waitingForRestoredItemId = null;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;
        this.queuePaused = false;
        this.lastError = null;

        if (this.queue.Count == 0 && this.duplicateQueue.Count == 0)
        {
            this.ResetQueue("No Glamour Dresser slots to clean up.");
            return;
        }

        if (this.TryStopQueueWithoutEnoughGlamourPrisms())
        {
            return;
        }

        var glamourPrismCost = this.GetRemainingGlamourPrismCost();
        var startingMessages = new List<string>();
        if (this.queue.Count > 0)
        {
            startingMessages.Add($"{this.queue.Count} outfit update{(this.queue.Count == 1 ? string.Empty : "s")}");
        }

        if (this.duplicateQueue.Count > 0)
        {
            startingMessages.Add($"{this.duplicateQueue.Count} duplicate item{(this.duplicateQueue.Count == 1 ? string.Empty : "s")}");
        }

        this.status = $"Starting cleanup: {string.Join(", ", startingMessages)}.";
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
        this.currentDuplicateItem = null;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;

        if (this.queue.Count == 0)
        {
            this.currentOutfit = null;
            this.BeginNextDuplicateItem();
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
            this.EnterStep(QueueStep.WaitingForDyedConfirmation, $"{this.currentOutfit.Name} includes dyed pieces. Confirm before continuing.");
            return;
        }

        this.EnterStep(QueueStep.RestoringItems, $"Restoring pieces for {this.currentOutfit.Name}.");
    }

    private void BeginNextDuplicateItem()
    {
        this.currentOutfit = null;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;
        this.ClearRestoreRetryState();

        while (this.duplicateQueue.Count > 0)
        {
            this.currentDuplicateItem = this.duplicateQueue[0];
            this.duplicateQueue.RemoveAt(0);

            var remainingDuplicates = this.CountPrismBoxItems(this.currentDuplicateItem.Key);
            if (remainingDuplicates <= 1)
            {
                this.completedDuplicateItems++;
                continue;
            }

            if (this.CountAvailableInventorySlots() <= 0)
            {
                this.skippedDuplicateItems++;
                this.status = $"Skipped duplicate {this.currentDuplicateItem.Name}; inventory is full.";
                continue;
            }

            this.EnterStep(QueueStep.RestoringDuplicateItem, $"Restoring a duplicate {this.currentDuplicateItem.Name}.");
            return;
        }

        this.currentDuplicateItem = null;
        var skippedMessages = new List<string>();
        if (this.skippedOutfits > 0)
        {
            skippedMessages.Add($"Skipped {this.skippedOutfits} outfit{(this.skippedOutfits == 1 ? string.Empty : "s")}.");
        }

        if (this.skippedDuplicateItems > 0)
        {
            skippedMessages.Add($"Skipped {this.skippedDuplicateItems} duplicate item{(this.skippedDuplicateItems == 1 ? string.Empty : "s")}.");
        }

        var completedMessages = new List<string>();
        if (this.totalQueuedOutfits > 0)
        {
            completedMessages.Add($"Updated {this.completedOutfits} outfit{(this.completedOutfits == 1 ? string.Empty : "s")}.");
        }

        if (this.totalQueuedDuplicateItems > 0)
        {
            completedMessages.Add($"Restored {this.completedDuplicateItems} duplicate item{(this.completedDuplicateItems == 1 ? string.Empty : "s")}.");
        }

        var skippedMessage = skippedMessages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", skippedMessages)}";
        var completedMessage = completedMessages.Count == 0
            ? "Cleanup complete."
            : string.Join(" ", completedMessages);
        this.EnterStep(QueueStep.Complete, $"{completedMessage}{skippedMessage}");
        this.MarkCandidatesDirty();
    }

    private void AdvanceQueue()
    {
        if (this.currentOutfit == null && this.currentDuplicateItem == null)
        {
            this.BeginNextOutfit();
            return;
        }

        switch (this.step)
        {
            case QueueStep.RestoringDuplicateItem:
                if (this.currentDuplicateItem != null)
                {
                    this.RestoreDuplicateItem(this.currentDuplicateItem);
                }

                break;
            case QueueStep.WaitingForDuplicateRestore:
                if (this.currentDuplicateItem != null)
                {
                    this.WaitForDuplicateRestore(this.currentDuplicateItem);
                }

                break;
            case QueueStep.RestoringItems:
                if (this.currentOutfit != null)
                {
                    this.RestoreNextItem(this.currentOutfit);
                }

                break;
            case QueueStep.WaitingForRestore:
                if (this.currentOutfit != null)
                {
                    this.WaitForRestoredItem(this.currentOutfit);
                }

                break;
            case QueueStep.OpeningSetConvert:
                if (this.currentOutfit != null)
                {
                    this.OpenSetConvert(this.currentOutfit);
                }

                break;
            case QueueStep.WaitingForSetConvertOpen:
                if (this.currentOutfit != null)
                {
                    this.WaitForSetConvertOpen(this.currentOutfit);
                }

                break;
            case QueueStep.FillingSetConvert:
                if (this.currentOutfit != null)
                {
                    this.FillSetConvert(this.currentOutfit);
                }

                break;
            case QueueStep.ValidatingSetConvert:
                if (this.currentOutfit != null)
                {
                    this.ValidateSetConvert(this.currentOutfit);
                }

                break;
            case QueueStep.StoringOutfit:
                if (this.currentOutfit != null)
                {
                    this.StoreOutfit(this.currentOutfit);
                }

                break;
            case QueueStep.ConfirmingStore:
                if (this.currentOutfit != null)
                {
                    this.ConfirmStoreOutfit(this.currentOutfit);
                }

                break;
            case QueueStep.WaitingForStore:
                if (this.currentOutfit != null)
                {
                    this.WaitForStoredOutfit(this.currentOutfit);
                }

                break;
        }
    }

    private bool IsTimedQueueStep()
    {
        return this.step != QueueStep.WaitingForDyedConfirmation;
    }

    private void RestoreDuplicateItem(QueuedDuplicateItem? item)
    {
        if (item == null)
        {
            this.BeginNextDuplicateItem();
            return;
        }

        var remainingDuplicates = this.CountPrismBoxItems(item.Key);
        if (remainingDuplicates <= 1)
        {
            this.completedDuplicateItems++;
            this.BeginNextDuplicateItem();
            return;
        }

        if (this.CountAvailableInventorySlots() <= 0)
        {
            this.skippedDuplicateItems++;
            this.status = $"Skipped duplicate {item.Name}; inventory is full.";
            this.BeginNextDuplicateItem();
            return;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.status = "Waiting for the Glamour Dresser before restoring a duplicate item.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var itemIndex = this.FindPrismBoxItemIndex(item.Key);
        if (itemIndex < 0)
        {
            this.completedDuplicateItems++;
            this.BeginNextDuplicateItem();
            return;
        }

        var inventoryCountBeforeRestore = this.CountInventoryBagItems(item.Key);
        if (!manager->RestorePrismBoxItem((uint)itemIndex))
        {
            this.ScheduleItemRestoreRetry(
                item.Key.ItemId,
                "Could not restore a duplicate item yet; waiting briefly before trying again.",
                "Could not restore a duplicate item. Inventory may be full, or you may already have a unique item.");
            return;
        }

        this.ClearRestoreRetryState();
        this.waitingForDuplicateItemKey = item.Key;
        this.waitingForDuplicateInventoryCount = inventoryCountBeforeRestore;
        this.EnterStep(QueueStep.WaitingForDuplicateRestore, $"Waiting for duplicate {item.Name} to return to inventory.");
    }

    private void WaitForDuplicateRestore(QueuedDuplicateItem? item)
    {
        if (item == null || this.waitingForDuplicateItemKey == null)
        {
            this.EnterStep(QueueStep.RestoringDuplicateItem, $"Restoring a duplicate {item?.Name ?? "item"}.");
            return;
        }

        var key = this.waitingForDuplicateItemKey.Value;
        if (this.CountInventoryBagItems(key) <= this.waitingForDuplicateInventoryCount)
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.completedDuplicateItems++;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;
        this.ClearRestoreRetryState();
        this.MarkCandidatesDirty();
        this.BeginNextDuplicateItem();
    }

    private void RestoreNextItem(QueuedOutfit outfit)
    {
        if (!this.HasStartedRestoring(outfit) && this.TrySkipCurrentOutfitWithoutEnoughInventorySpace(outfit))
        {
            return;
        }

        while (outfit.NextRestoreIndex < outfit.RestoreItems.Count
            && this.TryFindInventoryBagItem(outfit.RestoreItems[outfit.NextRestoreIndex], out var existingSlot))
        {
            this.AddRestoredSlot(outfit, existingSlot);
            outfit.NextRestoreIndex++;
        }

        if (outfit.NextRestoreIndex >= outfit.RestoreItems.Count)
        {
            if (!this.TryValidateRestoredInventory(outfit, out var error))
            {
                this.FailQueue(error ?? $"Could not find every piece to add for {outfit.Name} in inventory.");
                return;
            }

            this.EnterStep(QueueStep.OpeningSetConvert, $"Opening outfit creation for {outfit.Name}.");
            return;
        }

        var item = outfit.RestoreItems[outfit.NextRestoreIndex];
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.status = "Waiting for the Glamour Dresser before restoring a piece.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var itemIndex = this.FindPrismBoxItemIndex(item.ItemId);
        if (itemIndex < 0)
        {
            this.ScheduleItemRestoreRetry(
                item.ItemId,
                $"Waiting for a piece to be available in the Glamour Dresser before restoring {outfit.Name}.",
                "Could not find one of the pieces in the Glamour Dresser anymore.");
            return;
        }

        if (!manager->RestorePrismBoxItem((uint)itemIndex))
        {
            this.ScheduleItemRestoreRetry(
                item.ItemId,
                "Could not restore a piece yet; waiting briefly before trying again.",
                "Could not restore an outfit piece. Inventory may be full, or you may already have a unique item.");
            return;
        }

        this.ClearRestoreRetryState();
        this.waitingForRestoredItemId = item.ItemId;
        this.EnterStep(QueueStep.WaitingForRestore, "Waiting for a restored piece to return to inventory.");
    }

    private void WaitForRestoredItem(QueuedOutfit outfit)
    {
        if (this.waitingForRestoredItemId == null)
        {
            this.EnterStep(QueueStep.RestoringItems, $"Restoring pieces for {outfit.Name}.");
            return;
        }

        if (!this.TryFindInventoryBagItem(this.waitingForRestoredItemId.Value, out var restoredSlot))
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.AddRestoredSlot(outfit, restoredSlot);
        outfit.NextRestoreIndex++;
        this.waitingForRestoredItemId = null;
        this.ClearRestoreRetryState();
        this.EnterStep(QueueStep.RestoringItems, $"Restoring pieces for {outfit.Name}.");
    }

    private void AddRestoredSlot(QueuedOutfit outfit, InventorySlot slot)
    {
        for (var i = 0; i < outfit.RestoredSlots.Count; i++)
        {
            var existing = outfit.RestoredSlots[i];
            if (existing.InventoryType != slot.InventoryType || existing.Slot != slot.Slot)
            {
                continue;
            }

            outfit.RestoredSlots[i] = slot;
            return;
        }

        outfit.RestoredSlots.Add(slot);
    }

    private bool TryValidateRestoredInventory(QueuedOutfit outfit, out string? error)
    {
        foreach (var item in outfit.SelectionItems)
        {
            if (!this.TryFindOutfitInventoryItem(outfit, item.ItemId, ItemQualityRequirement.Normal, out var slot))
            {
                error = "Could not find one of the pieces to add in inventory. Stopped before opening outfit creation.";
                return false;
            }

            this.AddRestoredSlot(outfit, slot);
        }

        error = null;
        return true;
    }

    private void InitializeSetConvertOpenInterop()
    {
        var address = this.Services.SigScanner.ScanText(OpenSetConvertSignature);
        if (address == nint.Zero)
        {
            throw new InvalidOperationException("Could not initialize outfit creation support.");
        }

        this.openSetConvertAddress = address;
        this.useCurrentSetConvertOpenSignature = typeof(AgentMiragePrismPrismSetConvert).GetMethod(
            nameof(AgentMiragePrismPrismSetConvert.Open),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(uint), typeof(InventoryType), typeof(int), typeof(ushort), typeof(ushort), typeof(bool)],
            null) != null;
    }

    private void OpenSetConvertAgent(AgentMiragePrismPrismSetConvert* agent, uint itemId, InventoryType inventoryType, int slot, ushort dresserAddonId)
    {
        if (this.openSetConvertAddress == nint.Zero)
        {
            throw new InvalidOperationException("Outfit creation support is not initialized.");
        }

        if (this.useCurrentSetConvertOpenSignature)
        {
            var open = (delegate* unmanaged<AgentMiragePrismPrismSetConvert*, uint, InventoryType, int, ushort, ushort, bool, bool>)(void*)this.openSetConvertAddress;
            _ = open(agent, itemId, inventoryType, slot, 0, dresserAddonId, true);
            return;
        }

        var legacyOpen = (delegate* unmanaged<AgentMiragePrismPrismSetConvert*, uint, InventoryType, int, int, bool, void>)(void*)this.openSetConvertAddress;
        legacyOpen(agent, itemId, inventoryType, slot, dresserAddonId, true);
    }

    private void OpenSetConvert(QueuedOutfit outfit)
    {
        var dresserAddon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
        if (dresserAddon.IsNull || dresserAddon.Address == IntPtr.Zero)
        {
            this.FailQueue("Glamour Dresser is not available.");
            return;
        }

        var agent = AgentMiragePrismPrismSetConvert.Instance();
        if (agent == null)
        {
            this.FailQueue("Outfit creation is not available.");
            return;
        }

        if (!this.TryGetNextSetConvertSource(outfit, out var sourceItem, out var sourceSlot))
        {
            if (outfit.IsMerge && !outfit.StoredSetConvertOpenAttempted)
            {
                outfit.StoredSetConvertOpenAttempted = true;
                this.OpenSetConvertFromStoredOutfit(outfit, agent, dresserAddon.Id);
                return;
            }

            this.SkipCurrentOutfit($"Skipped {outfit.Name}; the game did not offer that outfit for the available pieces.");
            return;
        }

        try
        {
            outfit.SetConvertSourceItemIdsTried.Add(sourceItem.ItemId);
            outfit.CurrentSetConvertSourceItemId = sourceItem.ItemId;
            outfit.SetConvertOutfitSwitchAttempted = false;
            outfit.NativeAddendumAccepted = false;

            this.pendingSetConvertSlot = null;
            this.pendingSetConvertItemId = null;

            this.Services.Log.Debug(
                "Glamour Outfit Compactor opening Outfit Glamour Creation for {OutfitName} from item {ItemId} at {InventoryType}:{Slot}",
                outfit.Name,
                sourceSlot.RawItemId,
                sourceSlot.InventoryType,
                sourceSlot.Slot);
            this.OpenSetConvertAgent(agent, sourceSlot.RawItemId, sourceSlot.InventoryType, (int)sourceSlot.Slot, dresserAddon.Id);
        }
        catch (Exception ex)
        {
            this.FailQueue("Could not open outfit creation for the piece to add.");
            this.Services.Log.Warning(ex, "Failed to open Outfit Glamour Creation for {OutfitName} from item {ItemId}", outfit.Name, sourceSlot.RawItemId);
            return;
        }

        this.EnterStep(QueueStep.WaitingForSetConvertOpen, $"Waiting for outfit creation to open for {outfit.Name}.");
    }

    private void OpenSetConvertFromStoredOutfit(QueuedOutfit outfit, AgentMiragePrismPrismSetConvert* agent, ushort dresserAddonId)
    {
        var itemIndex = this.FindPrismBoxItemIndex(outfit.SetItemId);
        if (itemIndex < 0)
        {
            this.SkipCurrentOutfit($"Skipped {outfit.Name}; the existing outfit is no longer in the Glamour Dresser.");
            return;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.FailQueue("Glamour Dresser data is not available.");
            return;
        }

        var rawSetItemId = manager->PrismBoxItemIds[itemIndex];
        if (GetBaseItemId(rawSetItemId) != outfit.SetItemId)
        {
            this.FailQueue("Glamour Dresser data changed before outfit creation could open.");
            return;
        }

        try
        {
            outfit.CurrentSetConvertSourceItemId = null;
            outfit.SetConvertOutfitSwitchAttempted = false;
            outfit.NativeAddendumAccepted = false;
            this.pendingSetConvertSlot = null;
            this.pendingSetConvertItemId = null;

            this.Services.Log.Debug(
                "Glamour Outfit Compactor opening Outfit Glamour Creation for existing outfit {OutfitName} from Glamour Dresser index {ItemIndex}",
                outfit.Name,
                itemIndex);
            this.TryOpenStoredOutfitAddMissing(outfit, agent, dresserAddonId);
        }
        catch (Exception ex)
        {
            this.FailQueue("Could not open outfit creation for the existing outfit.");
            this.Services.Log.Warning(ex, "Failed to open Outfit Glamour Creation for existing outfit {OutfitName} at Glamour Dresser index {ItemIndex}", outfit.Name, itemIndex);
            return;
        }

        this.EnterStep(QueueStep.WaitingForSetConvertOpen, $"Waiting for outfit creation to open for {outfit.Name}.");
    }

    private bool TryOpenStoredOutfitAddMissing(QueuedOutfit outfit, AgentMiragePrismPrismSetConvert* agent, ushort dresserAddonId)
    {
        if (!this.TryPrepareStoredOutfitContext(outfit, agent, out var contextError))
        {
            this.Services.Log.Debug(
                "Glamour Outfit Compactor could not prepare Add Missing Outfit Gear for {OutfitName}: {Error}",
                outfit.Name,
                contextError ?? "unknown error");
            this.status = $"Opening existing outfit {outfit.Name}.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return false;
        }

        if (this.TrySelectAddMissingOutfitGearContextMenu(dresserAddonId, out var menuError))
        {
            return true;
        }

        this.Services.Log.Debug(
            "Glamour Outfit Compactor could not use native Add Missing Outfit Gear context menu for {OutfitName}: {Error}",
            outfit.Name,
            menuError ?? "unknown error");
        this.status = $"Opening existing outfit {outfit.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
        return false;
    }

    private void WaitForSetConvertOpen(QueuedOutfit outfit)
    {
        if (this.TryHandleAddendumPrompt(outfit, out var promptError) || promptError != null)
        {
            if (promptError != null)
            {
                this.FailQueue(promptError);
            }

            return;
        }

        if (!this.TryGetSetConvertAddon(out var addon))
        {
            if (outfit.IsMerge)
            {
                var dresserAddon = this.Services.GameGui.GetAddonByName(DresserAddonName, 1);
                var agent = AgentMiragePrismPrismSetConvert.Instance();
                if (!dresserAddon.IsNull
                    && dresserAddon.Address != IntPtr.Zero
                    && agent != null)
                {
                    this.TryOpenStoredOutfitAddMissing(outfit, agent, dresserAddon.Id);
                }
            }

            this.status = $"Waiting for outfit creation to open for {outfit.Name}.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (!this.TryReadSetConvertUiItems(addon, out var dataItems, out var error))
        {
            if (error != null)
            {
                this.status = error;
            }

            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        var setItems = outfit.SetItemIds.ToHashSet();
        var dataItemIds = dataItems.Select(item => item.ItemId).ToHashSet();
        if (!dataItemIds.SetEquals(setItems))
        {
            if (this.TrySwitchSetConvertOutfit(outfit, out error))
            {
                return;
            }

            error ??= "Outfit creation opened for a different outfit and could not switch to the current outfit.";
            if (!this.TryRecoverFromSetConvertError(outfit, error))
            {
                this.FailQueue(error);
            }

            return;
        }

        if (outfit.IsMerge)
        {
            var agent = AgentMiragePrismPrismSetConvert.Instance();
            if (agent == null || !this.TryPrepareStoredOutfitContext(outfit, agent, out error))
            {
                this.FailQueue(error ?? "Could not prepare the existing outfit context.");
                return;
            }
        }

        this.EnterStep(QueueStep.FillingSetConvert, $"Selecting pieces for {outfit.Name}.");
    }

    private bool TryPrepareStoredOutfitContext(QueuedOutfit outfit, AgentMiragePrismPrismSetConvert* setConvertAgent, out string? error)
    {
        error = null;
        var itemIndex = this.FindPrismBoxItemIndex(outfit.SetItemId);
        if (itemIndex < 0)
        {
            error = $"Stored outfit {outfit.Name} is no longer in the Glamour Dresser.";
            return false;
        }

        var prismBoxAgent = AgentMiragePrismPrismBox.Instance();
        if (prismBoxAgent == null || prismBoxAgent->Data == null)
        {
            error = "Glamour Dresser data is not available.";
            return false;
        }

        prismBoxAgent->Data->TempContextItemIndex = itemIndex;
        prismBoxAgent->Data->TempContextItem = prismBoxAgent->Data->PrismBoxItems[itemIndex];

        if (setConvertAgent->Data != null)
        {
            setConvertAgent->Data->ContextMenuItemIndex = itemIndex;
        }

        return true;
    }

    private bool TrySelectAddMissingOutfitGearContextMenu(ushort dresserAddonId, out string? error)
    {
        error = null;
        var contextAgent = AgentContext.Instance();
        if (contextAgent == null)
        {
            error = "Could not open Add Missing Outfit Gear because the native context menu state is not available.";
            return false;
        }

        contextAgent->OpenContextMenuForAddon(dresserAddonId, true);

        var contextMenuHandle = this.Services.GameGui.GetAddonByName(ContextMenuAddonName, 1);
        if (contextMenuHandle.IsNull || contextMenuHandle.Address == IntPtr.Zero)
        {
            error = "Could not open Add Missing Outfit Gear because the native context menu is not available.";
            return false;
        }

        var addon = (AtkUnitBase*)contextMenuHandle.Address;
        if (!addon->IsVisible)
        {
            error = "The native context menu did not open.";
            return false;
        }

        if (!this.TryReadAtkUInt(addon, 0, out var entryCount) || entryCount == 0)
        {
            error = "Could not open Add Missing Outfit Gear because the native context menu was not prepared.";
            return false;
        }

        if (!this.TryFindContextMenuEntry(addon, (int)entryCount, AddMissingOutfitGearContextMenuText, out var entryIndex))
        {
            error = "Could not open Add Missing Outfit Gear because the native context menu does not contain the expected action.";
            return false;
        }

        this.Services.Log.Debug(
            "Glamour Outfit Compactor selecting native context menu entry {EntryIndex}: {ContextMenuDiagnostic}",
            entryIndex,
            $"{GetAtkValuesDiagnostic(addon)}, entries={this.GetContextMenuEntryTextDiagnostic(addon, Math.Min((int)entryCount, MaxContextMenuEntries))}");
        var contextMenu = (AddonContextMenu*)addon;
        if (!contextMenu->OnMenuSelected(entryIndex, 0))
        {
            error = "Could not open Add Missing Outfit Gear from the native context menu.";
            return false;
        }

        return true;
    }

    private bool TryGetNextSetConvertSource(QueuedOutfit outfit, out CandidateItem sourceItem, out InventorySlot sourceSlot)
    {
        foreach (var item in outfit.SelectionItems
            .OrderBy(item => this.GetSetConvertSourceRank(item.ItemId, outfit.SetItemId))
            .ThenBy(item => item.ItemId))
        {
            if (outfit.SetConvertSourceItemIdsTried.Contains(item.ItemId))
            {
                continue;
            }

            if (this.IsUnsupportedSetConvertSource(item.ItemId, outfit.SetItemId))
            {
                continue;
            }

            if (!this.TryFindOutfitInventoryItem(outfit, item.ItemId, out sourceSlot))
            {
                continue;
            }

            sourceItem = item;
            return true;
        }

        sourceItem = null!;
        sourceSlot = default;
        return false;
    }

    private bool HasSupportedSetConvertSource(OutfitCandidate candidate)
    {
        return candidate.IsMerge || candidate.SelectionItems.Any(item => !this.IsUnsupportedSetConvertSource(item.ItemId, candidate.SetItemId));
    }

    private int GetSetConvertSourceRank(uint itemId, uint setItemId)
    {
        var setItemIds = this.GetLookupSetItemIds(itemId);
        if (setItemIds.Length <= 1)
        {
            return 0;
        }

        return setItemIds[0] == setItemId ? 1 : 2;
    }

    private uint[] GetLookupSetItemIds(uint itemId)
    {
        var lookupSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItemLookup>();
        return !lookupSheet.TryGetRow(itemId, out var lookupRow)
            ? []
            : lookupRow.Item
                .Where(setItem => setItem.RowId != 0 && setItem.IsValid)
                .Select(setItem => setItem.RowId)
                .ToArray();
    }

    private void FillSetConvert(QueuedOutfit outfit)
    {
        if (this.TryHandleAddendumPrompt(outfit, out var promptError) || promptError != null)
        {
            if (promptError != null)
            {
                this.FailQueue(promptError);
            }

            return;
        }

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

            if (!this.TryRecoverFromSetConvertError(outfit, error))
            {
                this.FailQueue(error);
            }

            return;
        }

        this.EnterStep(QueueStep.ValidatingSetConvert, $"Checking selected pieces for {outfit.Name}.");
    }

    private void ValidateSetConvert(QueuedOutfit outfit)
    {
        if (this.TryHandleAddendumPrompt(outfit, out var promptError) || promptError != null)
        {
            if (promptError != null)
            {
                this.FailQueue(promptError);
            }

            return;
        }

        if (!this.IsSetConvertOpen())
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryValidateSetConvertItems(outfit, out var error))
        {
            this.EnterStep(QueueStep.StoringOutfit, $"Storing {outfit.Name}.");
            return;
        }

        if (error != null)
        {
            if (!this.TryRecoverFromSetConvertError(outfit, error))
            {
                this.FailQueue(error);
            }

            return;
        }

        if (!this.TryFillSetConvertItems(outfit, out error))
        {
            if (error == null)
            {
                this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
                return;
            }

            if (!this.TryRecoverFromSetConvertError(outfit, error))
            {
                this.FailQueue(error);
            }

            return;
        }

        this.status = $"Revalidating selected pieces for {outfit.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
    }

    private void StoreOutfit(QueuedOutfit outfit)
    {
        if (!this.TryValidateSetConvertItems(outfit, out var error))
        {
            if (error != null)
            {
                if (!this.TryRecoverFromSetConvertError(outfit, error))
                {
                    this.FailQueue(error);
                }

                return;
            }

            this.EnterStep(QueueStep.ValidatingSetConvert, $"Checking selected pieces for {outfit.Name}.");
            return;
        }

        if (!this.TryGetSetConvertAddon(out var addon))
        {
            this.FailQueue("Outfit creation is not ready for Store.");
            return;
        }

        if (!this.TryClickButton(addon, StoreAsGlamourButtonId))
        {
            this.FailQueue("Store as Outfit Glamour was not available.");
            return;
        }

        this.EnterStep(QueueStep.ConfirmingStore, $"Confirming storage for {outfit.Name}.");
    }

    private void ConfirmStoreOutfit(QueuedOutfit outfit)
    {
        var manager = MirageManager.Instance();
        if (!outfit.IsNativeAddendum
            && manager != null
            && manager->PrismBoxLoaded
            && this.FindPrismBoxItemIndex(outfit.SetItemId) >= 0)
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
            this.status = "Waiting for the confirmation to enable Yes after selecting Store as Outfit Glamour.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryForceEnableConfirmYes(addon, ConfirmStoreAsOutfitCheckBoxId, ConfirmYesButtonId))
        {
            this.status = "Preparing the storage confirmation.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (this.TryNotifyCheckedCheckBox(addon, ConfirmStoreAsOutfitCheckBoxId))
        {
            this.status = "Waiting for the confirmation to enable Yes after selecting Store as Outfit Glamour.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        this.status = "Waiting for the storage confirmation controls.";
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

        if (this.FindPrismBoxItemIndex(outfit.SetItemId) < 0)
        {
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            return;
        }

        if (outfit.RestoredSlots.Count == 0 || outfit.RestoredSlots.Any(slot => !this.IsInventorySlotConsumed(slot)))
        {
            this.status = $"Waiting for added pieces to leave inventory after storing {outfit.Name}.";
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

        if (this.TryHandleAddendumPrompt(outfit, out error) || error != null)
        {
            return false;
        }

        if (!this.TryGetSetConvertAddon(out var addon))
        {
            return false;
        }

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

        var dataItemIds = dataItems.Select(item => item.ItemId).ToHashSet();
        if (!dataItemIds.SetEquals(setItems))
        {
            if (this.TrySwitchSetConvertOutfit(outfit, out error))
            {
                return false;
            }

            error ??= "Outfit creation opened for a different outfit and could not switch to the current outfit.";
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

        foreach (var item in dataItems)
        {
            if (!expectedItems.Contains(item.ItemId))
            {
                continue;
            }

            if (!this.TryFindOutfitInventoryItem(outfit, item.ItemId, out var slot))
            {
                error = "Could not find one of the pieces to add in inventory.";
                return false;
            }

            if (this.IsSetConvertUiItemSelected(item, slot))
            {
                continue;
            }

            this.FireSetConvertHandOverCallback(addon, item.Index);
            this.pendingSetConvertSlot = item.Index;
            this.pendingSetConvertItemId = item.ItemId;
            this.status = $"Selecting a piece for {outfit.Name}.";
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

    private bool TrySwitchSetConvertOutfit(QueuedOutfit outfit, out string? error)
    {
        error = null;

        if (outfit.SetConvertOutfitSwitchAttempted)
        {
            error = "Outfit creation opened for a different outfit after switching once. Stopped before storing.";
            return false;
        }

        var agent = AgentMiragePrismPrismSetConvert.Instance();
        if (agent == null || agent->Data == null)
        {
            error = "Outfit creation data is still loading.";
            return false;
        }

        if (!agent->Data->ItemSets.ToArray().Any(itemSet => GetBaseItemId(itemSet.ItemId) == outfit.SetItemId))
        {
            if (outfit.CurrentSetConvertSourceItemId is { } sourceItemId)
            {
                this.unsupportedSetConvertSources.Add(new SetConvertSourceKey(sourceItemId, outfit.SetItemId));
            }

            var availableSetItemIds = agent->Data->ItemSets.ToArray()
                .Select(itemSet => GetBaseItemId(itemSet.ItemId))
                .Where(itemId => itemId != 0)
                .ToArray();
            this.Services.Log.Warning(
                "Glamour Outfit Compactor could not use source item {SourceItemId} for {OutfitName} ({SetItemId}); available sets were {AvailableSetItemIds}",
                outfit.CurrentSetConvertSourceItemId.GetValueOrDefault(),
                outfit.Name,
                outfit.SetItemId,
                string.Join(", ", availableSetItemIds));
            error = SetConvertTargetUnavailableError;
            return false;
        }

        if (outfit.IsMerge && !this.TryPrepareStoredOutfitContext(outfit, agent, out error))
        {
            return false;
        }

        var agentItems = agent->Data->Items;
        if (outfit.SetItemIds.Length > agentItems.Length || outfit.SetItemIds.Length != outfit.SetSlotIndexes.Length)
        {
            error = "The outfit data did not match the outfit creation window.";
            return false;
        }

        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        for (var i = 0; i < agentItems.Length; i++)
        {
            agentItems[i] = default;
        }

        for (var i = 0; i < outfit.SetItemIds.Length; i++)
        {
            var itemId = outfit.SetItemIds[i];
            ref var item = ref agentItems[i];
            item.ItemId = itemId;
            item.IconId = itemSheet.TryGetRow(itemId, out var itemRow) ? (uint)itemRow.Icon : 0;
            SetSetConvertItemSlotIndex(ref item, (uint)outfit.SetSlotIndexes[i]);
            item.InventoryType = InventoryType.Invalid;
            item.Slot = 0;
        }

        agent->Data->NumItemsInSet = (uint)outfit.SetItemIds.Length;
        outfit.SetConvertOutfitSwitchAttempted = true;
        this.pendingSetConvertSlot = null;
        this.pendingSetConvertItemId = null;
        agent->Update(SetConvertRefreshFlags);
        this.status = $"Switching outfit creation to {outfit.Name}.";
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
        this.Services.Log.Debug(
            "Glamour Outfit Compactor switched Outfit Glamour Creation to {OutfitName} ({SetItemId})",
            outfit.Name,
            outfit.SetItemId);
        return true;
    }

    private bool TryRecoverFromSetConvertError(QueuedOutfit outfit, string error)
    {
        if (!string.Equals(error, SetConvertTargetUnavailableError, StringComparison.Ordinal))
        {
            return false;
        }

        if (this.HasAvailableSetConvertSource(outfit))
        {
            this.EnterStep(QueueStep.OpeningSetConvert, $"Trying another piece for {outfit.Name}.");
            return true;
        }

        if (outfit.IsMerge && !outfit.StoredSetConvertOpenAttempted)
        {
            this.EnterStep(QueueStep.OpeningSetConvert, $"Trying the stored outfit for {outfit.Name}.");
            return true;
        }

        this.SkipCurrentOutfit($"Skipped {outfit.Name}; the game did not offer that outfit for the available pieces.");
        return true;
    }

    private bool HasAvailableSetConvertSource(QueuedOutfit outfit)
    {
        return outfit.SelectionItems.Any(item =>
            !outfit.SetConvertSourceItemIdsTried.Contains(item.ItemId)
            && !this.IsUnsupportedSetConvertSource(item.ItemId, outfit.SetItemId)
            && this.TryFindOutfitInventoryItem(outfit, item.ItemId, out _));
    }

    private bool IsUnsupportedSetConvertSource(uint itemId, uint setItemId)
    {
        return this.unsupportedSetConvertSources.Contains(new SetConvertSourceKey(itemId, setItemId));
    }

    private static void SetSetConvertItemSlotIndex(ref AgentMiragePrismPrismSetConvert.AgentData.ItemSetItem item, uint slotIndex)
    {
        // Current FFXIVClientStructs exposes SlotIndex as private; the generated layout places it at 0x08.
        var itemPtr = (byte*)Unsafe.AsPointer(ref item);
        *(uint*)(itemPtr + 8) = slotIndex;
    }

    private static uint GetSetConvertItemSlotIndex(ref AgentMiragePrismPrismSetConvert.AgentData.ItemSetItem item)
    {
        var itemPtr = (byte*)Unsafe.AsPointer(ref item);
        return *(uint*)(itemPtr + 8);
    }

    private string GetSetConvertFillDiagnostic(QueuedOutfit outfit)
    {
        if (this.TryGetSelectYesNoAddon(out var selectYesNoAddon)
            && IsAddendumPrompt(GetSelectYesNoPromptText(selectYesNoAddon)))
        {
            return $"Waiting on addendum confirmation prompt after {outfit.AddendumPromptAttempts} attempt(s). {GetSelectYesNoDiagnostic((AtkUnitBase*)selectYesNoAddon)}";
        }

        if (!this.TryGetSetConvertAddon(out var addon))
        {
            return $"Outfit Glamour Creation addon was not available. Checked {SetConvertAddonName} and {SetConvertAlternateAddonName}.";
        }

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

        if (this.TryHandleAddendumPrompt(outfit, out error) || error != null)
        {
            return false;
        }

        if (!this.TryGetSetConvertAddon(out var addon))
        {
            return false;
        }

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

        var dataItemIds = dataItems.Select(item => item.ItemId).ToHashSet();
        if (!dataItemIds.SetEquals(setItems))
        {
            if (!outfit.SetConvertOutfitSwitchAttempted && this.TrySwitchSetConvertOutfit(outfit, out error))
            {
                return false;
            }

            error ??= "Outfit creation opened for a different outfit and could not switch to the current outfit.";
            return false;
        }

        if (!this.TryValidateStoredOutfit(outfit, out error))
        {
            return false;
        }

        var allowedSelectedItems = outfit.NativeAddendumAccepted
            ? outfit.SetItemIds.ToHashSet()
            : expectedItems.Concat(outfit.StoredSetItemIds).ToHashSet();
        if (dataItems.Any(item => !allowedSelectedItems.Contains(item.ItemId) && item.InventoryType != (uint)InventoryType.Invalid))
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

            if (!this.TryFindOutfitInventoryItem(outfit, expectedItemId, out var slot))
            {
                error = "Could not find one of the selected pieces in inventory.";
                return false;
            }

            if (!this.IsSetConvertUiItemSelected(selectedItem, slot))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryHandleAddendumPrompt(QueuedOutfit outfit, out string? error)
    {
        error = null;

        if (!this.TryGetSelectYesNoAddon(out var addon))
        {
            return false;
        }

        var prompt = GetSelectYesNoPromptText(addon);
        if (!IsAddendumPrompt(prompt))
        {
            return false;
        }

        if (!this.TryValidateStoredOutfit(outfit, out error))
        {
            return error == null;
        }

        var unitBase = (AtkUnitBase*)addon;
        outfit.AddendumPromptAttempts++;
        var method = this.TryConfirmSelectYesNoPrompt(unitBase, outfit.AddendumPromptAttempts);
        if (method == null)
        {
            this.status = $"Waiting for the existing outfit prompt for {outfit.Name}.";
            this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
            this.Services.Log.Warning(
                "Glamour Outfit Compactor could not confirm addendum prompt for {OutfitName}: attempt={Attempt}, {Diagnostic}",
                outfit.Name,
                outfit.AddendumPromptAttempts,
                GetSelectYesNoDiagnostic(unitBase));
            return true;
        }

        this.status = $"Confirming the existing outfit prompt for {outfit.Name}.";
        outfit.NativeAddendumAccepted = true;
        this.nextActionAt = DateTimeOffset.UtcNow + CandidateRefreshDelay;
        this.Services.Log.Warning(
            "Glamour Outfit Compactor tried addendum prompt confirmation for {OutfitName}: attempt={Attempt}, method={Method}, {Diagnostic}",
            outfit.Name,
            outfit.AddendumPromptAttempts,
            method,
            GetSelectYesNoDiagnostic(unitBase));
        return true;
    }

    private string? TryConfirmSelectYesNoPrompt(AtkUnitBase* addon, int attempt)
    {
        switch (attempt)
        {
            case 1:
            case 2:
                return this.TryClickButton(addon, SelectYesNoYesButtonId)
                    ? $"button {SelectYesNoYesButtonId}"
                    : null;
            case 3:
                this.FireSelectYesNoCallback(addon, 0);
                return "callback [0]";
            case 4:
                this.FireSelectYesNoCallback(addon, 0, 0);
                return "callback [0,0]";
            default:
                this.FireSelectYesNoCallback(addon, 0, 1);
                return "callback [0,1]";
        }
    }

    private bool TryGetSelectYesNoAddon(out AddonSelectYesno* addon)
    {
        if (this.TryGetSelectYesNoAddonByName(SelectYesNoAddonName, out addon))
        {
            return true;
        }

        return this.TryGetSelectYesNoAddonByName(SelectYesNoAlternateAddonName, out addon);
    }

    private bool TryGetSelectYesNoAddonByName(string addonName, out AddonSelectYesno* addon)
    {
        var addonHandle = this.Services.GameGui.GetAddonByName(addonName, 1);
        if (addonHandle.IsNull
            || addonHandle.Address == IntPtr.Zero
            || !addonHandle.IsReady
            || !addonHandle.IsVisible)
        {
            addon = null;
            return false;
        }

        addon = (AddonSelectYesno*)addonHandle.Address;
        return true;
    }

    private static bool IsAddendumPrompt(string prompt)
    {
        var normalizedPrompt = NormalizePromptText(prompt);
        return normalizedPrompt.Contains(AddendumPromptFirstLine, StringComparison.Ordinal)
            && normalizedPrompt.Contains(AddendumPromptSecondLine, StringComparison.Ordinal);
    }

    private static string NormalizePromptText(string prompt)
    {
        return new string(prompt
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Select(character => char.IsControl(character) ? '\n' : character)
            .ToArray())
            .Trim();
    }

    private static string GetSelectYesNoPromptText(AddonSelectYesno* addon)
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

    private static string GetSelectYesNoDiagnostic(AtkUnitBase* addon)
    {
        var yesButton = addon->GetComponentButtonById(SelectYesNoYesButtonId);
        var yesDiagnostic = yesButton == null
            ? "yesButton=null"
            : $"yesButton=enabled:{yesButton->IsEnabled}, visible:{(yesButton->AtkResNode != null && yesButton->AtkResNode->IsVisible())}";

        var prompt = NormalizePromptText(GetSelectYesNoPromptText((AddonSelectYesno*)addon))
            .Replace("\n", " ", StringComparison.Ordinal);
        return $"addonId={addon->Id}, values={addon->AtkValuesCount}, {yesDiagnostic}, prompt=\"{prompt}\"";
    }

    private string GetContextMenuEntryTextDiagnostic(AtkUnitBase* addon, int entryCount)
    {
        var entries = new List<string>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            if (this.TryGetContextMenuEntryText(addon, i, out var text))
            {
                entries.Add($"{i}:\"{text}\"");
            }
        }

        return $"[{string.Join("; ", entries)}]";
    }

    private bool TryFindContextMenuEntry(AtkUnitBase* addon, int entryCount, string expectedText, out int entryIndex)
    {
        var cappedEntryCount = Math.Min(entryCount, MaxContextMenuEntries);
        for (var i = 0; i < cappedEntryCount; i++)
        {
            if (this.TryGetContextMenuEntryText(addon, i, out var text)
                && string.Equals(text, expectedText, StringComparison.Ordinal))
            {
                entryIndex = i;
                return true;
            }
        }

        entryIndex = -1;
        return false;
    }

    private bool TryGetContextMenuEntryText(AtkUnitBase* addon, int entryIndex, out string text)
    {
        var valueIndex = 8 + entryIndex;
        if (addon->AtkValues == null || valueIndex < 0 || (uint)valueIndex >= addon->AtkValuesCount)
        {
            text = string.Empty;
            return false;
        }

        text = NormalizeDiagnosticText(ReadAtkValueString(addon->AtkValues[valueIndex]));
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string GetAtkValuesDiagnostic(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            return "addon=null";
        }

        if (addon->AtkValues == null)
        {
            return $"addonId={addon->Id}, values=null";
        }

        var valueCount = Math.Min((int)addon->AtkValuesCount, MaxDiagnosticAtkValues);
        var values = new List<string>(valueCount);
        for (var i = 0; i < valueCount; i++)
        {
            values.Add($"{i}:{FormatAtkValue(addon->AtkValues[i])}");
        }

        return $"addonId={addon->Id}, values={addon->AtkValuesCount}, firstValues=[{string.Join("; ", values)}]";
    }

    private static string FormatAtkValue(AtkValue value)
    {
        return value.Type switch
        {
            AtkValueType.Bool => $"Bool:{value.Bool}",
            AtkValueType.Int => $"Int:{value.Int}",
            AtkValueType.Int64 => $"Int64:{value.Int64}",
            AtkValueType.UInt => $"UInt:{value.UInt}",
            AtkValueType.UInt64 => $"UInt64:{value.UInt64}",
            AtkValueType.Float => $"Float:{value.Float}",
            AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString or AtkValueType.WideString => $"String:\"{NormalizeDiagnosticText(ReadAtkValueString(value))}\"",
            AtkValueType.Pointer => $"Pointer:0x{(nint)value.Pointer:X}",
            AtkValueType.Null => "Null",
            AtkValueType.Undefined => "Undefined",
            _ => value.Type.ToString()
        };
    }

    private static string NormalizeDiagnosticText(string text)
    {
        var normalized = NormalizePromptText(text)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 96
            ? normalized
            : string.Concat(normalized.AsSpan(0, 96), "...");
    }

    private bool TryValidateStoredOutfit(QueuedOutfit outfit, out string? error)
    {
        error = null;
        if (!outfit.IsMerge)
        {
            return true;
        }

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            return false;
        }

        var setItemIndex = this.FindPrismBoxItemIndex(outfit.SetItemId);
        if (setItemIndex < 0)
        {
            error = $"Stored outfit {outfit.Name} is no longer in the Glamour Dresser.";
            return false;
        }

        foreach (var slotIndex in outfit.StoredSlotIndexes)
        {
            if (!manager->IsSetSlotUnlocked((uint)setItemIndex, slotIndex))
            {
                error = $"Stored outfit {outfit.Name} changed before it could be updated.";
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
            return this.TryReadSetConvertAgentItems(out items, out error);
        }

        var itemCount = (int)itemCountValue;
        if (itemCount <= 0)
        {
            return this.TryReadSetConvertAgentItems(out items, out error);
        }

        var requiredValueCount = SetConvertUiItemsOffset + (itemCount * SetConvertUiItemStride);
        if (addon->AtkValuesCount < requiredValueCount)
        {
            if (this.TryReadSetConvertAgentItems(out items, out error))
            {
                return true;
            }

            error = "Outfit creation data is still loading.";
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
                if (this.TryReadSetConvertAgentItems(out items, out error))
                {
                    return true;
                }

                error = "Could not read one of the outfit creation rows.";
                return false;
            }

            readItems.Add(new SetConvertUiItem(i, GetBaseItemId(itemId), inventoryType, slot, flag));
        }

        items = readItems.ToArray();
        return items.Length > 0 || this.TryReadSetConvertAgentItems(out items, out error);
    }

    private bool TryReadSetConvertAgentItems(out SetConvertUiItem[] items, out string? error)
    {
        items = [];
        error = null;

        var agent = AgentMiragePrismPrismSetConvert.Instance();
        if (agent == null || agent->Data == null)
        {
            error = "Outfit creation data is still loading.";
            return false;
        }

        var agentItems = agent->Data->Items;
        var itemLimit = agentItems.Length;
        var declaredItemCount = (int)agent->Data->NumItemsInSet;
        if (declaredItemCount > 0)
        {
            itemLimit = Math.Min(declaredItemCount, agentItems.Length);
        }

        var readItems = new List<SetConvertUiItem>(itemLimit);
        for (var i = 0; i < itemLimit; i++)
        {
            var item = agentItems[i];
            if (item.ItemId == 0)
            {
                continue;
            }

            var slotIndex = GetSetConvertItemSlotIndex(ref item);
            readItems.Add(new SetConvertUiItem(i, GetBaseItemId(item.ItemId), (uint)item.InventoryType, item.Slot, slotIndex));
        }

        items = readItems.ToArray();
        if (items.Length == 0)
        {
            error = "Outfit creation data is still loading.";
            return false;
        }

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

    private void FireSelectYesNoCallback(AtkUnitBase* addon, int response, int? extraValue = null)
    {
        var valueCount = extraValue == null ? 1u : 2u;
        var values = stackalloc AtkValue[(int)valueCount];
        values[0] = this.CreateAtkInt(response);
        if (extraValue != null)
        {
            values[1] = this.CreateAtkInt(extraValue.Value);
        }

        addon->FireCallback(valueCount, values, true);
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
            this.FailQueue($"{finalError} Tried {MaxRestoreAttempts} times.");
            return;
        }

        this.status = $"{retryStatus} Trying again ({this.restoreRetryAttempts + 1}/{MaxRestoreAttempts}).";
        this.nextActionAt = DateTimeOffset.UtcNow + RestoreRetryDelay;
        this.MarkCandidatesDirty();
        this.Services.Log.Debug(
            "Glamour Outfit Compactor restore retry for item {ItemId}: attempt {NextAttempt}/{MaxAttempts}",
            itemId,
            this.restoreRetryAttempts + 1,
            MaxRestoreAttempts);
    }

    private void ClearRestoreRetryState()
    {
        this.restoreRetryItemId = null;
        this.restoreRetryAttempts = 0;
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
        this.duplicateQueue.Clear();
        this.currentOutfit = null;
        this.currentDuplicateItem = null;
        this.completedOutfits = 0;
        this.skippedOutfits = 0;
        this.completedDuplicateItems = 0;
        this.skippedDuplicateItems = 0;
        this.totalQueuedOutfits = 0;
        this.totalQueuedDuplicateItems = 0;
        this.waitingForRestoredItemId = null;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;
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
        this.duplicateQueue.Clear();
        this.currentOutfit = null;
        this.currentDuplicateItem = null;
        this.waitingForRestoredItemId = null;
        this.waitingForDuplicateItemKey = null;
        this.waitingForDuplicateInventoryCount = 0;
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
        this.duplicateCandidates.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            this.lastPrismBoxItemIds = [];
            if (this.step == QueueStep.Idle)
            {
                this.status = "Waiting for the Glamour Dresser.";
            }

            return;
        }

        this.lastPrismBoxItemIds = manager->PrismBoxItemIds.ToArray();

        var itemIndexes = new Dictionary<uint, PrismBoxCandidateItem>();
        var inventoryItems = this.GetInventoryCandidateItems();
        var setSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItem>();
        var lookupSheet = this.Services.DataManager.GetExcelSheet<MirageStoreSetItemLookup>();
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        var outfitSetItemIds = setSheet.Select(row => row.RowId).Where(rowId => rowId != 0).ToHashSet();

        for (var i = 0; i < this.lastPrismBoxItemIds.Length; i++)
        {
            var rawItemId = this.lastPrismBoxItemIds[i];
            var itemId = GetBaseItemId(rawItemId);
            if (itemId == 0)
            {
                continue;
            }

            if (IsHighQualityItem(rawItemId))
            {
                continue;
            }

            if (itemIndexes.ContainsKey(itemId))
            {
                continue;
            }

            itemIndexes[itemId] = new PrismBoxCandidateItem(i);
        }

        if (this.RestoreDuplicateItems)
        {
            this.RebuildDuplicateCandidates(manager, itemSheet, outfitSetItemIds);
        }

        var rawCandidates = new List<OutfitCandidate>();
        var checkedSetIds = new HashSet<uint>();
        foreach (var itemId in itemIndexes.Keys.Concat(inventoryItems.Keys).Distinct())
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
                var hasStoredOutfit = false;
                if (!rowIdIsSetPiece && itemIndexes.TryGetValue(setItemId, out var storedSetItem))
                {
                    hasStoredOutfit = true;
                    storedSetIndex = storedSetItem.Index;
                }
                var selectionItems = new List<CandidateItem>();
                var restoreItems = new List<CandidateItem>();
                var inventoryItemIds = new List<uint>();
                var storedSlotIndexes = new List<int>();
                var storedSetItemIds = new List<uint>();

                foreach (var setSlot in setSlots)
                {
                    var hasLooseItem = this.TryCreateCandidateItem(setSlot.ItemId, itemIndexes, manager, out var looseItem);
                    var hasInventoryItem = inventoryItems.TryGetValue(setSlot.ItemId, out var inventoryItem);
                    var storedInOutfit = hasStoredOutfit && manager->IsSetSlotUnlocked((uint)storedSetIndex, setSlot.SlotIndex);

                    if (!storedInOutfit && !hasLooseItem && !hasInventoryItem)
                    {
                        continue;
                    }

                    if (storedInOutfit)
                    {
                        storedSlotIndexes.Add(setSlot.SlotIndex);
                        storedSetItemIds.Add(setSlot.ItemId);
                        continue;
                    }

                    if (hasLooseItem)
                    {
                        selectionItems.Add(looseItem!);
                        restoreItems.Add(looseItem!);
                        continue;
                    }

                    selectionItems.Add(inventoryItem!);
                    inventoryItemIds.Add(setSlot.ItemId);
                }

                if (selectionItems.Count == 0)
                {
                    continue;
                }

                var candidate = new OutfitCandidate(
                    setItemId,
                    name,
                    setItemIds,
                    setSlots.Select(slot => slot.SlotIndex).ToArray(),
                    selectionItems,
                    restoreItems,
                    inventoryItemIds.ToArray(),
                    storedSlotIndexes.ToArray(),
                    storedSetItemIds.ToArray(),
                    selectionItems.Any(item => item.Dyed));
                if (!this.HasSupportedSetConvertSource(candidate))
                {
                    continue;
                }

                if (!this.ShouldIncludeCandidate(candidate))
                {
                    continue;
                }

                rawCandidates.Add(candidate);
            }
        }

        var reservedItems = new HashSet<uint>();
        foreach (var candidate in rawCandidates
            .OrderByDescending(candidate => candidate.GlamourDresserSlotsSaved)
            .ThenByDescending(candidate => candidate.IsMerge)
            .ThenByDescending(candidate => candidate.SelectionItems.Count + candidate.StoredSetItemIds.Length)
            .ThenByDescending(candidate => candidate.SelectionItems.Count)
            .ThenBy(candidate => candidate.SelectionItems.Min(item => this.GetSetConvertSourceRank(item.ItemId, candidate.SetItemId)))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SetItemId))
        {
            var candidateReservedItems = candidate.RestoreItems.Select(item => item.ItemId).ToList();
            candidateReservedItems.AddRange(candidate.InventoryItemIds);
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
            if (this.candidates.Count == 0 && this.duplicateCandidates.Count == 0)
            {
                this.status = "No Glamour Dresser slots to clean up.";
            }
            else
            {
                this.status = this.GetCandidateSummary();
            }
        }
    }

    private void RebuildDuplicateCandidates(MirageManager* manager, Lumina.Excel.ExcelSheet<Item> itemSheet, HashSet<uint> outfitSetItemIds)
    {
        var duplicateGroups = new Dictionary<DuplicateItemKey, int>();
        for (var i = 0; i < this.lastPrismBoxItemIds.Length; i++)
        {
            var rawItemId = this.lastPrismBoxItemIds[i];
            var itemId = GetBaseItemId(rawItemId);
            if (itemId == 0 || IsHighQualityItem(rawItemId) || outfitSetItemIds.Contains(itemId))
            {
                continue;
            }

            var key = new DuplicateItemKey(itemId, manager->PrismBoxStain0Ids[i], manager->PrismBoxStain1Ids[i]);
            duplicateGroups.TryGetValue(key, out var count);
            duplicateGroups[key] = count + 1;
        }

        foreach (var group in duplicateGroups
            .Where(group => group.Value > 1)
            .OrderBy(group => this.GetDuplicateItemName(group.Key.ItemId, itemSheet), StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ItemId)
            .ThenBy(group => group.Key.Stain0Id)
            .ThenBy(group => group.Key.Stain1Id))
        {
            this.duplicateCandidates.Add(new DuplicateItemCandidate(
                group.Key,
                this.GetDuplicateItemName(group.Key.ItemId, itemSheet),
                group.Value - 1));
        }
    }

    private List<QueuedDuplicateItem> CreateDuplicateCleanupQueue()
    {
        var items = new List<QueuedDuplicateItem>();
        foreach (var candidate in this.duplicateCandidates)
        {
            for (var i = 0; i < candidate.DuplicatesToRestore; i++)
            {
                items.Add(new QueuedDuplicateItem(candidate.Key, candidate.Name));
            }
        }

        return items;
    }

    private string GetDuplicateItemName(uint itemId, Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        if (!itemSheet.TryGetRow(itemId, out var itemRow))
        {
            return $"item {itemId}";
        }

        var name = itemRow.Name.ToString();
        return string.IsNullOrWhiteSpace(name)
            ? $"item {itemId}"
            : name;
    }

    private string GetCandidateSummary()
    {
        var totalUpdates = this.candidates.Count;
        var totalDuplicatesToRestore = this.duplicateCandidates.Sum(candidate => candidate.DuplicatesToRestore);
        var totalSlotSavings = this.candidates.Where(candidate => candidate.GlamourDresserSlotsSaved > 0).Sum(candidate => candidate.GlamourDresserSlotsSaved);
        var slotUsingInventoryOutfitCount = this.candidates.Count(candidate => candidate.GlamourDresserSlotsSaved < 0 && candidate.IsNewOutfitUsingInventory);
        var duplicateSummary = totalDuplicatesToRestore > 0
            ? $" {totalDuplicatesToRestore} duplicate dresser item{(totalDuplicatesToRestore == 1 ? string.Empty : "s")} can be restored to inventory."
            : string.Empty;
        if (totalUpdates == 0)
        {
            return duplicateSummary.TrimStart();
        }

        if (totalSlotSavings > 0 && slotUsingInventoryOutfitCount > 0)
        {
            return $"{totalUpdates} outfit update{(totalUpdates == 1 ? string.Empty : "s")} available. {totalSlotSavings} Glamour Dresser slot{(totalSlotSavings == 1 ? string.Empty : "s")} can be freed; {slotUsingInventoryOutfitCount} new inventory outfit{(slotUsingInventoryOutfitCount == 1 ? string.Empty : "s")} will use a slot.{duplicateSummary}";
        }

        if (totalSlotSavings > 0)
        {
            return $"{totalUpdates} outfit update{(totalUpdates == 1 ? string.Empty : "s")} can free {totalSlotSavings} Glamour Dresser slot{(totalSlotSavings == 1 ? string.Empty : "s")}.{duplicateSummary}";
        }

        if (slotUsingInventoryOutfitCount > 0)
        {
            return $"{slotUsingInventoryOutfitCount} inventory outfit{(slotUsingInventoryOutfitCount == 1 ? string.Empty : "s")} can be created.{duplicateSummary}";
        }

        return $"{totalUpdates} outfit update{(totalUpdates == 1 ? string.Empty : "s")} available without using more Glamour Dresser slots.{duplicateSummary}";
    }

    private bool ShouldIncludeCandidate(OutfitCandidate candidate)
    {
        if (candidate.GlamourDresserSlotsSaved >= 0)
        {
            return true;
        }

        if (!candidate.IsNewOutfitUsingInventory)
        {
            return false;
        }

        return this.CurrentNewInventoryOutfitPolicy switch
        {
            NewInventoryOutfitPolicy.FullSetsOnly => candidate.IsCompleteOutfit,
            NewInventoryOutfitPolicy.PartialAndFullSets => true,
            _ => false
        };
    }

    private bool TryCreateCandidateItem(uint itemId, Dictionary<uint, PrismBoxCandidateItem> itemIndexes, MirageManager* manager, out CandidateItem? item)
    {
        item = null;
        if (itemId == 0 || !itemIndexes.TryGetValue(itemId, out var prismBoxItem))
        {
            return false;
        }

        var index = prismBoxItem.Index;
        var dyed = manager->PrismBoxStain0Ids[index] != 0 || manager->PrismBoxStain1Ids[index] != 0;
        item = new CandidateItem(itemId, dyed);
        return true;
    }

    private Dictionary<uint, CandidateItem> GetInventoryCandidateItems()
    {
        var items = new Dictionary<uint, CandidateItem>();
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return items;
        }

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
                if (inventorySlot == null)
                {
                    continue;
                }

                var rawItemId = inventorySlot->GetItemId();
                var itemId = GetBaseItemId(rawItemId);
                if (itemId == 0)
                {
                    continue;
                }

                if (IsHighQualityItem(rawItemId))
                {
                    continue;
                }

                if (items.ContainsKey(itemId))
                {
                    continue;
                }

                var dyed = inventorySlot->GetStain(0) != 0 || inventorySlot->GetStain(1) != 0;
                items[itemId] = new CandidateItem(itemId, dyed);
            }
        }

        return items;
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

    private int FindPrismBoxItemIndex(uint itemId, ItemQualityRequirement quality = ItemQualityRequirement.Normal)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            return -1;
        }

        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var rawItemId = manager->PrismBoxItemIds[i];
            if (GetBaseItemId(rawItemId) == itemId && MatchesQuality(rawItemId, quality))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindPrismBoxItemIndex(DuplicateItemKey key)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            return -1;
        }

        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            if (MatchesDuplicateKey(manager->PrismBoxItemIds[i], manager->PrismBoxStain0Ids[i], manager->PrismBoxStain1Ids[i], key))
            {
                return i;
            }
        }

        return -1;
    }

    private int CountPrismBoxItems(DuplicateItemKey key)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            if (MatchesDuplicateKey(manager->PrismBoxItemIds[i], manager->PrismBoxStain0Ids[i], manager->PrismBoxStain1Ids[i], key))
            {
                count++;
            }
        }

        return count;
    }

    private bool HasStartedRestoring(QueuedOutfit outfit)
    {
        return outfit.RestoredSlots.Count > 0 || outfit.NextRestoreIndex > 0 || this.waitingForRestoredItemId != null;
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
        this.FailQueue($"Need {neededPrisms} Glamour Prism{(neededPrisms == 1 ? string.Empty : "s")} to finish {remainingOutfits} outfit update{(remainingOutfits == 1 ? string.Empty : "s")}, but only {availablePrisms} available.");
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
        neededSlots = outfit.RestoreItems.Count;
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

    private int CountInventoryItem(uint itemId)
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
                if (inventorySlot != null && inventorySlot->GetItemId() == itemId)
                {
                    itemCount += (int)inventorySlot->GetQuantity();
                }
            }
        }

        return itemCount;
    }

    private int CountInventoryBagItems(DuplicateItemKey key)
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
                if (inventorySlot == null)
                {
                    continue;
                }

                if (MatchesDuplicateKey(inventorySlot->GetItemId(), inventorySlot->GetStain(0), inventorySlot->GetStain(1), key))
                {
                    itemCount++;
                }
            }
        }

        return itemCount;
    }

    private bool TryFindInventoryItem(uint itemId, out InventorySlot slot)
    {
        return this.TryFindInventoryItem(itemId, ItemQualityRequirement.Normal, out slot);
    }

    private bool TryFindInventoryBagItem(uint itemId, out InventorySlot slot)
    {
        return this.TryFindInventoryBagItem(itemId, ItemQualityRequirement.Normal, out slot);
    }

    private bool TryFindInventoryBagItem(CandidateItem item, out InventorySlot slot)
    {
        return this.TryFindInventoryBagItem(item.ItemId, out slot);
    }

    private bool TryFindInventoryItem(uint itemId, ItemQualityRequirement quality, out InventorySlot slot)
    {
        return this.TryFindInventoryItem(itemId, InventoryBagTypes, quality, out slot);
    }

    private bool TryFindInventoryBagItem(uint itemId, ItemQualityRequirement quality, out InventorySlot slot)
    {
        return this.TryFindInventoryItem(itemId, InventoryBagTypes, quality, out slot);
    }

    private bool TryFindInventoryItem(uint itemId, IReadOnlyList<InventoryType> inventoryTypes, out InventorySlot slot)
    {
        return this.TryFindInventoryItem(itemId, inventoryTypes, ItemQualityRequirement.Normal, out slot);
    }

    private bool TryFindInventoryItem(uint itemId, IReadOnlyList<InventoryType> inventoryTypes, ItemQualityRequirement quality, out InventorySlot slot)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            slot = default;
            return false;
        }

        foreach (var inventoryType in inventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                continue;
            }

            for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
            {
                var inventorySlot = container->GetInventorySlot(slotIndex);
                if (inventorySlot == null)
                {
                    continue;
                }

                var rawItemId = inventorySlot->GetItemId();
                if (GetBaseItemId(rawItemId) != itemId || !MatchesQuality(rawItemId, quality))
                {
                    continue;
                }

                slot = new InventorySlot(rawItemId, inventorySlot->GetInventoryType(), inventorySlot->GetSlot());
                return true;
            }
        }

        slot = default;
        return false;
    }

    private bool TryFindOutfitInventoryItem(QueuedOutfit outfit, uint itemId, out InventorySlot slot)
    {
        return this.TryFindOutfitInventoryItem(outfit, itemId, ItemQualityRequirement.Normal, out slot);
    }

    private bool TryFindOutfitInventoryItem(QueuedOutfit outfit, uint itemId, ItemQualityRequirement quality, out InventorySlot slot)
    {
        foreach (var restoredSlot in outfit.RestoredSlots)
        {
            if (!this.TryGetInventoryRawItemId(restoredSlot, out var rawItemId)
                || GetBaseItemId(rawItemId) != itemId
                || !MatchesQuality(rawItemId, quality))
            {
                continue;
            }

            slot = new InventorySlot(rawItemId, restoredSlot.InventoryType, restoredSlot.Slot);
            return true;
        }

        return this.TryFindInventoryItem(itemId, quality, out slot);
    }

    private bool TryGetInventoryItemPointer(InventorySlot slot, out InventoryItem* inventoryItem)
    {
        inventoryItem = null;
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

        inventoryItem = container->GetInventorySlot((int)slot.Slot);
        return inventoryItem != null && inventoryItem->GetItemId() != 0;
    }

    private bool TryGetInventoryRawItemId(InventorySlot slot, out uint rawItemId)
    {
        if (!this.TryGetInventoryItemPointer(slot, out var inventoryItem))
        {
            rawItemId = 0;
            return false;
        }

        rawItemId = inventoryItem->GetItemId();
        return true;
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
        return inventorySlot != null && inventorySlot->GetItemId() != slot.RawItemId;
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

    private static bool MatchesQuality(uint itemId, ItemQualityRequirement quality)
    {
        return quality == ItemQualityRequirement.Normal && !IsHighQualityItem(itemId);
    }

    private static bool MatchesDuplicateKey(uint rawItemId, byte stain0Id, byte stain1Id, DuplicateItemKey key)
    {
        return GetBaseItemId(rawItemId) == key.ItemId
            && !IsHighQualityItem(rawItemId)
            && stain0Id == key.Stain0Id
            && stain1Id == key.Stain1Id;
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
        return this.TryGetSetConvertAddon(out _);
    }

    private bool TryGetSetConvertAddon(out AtkUnitBase* addon)
    {
        if (this.TryGetVisibleAddon(SetConvertAddonName, out addon))
        {
            return true;
        }

        return this.TryGetVisibleAddon(SetConvertAlternateAddonName, out addon);
    }

    private bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon)
    {
        var handle = this.Services.GameGui.GetAddonByName(addonName, 1);
        if (handle.IsNull
            || handle.Address == IntPtr.Zero
            || !handle.IsReady
            || !handle.IsVisible)
        {
            addon = null;
            return false;
        }

        addon = (AtkUnitBase*)handle.Address;
        return true;
    }

    private bool TryClickButton(AtkUnitBase* addon, uint buttonId, System.Action? beforeClick = null)
    {
        if (addon == null)
        {
            return false;
        }

        var button = addon->GetComponentButtonById(buttonId);
        return this.TryClickButton(addon, button, beforeClick);
    }

    private bool TryClickButton(AtkUnitBase* addon, AtkComponentButton* button, System.Action? beforeClick = null)
    {
        if (addon == null || button == null)
        {
            return false;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null
            || button->AtkResNode == null
            || !button->AtkResNode->IsVisible()
            || !button->IsEnabled)
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
            QueueStep.RestoringDuplicateItem => "restoring duplicate items",
            QueueStep.WaitingForDuplicateRestore => "waiting for duplicate item restore",
            QueueStep.RestoringItems => "restoring outfit pieces",
            QueueStep.WaitingForRestore => "waiting for inventory updates",
            QueueStep.OpeningSetConvert => "opening outfit creation",
            QueueStep.WaitingForSetConvertOpen => "waiting for outfit creation to open",
            QueueStep.FillingSetConvert => "selecting outfit pieces",
            QueueStep.ValidatingSetConvert => "checking selected outfit pieces",
            QueueStep.StoringOutfit => "storing the outfit",
            QueueStep.ConfirmingStore => "confirming storage",
            QueueStep.WaitingForStore => "waiting for the outfit to be stored",
            _ => "updating outfits"
        };
    }

    private static string FormatNewInventoryOutfitPolicy(NewInventoryOutfitPolicy mode)
    {
        return mode switch
        {
            NewInventoryOutfitPolicy.FullSetsOnly => "Full sets only",
            NewInventoryOutfitPolicy.PartialAndFullSets => "Partial and full sets",
            _ => "Off"
        };
    }

    private enum NewInventoryOutfitPolicy
    {
        Off,
        FullSetsOnly,
        PartialAndFullSets
    }

    private enum ItemQualityRequirement
    {
        Normal
    }

    private enum QueueStep
    {
        Idle,
        WaitingForDyedConfirmation,
        RestoringDuplicateItem,
        WaitingForDuplicateRestore,
        RestoringItems,
        WaitingForRestore,
        OpeningSetConvert,
        WaitingForSetConvertOpen,
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
            int[] setSlotIndexes,
            List<CandidateItem> selectionItems,
            List<CandidateItem> restoreItems,
            uint[] inventoryItemIds,
            int[] storedSlotIndexes,
            uint[] storedSetItemIds,
            bool requiresConfirmation)
        {
            this.SetItemId = setItemId;
            this.Name = name;
            this.SetItemIds = setItemIds;
            this.SetSlotIndexes = setSlotIndexes;
            this.SelectionItems = selectionItems;
            this.RestoreItems = restoreItems;
            this.InventoryItemIds = inventoryItemIds;
            this.StoredSlotIndexes = storedSlotIndexes;
            this.StoredSetItemIds = storedSetItemIds;
            this.RequiresConfirmation = requiresConfirmation;
        }

        public uint SetItemId { get; }
        public string Name { get; }
        public uint[] SetItemIds { get; }
        public int[] SetSlotIndexes { get; }
        public List<CandidateItem> SelectionItems { get; }
        public List<CandidateItem> RestoreItems { get; }
        public uint[] InventoryItemIds { get; }
        public int[] StoredSlotIndexes { get; }
        public uint[] StoredSetItemIds { get; }
        public bool RequiresConfirmation { get; }
        public int GlamourPrismCost => this.SelectionItems.Count;
        public int GlamourDresserSlotsSaved => this.RestoreItems.Count - (this.IsMerge ? 0 : 1);
        public bool IsMerge => this.StoredSlotIndexes.Length > 0;
        public bool IsNewOutfitUsingInventory => !this.IsMerge && this.InventoryItemIds.Length > 0;
        public bool IsCompleteOutfit => this.SelectionItems.Count == this.SetItemIds.Length;
    }

    private sealed class QueuedOutfit
    {
        public QueuedOutfit(OutfitCandidate candidate)
        {
            this.SetItemId = candidate.SetItemId;
            this.Name = candidate.Name;
            this.SetItemIds = candidate.SetItemIds;
            this.SetSlotIndexes = candidate.SetSlotIndexes;
            this.SelectionItems = candidate.SelectionItems;
            this.RestoreItems = candidate.RestoreItems;
            this.InventoryItemIds = candidate.InventoryItemIds;
            this.StoredSlotIndexes = candidate.StoredSlotIndexes;
            this.StoredSetItemIds = candidate.StoredSetItemIds;
            this.RequiresConfirmation = candidate.RequiresConfirmation;
            this.GlamourPrismCost = candidate.GlamourPrismCost;
        }

        public uint SetItemId { get; }
        public string Name { get; }
        public uint[] SetItemIds { get; }
        public int[] SetSlotIndexes { get; }
        public List<CandidateItem> SelectionItems { get; }
        public List<CandidateItem> RestoreItems { get; }
        public uint[] InventoryItemIds { get; }
        public int[] StoredSlotIndexes { get; }
        public uint[] StoredSetItemIds { get; }
        public bool RequiresConfirmation { get; }
        public int GlamourPrismCost { get; }
        public bool IsMerge => this.StoredSlotIndexes.Length > 0;
        public bool IsNativeAddendum => this.IsMerge || this.NativeAddendumAccepted;
        public List<InventorySlot> RestoredSlots { get; } = [];
        public HashSet<uint> SetConvertSourceItemIdsTried { get; } = [];
        public uint? CurrentSetConvertSourceItemId { get; set; }
        public int NextRestoreIndex { get; set; }
        public bool StoredSetConvertOpenAttempted { get; set; }
        public bool SetConvertOutfitSwitchAttempted { get; set; }
        public int AddendumPromptAttempts { get; set; }
        public bool NativeAddendumAccepted { get; set; }
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

    private sealed class DuplicateItemCandidate
    {
        public DuplicateItemCandidate(DuplicateItemKey key, string name, int duplicatesToRestore)
        {
            this.Key = key;
            this.Name = name;
            this.DuplicatesToRestore = duplicatesToRestore;
        }

        public DuplicateItemKey Key { get; }
        public string Name { get; }
        public int DuplicatesToRestore { get; }
    }

    private sealed class QueuedDuplicateItem
    {
        public QueuedDuplicateItem(DuplicateItemKey key, string name)
        {
            this.Key = key;
            this.Name = name;
        }

        public DuplicateItemKey Key { get; }
        public string Name { get; }
    }

    private readonly struct DuplicateItemKey : IEquatable<DuplicateItemKey>
    {
        public DuplicateItemKey(uint itemId, byte stain0Id, byte stain1Id)
        {
            this.ItemId = itemId;
            this.Stain0Id = stain0Id;
            this.Stain1Id = stain1Id;
        }

        public uint ItemId { get; }
        public byte Stain0Id { get; }
        public byte Stain1Id { get; }

        public bool Equals(DuplicateItemKey other)
        {
            return this.ItemId == other.ItemId
                && this.Stain0Id == other.Stain0Id
                && this.Stain1Id == other.Stain1Id;
        }

        public override bool Equals(object? obj)
        {
            return obj is DuplicateItemKey other && this.Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(this.ItemId, this.Stain0Id, this.Stain1Id);
        }
    }

    private readonly struct PrismBoxCandidateItem
    {
        public PrismBoxCandidateItem(int index)
        {
            this.Index = index;
        }

        public int Index { get; }
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
        public InventorySlot(uint rawItemId, InventoryType inventoryType, uint slot)
        {
            this.RawItemId = rawItemId;
            this.InventoryType = inventoryType;
            this.Slot = slot;
        }

        public uint RawItemId { get; }
        public InventoryType InventoryType { get; }
        public uint Slot { get; }
    }

    private readonly record struct SetConvertSourceKey(uint ItemId, uint SetItemId);

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
