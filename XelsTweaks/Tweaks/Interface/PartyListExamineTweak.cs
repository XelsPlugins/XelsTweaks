using System;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace XelsTweaks.Tweaks.Interface;

internal sealed class PartyListExamineTweak : TweakBase
{
    public const string TweakId = "interface.partyListExamine";

    private const string PartyListAddonName = "_PartyList";
    private static readonly SeString ExamineLabel = "Examine";

    public PartyListExamineTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Examine Party Members";
    public override string Description => "Adds Examine to right-click menus for party members.";
    public override TweakCategory Category => TweakCategory.Interface;

    protected override void OnEnable()
    {
        this.Services.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    protected override void OnDisable()
    {
        this.Services.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default
            || args.Target is not MenuTargetDefault target
            || !this.TryGetPlayerTarget(target, out var player, out var isPartyMember)
            || (!IsPartyListAddon(args.AddonName) && !isPartyMember))
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = ExamineLabel,
            PrefixChar = 'X',
            OnClicked = _ => this.Examine(player),
        });
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

    private void Examine(IGameObject player)
    {
        var previousTarget = this.Services.TargetManager.Target;

        try
        {
            this.Services.TargetManager.Target = player;
            this.Services.Framework.RunOnTick(
                () => this.ExecuteCheckCommand(player, previousTarget),
                default,
                1,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            this.Services.Log.Warning(ex, "Failed to examine party list target {Target}", player.Name.TextValue);
        }
    }

    private unsafe void ExecuteCheckCommand(IGameObject player, IGameObject? previousTarget)
    {
        try
        {
            using var command = new Utf8String("/check <t>");
            RaptureShellModule.Instance()->ExecuteCommandInner(&command, UIModule.Instance());

            this.Services.Framework.RunOnTick(
                () => this.RestoreTarget(player, previousTarget),
                default,
                4,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            this.Services.Log.Warning(ex, "Failed to run examine command for party list target {Target}", player.Name.TextValue);
        }
    }

    private void RestoreTarget(IGameObject player, IGameObject? previousTarget)
    {
        if (this.Services.TargetManager.Target?.GameObjectId == player.GameObjectId)
        {
            this.Services.TargetManager.Target = previousTarget;
        }
    }
}
