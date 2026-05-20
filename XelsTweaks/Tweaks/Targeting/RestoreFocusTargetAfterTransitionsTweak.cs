using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace XelsTweaks.Tweaks.Targeting;

internal sealed class RestoreFocusTargetAfterTransitionsTweak : TweakBase
{
    public const string TweakId = "targeting.restoreFocusTargetAfterTransitions";

    private const string EnablePartyMenuOptionKey = "enablePartyMenu";
    private const string PartyListAddonName = "_PartyList";
    private const uint InvalidEntityId = 0xE0000000;
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromSeconds(120);
    private static readonly SeString SetFocusTargetMenuLabel = "Set Focus Target";
    private static readonly IReadOnlyList<TweakOptionDefinition> CommandOptions =
    [
        TweakOptionDefinition.Bool(
            EnablePartyMenuOptionKey,
            "Party menu item",
            "Adds a party member context menu item for setting your focus target.",
            true,
            "Context menu")
    ];

    private uint currentTerritoryType;
    private uint currentInstance;
    private FocusTargetSnapshot? lastFocusTarget;
    private FocusTargetSnapshot? pendingRestore;
    private DateTimeOffset pendingRestoreUntil;

    public RestoreFocusTargetAfterTransitionsTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Restore Focus Target After Transitions";
    public override string Description => "Attempts to restore your focus target when it reappears in the same instance.";
    public override TweakCategory Category => TweakCategory.Targeting;
    public override bool DrawConfigWhenDisabled => true;
    public override IReadOnlyList<TweakOptionDefinition> Options => CommandOptions;

    public override bool DrawConfig()
    {
        var enablePartyMenu = this.IsPartyMenuOptionEnabled();
        if (!ImGui.Checkbox("Add Set Focus Target to party member menus", ref enablePartyMenu))
        {
            return false;
        }

        this.SetBool(EnablePartyMenuOptionKey, enablePartyMenu);
        return true;
    }

    protected override void OnEnable()
    {
        this.currentTerritoryType = 0;
        this.currentInstance = 0;
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    protected override void OnDisable()
    {
        this.Services.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.ClearState();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!this.IsPartyMenuOptionEnabled()
            || args.MenuType != ContextMenuType.Default
            || args.Target is not MenuTargetDefault target
            || !this.TryGetPlayerTarget(target, out var player, out var isPartyMember)
            || (!IsPartyListAddon(args.AddonName) && !isPartyMember))
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = SetFocusTargetMenuLabel,
            PrefixChar = 'F',
            OnClicked = _ => this.SetFocusTarget(player),
        });
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.Services.ClientState.IsLoggedIn)
        {
            this.ClearState();
            this.currentTerritoryType = 0;
            this.currentInstance = 0;
            return;
        }

        if (this.HasLocationChanged())
        {
            this.currentTerritoryType = this.Services.ClientState.TerritoryType;
            this.currentInstance = this.Services.ClientState.Instance;
            this.ClearState();
            this.CaptureCurrentFocusTarget();
            return;
        }

        var focusTarget = this.Services.TargetManager.FocusTarget;
        if (focusTarget != null)
        {
            this.lastFocusTarget = this.CreateSnapshot(focusTarget);
            this.pendingRestore = null;
            return;
        }

        if (this.TryCompletePendingRestore())
        {
            return;
        }

        if (this.lastFocusTarget == null)
        {
            return;
        }

        if (this.TryFindRestorableTarget(this.lastFocusTarget, out _))
        {
            this.lastFocusTarget = null;
            return;
        }

        this.pendingRestore = this.lastFocusTarget;
        this.pendingRestoreUntil = DateTimeOffset.UtcNow + RestoreWindow;
        this.Services.Log.Debug(
            "Focus target {TargetName} disappeared in territory {TerritoryType}, instance {Instance}; watching for it to return.",
            this.pendingRestore.Name,
            this.pendingRestore.TerritoryType,
            this.pendingRestore.Instance);
    }

    private bool TryCompletePendingRestore()
    {
        if (this.pendingRestore == null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow > this.pendingRestoreUntil)
        {
            this.lastFocusTarget = null;
            this.pendingRestore = null;
            return true;
        }

        if (!this.TryFindRestorableTarget(this.pendingRestore, out var target))
        {
            return true;
        }

        this.Services.TargetManager.FocusTarget = target;
        this.lastFocusTarget = this.CreateSnapshot(target);
        this.pendingRestore = null;
        this.Services.Log.Debug("Restored focus target {TargetName}.", target.Name.TextValue);
        return true;
    }

    private void CaptureCurrentFocusTarget()
    {
        var focusTarget = this.Services.TargetManager.FocusTarget;
        this.lastFocusTarget = focusTarget != null ? this.CreateSnapshot(focusTarget) : null;
        this.pendingRestore = null;
    }

    private bool HasLocationChanged()
    {
        return this.currentTerritoryType != this.Services.ClientState.TerritoryType
            || this.currentInstance != this.Services.ClientState.Instance;
    }

    private void ClearState()
    {
        this.lastFocusTarget = null;
        this.pendingRestore = null;
        this.pendingRestoreUntil = DateTimeOffset.MinValue;
    }

    private void SetFocusTarget(IGameObject player)
    {
        try
        {
            this.Services.TargetManager.FocusTarget = player;
            this.lastFocusTarget = this.CreateSnapshot(player);
            this.pendingRestore = null;
        }
        catch (Exception ex)
        {
            this.Services.Log.Warning(ex, "Failed to set focus target {Target}", player.Name.TextValue);
        }
    }

    private static bool IsPartyListAddon(string? addonName)
    {
        return !string.IsNullOrEmpty(addonName)
            && (string.Equals(addonName, PartyListAddonName, StringComparison.Ordinal)
                || addonName.Contains("Party", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetPlayerTarget(MenuTargetDefault target, out IGameObject player, out bool isPartyMember)
    {
        if (target.TargetObject is { ObjectKind: ObjectKind.Pc } targetObject)
        {
            isPartyMember = this.IsPartyMember(targetObject);
            player = targetObject;
            return true;
        }

        if (target.TargetObjectId != 0)
        {
            var objectById = this.Services.ObjectTable.SearchById(target.TargetObjectId);
            if (objectById is { ObjectKind: ObjectKind.Pc })
            {
                isPartyMember = this.IsPartyMember(objectById);
                player = objectById;
                return true;
            }
        }

        var contentId = target.TargetContentId != 0 ? target.TargetContentId : target.TargetCharacter?.ContentId ?? 0;
        if (contentId != 0 && this.TryGetPartyMemberObject(contentId, out var partyMemberObject))
        {
            isPartyMember = true;
            player = partyMemberObject;
            return true;
        }

        var targetName = target.TargetName;
        if (!string.IsNullOrWhiteSpace(targetName) && this.TryGetPartyMemberObject(targetName, out partyMemberObject))
        {
            isPartyMember = true;
            player = partyMemberObject;
            return true;
        }

        isPartyMember = false;
        player = null!;
        return false;
    }

    private bool IsPartyMember(IGameObject gameObject)
    {
        return this.Services.PartyList.Any(member => member.GameObject?.GameObjectId == gameObject.GameObjectId);
    }

    private bool TryGetPartyMemberObject(ulong contentId, out IGameObject player)
    {
        var member = this.Services.PartyList.FirstOrDefault(member => member.ContentId == contentId);
        if (member?.GameObject is { ObjectKind: ObjectKind.Pc } gameObject)
        {
            player = gameObject;
            return true;
        }

        player = null!;
        return false;
    }

    private bool TryGetPartyMemberObject(string name, out IGameObject player)
    {
        var member = this.Services.PartyList.FirstOrDefault(member => string.Equals(member.Name.TextValue, name, StringComparison.Ordinal));
        if (member?.GameObject is { ObjectKind: ObjectKind.Pc } gameObject)
        {
            player = gameObject;
            return true;
        }

        player = null!;
        return false;
    }

    private bool IsPartyMenuOptionEnabled()
    {
        return this.GetBool(EnablePartyMenuOptionKey, true);
    }

    private bool TryFindRestorableTarget(FocusTargetSnapshot snapshot, out IGameObject target)
    {
        target = null!;

        if (snapshot.TerritoryType != this.Services.ClientState.TerritoryType
            || snapshot.Instance != this.Services.ClientState.Instance)
        {
            return false;
        }

        var objectById = this.Services.ObjectTable.SearchById(snapshot.GameObjectId);
        if (objectById != null && IsRestorableTarget(objectById, snapshot))
        {
            target = objectById;
            return true;
        }

        if (snapshot.EntityId != 0 && snapshot.EntityId != InvalidEntityId)
        {
            foreach (var gameObject in this.Services.ObjectTable)
            {
                if (IsRestorableTarget(gameObject, snapshot) && gameObject.EntityId == snapshot.EntityId)
                {
                    target = gameObject;
                    return true;
                }
            }
        }

        return this.TryFindUniqueBaseNameMatch(snapshot, out target);
    }

    private bool TryFindUniqueBaseNameMatch(FocusTargetSnapshot snapshot, out IGameObject target)
    {
        target = null!;
        if (snapshot.BaseId == 0 || string.IsNullOrEmpty(snapshot.Name))
        {
            return false;
        }

        foreach (var gameObject in this.Services.ObjectTable)
        {
            if (!IsRestorableTarget(gameObject, snapshot)
                || gameObject.BaseId != snapshot.BaseId
                || !string.Equals(gameObject.Name.TextValue, snapshot.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (target != null)
            {
                target = null!;
                return false;
            }

            target = gameObject;
        }

        return target != null;
    }

    private static bool IsRestorableTarget(IGameObject? gameObject, FocusTargetSnapshot snapshot)
    {
        return gameObject != null
            && gameObject.ObjectKind == snapshot.ObjectKind
            && gameObject.IsTargetable
            && !gameObject.IsDead;
    }

    private FocusTargetSnapshot CreateSnapshot(IGameObject gameObject)
    {
        return new FocusTargetSnapshot(
            gameObject.GameObjectId,
            gameObject.EntityId,
            gameObject.BaseId,
            gameObject.Name.TextValue,
            gameObject.ObjectKind,
            this.Services.ClientState.TerritoryType,
            this.Services.ClientState.Instance);
    }

    private sealed record FocusTargetSnapshot(
        ulong GameObjectId,
        uint EntityId,
        uint BaseId,
        string Name,
        ObjectKind ObjectKind,
        uint TerritoryType,
        uint Instance);
}
