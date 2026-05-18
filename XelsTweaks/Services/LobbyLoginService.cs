using System;
using System.Linq;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XelsTweaks.Services;

internal sealed unsafe class LobbyLoginService
{
    private const string TitleMenuAddonName = "_TitleMenu";
    private const string TitleDataCenterAddonName = "TitleDCWorldMap";
    private const string TitleConnectAddonName = "TitleConnect";
    private const string CharaSelectAddonName = "_CharaSelectListMenu";
    private const string CharaSelectWorldAddonName = "_CharaSelectWorldServer";
    private const string SelectStringAddonName = "SelectString";
    private const string SelectYesNoAddonName = "SelectYesno";
    private const int TitleStartButtonId = 4;
    private const int TitleDataCenterButtonId = 5;
    private const int CharaSelectWorldButtonId = 4;
    private const int LoginCharacterEventBase = 5;
    private const int SelectWorldCallbackId = 25;
    private const int SelectTitleDataCenterCallbackId = 17;
    private const int MaxServiceAccountIndex = 7;

    private static readonly TimeSpan LoginConfirmationRetryDelay = TimeSpan.FromSeconds(8);

    private readonly DalamudServices services;
    private DateTimeOffset lastCharacterClickAt;
    private string? lastClickedCharacterKey;

    public LobbyLoginService(DalamudServices services)
    {
        this.services = services;
    }

    public bool TryStepLogin(
        ulong contentId,
        string characterName,
        string homeWorld,
        ushort homeWorldId,
        int serviceAccountIndex,
        out bool completed,
        out string status,
        out string? error)
    {
        completed = false;
        error = null;

        if (this.services.ClientState.IsLoggedIn)
        {
            completed = true;
            status = "Logged in.";
            return true;
        }

        if (this.services.Condition.Any())
        {
            status = "Waiting for the lobby to become idle.";
            return true;
        }

        var characterKey = $"{contentId}:{characterName}@{homeWorld}";
        if (!string.Equals(this.lastClickedCharacterKey, characterKey, StringComparison.Ordinal))
        {
            this.lastClickedCharacterKey = null;
            this.lastCharacterClickAt = DateTimeOffset.MinValue;
        }

        if (this.TryHandleLoginPrompt(characterName, homeWorld, out var confirmedLogin, out status, out error))
        {
            completed = confirmedLogin;
            this.lastClickedCharacterKey = null;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(this.lastClickedCharacterKey, characterKey, StringComparison.Ordinal)
            && now - this.lastCharacterClickAt < LoginConfirmationRetryDelay)
        {
            status = $"Waiting for the login confirmation for {characterName} @ {homeWorld}.";
            return true;
        }

        if (this.TryHandleCharacterSelect(contentId, characterName, homeWorld, homeWorldId, out status, out error))
        {
            return true;
        }

        if (this.TryHandleTitle(characterName, homeWorld, homeWorldId, serviceAccountIndex, out status, out error))
        {
            return true;
        }

        status = "Waiting for the title screen or character select.";
        return true;
    }

    private bool TryHandleCharacterSelect(
        ulong contentId,
        string characterName,
        string homeWorld,
        ushort homeWorldId,
        out string status,
        out string? error)
    {
        error = null;

        if (!this.TryGetAddon(CharaSelectAddonName, out var charaSelect))
        {
            status = string.Empty;
            return false;
        }

        var lobby = AgentLobby.Instance();
        if (lobby == null)
        {
            status = "Waiting for character data.";
            return true;
        }

        if (lobby->TemporaryLocked)
        {
            status = "Waiting for character select to finish loading.";
            return true;
        }

        if (this.TryFindCharacter(contentId, characterName, homeWorld, homeWorldId, out var characterIndex, out var canLogin))
        {
            if (!canLogin)
            {
                error = $"{characterName} @ {homeWorld} cannot be logged in normally right now.";
                status = error;
                return true;
            }

            this.ClickCharacter(charaSelect, characterIndex);
            this.lastClickedCharacterKey = $"{contentId}:{characterName}@{homeWorld}";
            this.lastCharacterClickAt = DateTimeOffset.UtcNow;
            status = $"Selected {characterName} @ {homeWorld}; waiting for the confirmation prompt.";
            return true;
        }

        if (!this.TryGetAddon(CharaSelectWorldAddonName, out var worldServer))
        {
            if (this.TryClickButton(charaSelect, CharaSelectWorldButtonId, out error))
            {
                status = "Opening the world selector.";
                return true;
            }

            status = error ?? "Waiting for the world selector.";
            return true;
        }

        if (this.TrySelectWorld(worldServer, homeWorld, out error))
        {
            status = $"Selecting {homeWorld}.";
            return true;
        }

        status = $"Waiting for {homeWorld} to be available on this data center.";
        return true;
    }

    private bool TryHandleTitle(
        string characterName,
        string homeWorld,
        ushort homeWorldId,
        int serviceAccountIndex,
        out string status,
        out string? error)
    {
        error = null;

        if (this.TryGetAddon(TitleConnectAddonName, out _))
        {
            status = "Waiting for the title connection to finish.";
            return true;
        }

        if (this.TryGetAddon(SelectStringAddonName, out var selectString))
        {
            var clampedServiceAccountIndex = Math.Clamp(serviceAccountIndex, 0, MaxServiceAccountIndex);
            this.FireCallback(selectString, true, this.CreateAtkInt(clampedServiceAccountIndex));
            status = $"Selecting service account {clampedServiceAccountIndex + 1}.";
            return true;
        }

        if (this.TryGetAddon(TitleDataCenterAddonName, out var dataCenterMap))
        {
            if (this.TryResolveDataCenterId(homeWorld, homeWorldId, out var dataCenterId))
            {
                this.FireCallback(dataCenterMap, true, this.CreateAtkInt(SelectTitleDataCenterCallbackId), this.CreateAtkInt((int)dataCenterId));
                status = $"Selecting the data center for {characterName} @ {homeWorld}.";
                return true;
            }

            status = $"Could not resolve the data center for {homeWorld}; waiting for character select.";
            return true;
        }

        if (!this.TryGetAddon(TitleMenuAddonName, out var titleMenu))
        {
            status = string.Empty;
            return false;
        }

        if (!IsTitleMenuReady(titleMenu))
        {
            status = "Waiting for the title menu to become ready.";
            return true;
        }

        if (this.TryResolveDataCenterId(homeWorld, homeWorldId, out _))
        {
            if (this.TryClickButton(titleMenu, TitleDataCenterButtonId, out error))
            {
                status = $"Opening data center selection for {characterName} @ {homeWorld}.";
                return true;
            }

            status = error ?? "Waiting for the data center button.";
            return true;
        }

        if (this.TryClickButton(titleMenu, TitleStartButtonId, out error))
        {
            status = "Opening character select.";
            return true;
        }

        status = error ?? "Waiting for the title menu.";
        return true;
    }

    private bool TryFindCharacter(
        ulong contentId,
        string characterName,
        string homeWorld,
        ushort homeWorldId,
        out int characterIndex,
        out bool canLogin)
    {
        characterIndex = -1;
        canLogin = false;

        var lobby = AgentLobby.Instance();
        if (lobby == null)
        {
            return false;
        }

        var entries = lobby->LobbyData.CharaSelectEntries.ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].Value;
            if (entry == null)
            {
                continue;
            }

            var matchesContentId = contentId != 0 && entry->ContentId == contentId;
            var matchesNameAndWorld = entry->NameString.Equals(characterName, StringComparison.OrdinalIgnoreCase)
                && (homeWorldId != 0
                    ? entry->HomeWorldId == homeWorldId
                    : entry->HomeWorldNameString.Equals(homeWorld, StringComparison.OrdinalIgnoreCase));

            if (!matchesContentId && !matchesNameAndWorld)
            {
                continue;
            }

            characterIndex = i;
            canLogin = CanLoginNormally(entry->LoginFlags);
            return true;
        }

        return false;
    }

    private bool TryHandleLoginPrompt(
        string characterName,
        string homeWorld,
        out bool confirmedLogin,
        out string status,
        out string? error)
    {
        confirmedLogin = false;
        status = string.Empty;
        error = null;

        if (!this.TryGetAddon(SelectYesNoAddonName, out var addon))
        {
            return false;
        }

        var prompt = ReadSelectYesNoPrompt(addon);
        if (!IsLoginPrompt(prompt))
        {
            status = "Waiting for another confirmation dialog to close.";
            return true;
        }

        var selectYesNo = (AddonSelectYesno*)addon;
        if (selectYesNo->YesButton == null)
        {
            error = "The login confirmation is missing its yes button.";
            status = error;
            return true;
        }

        if (!this.TryClickButton(addon, selectYesNo->YesButton, out error))
        {
            status = error ?? "Waiting for the login confirmation button.";
            return true;
        }

        confirmedLogin = true;
        status = $"Confirmed login for {characterName} @ {homeWorld}.";
        return true;
    }

    private bool TrySelectWorld(AtkUnitBase* worldServer, string homeWorld, out string? error)
    {
        error = null;

        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule == null)
        {
            error = "The world list is still loading.";
            return false;
        }

        var stringArray = raptureAtkModule->AtkArrayDataHolder.StringArrays[1];
        if (stringArray == null)
        {
            error = "The world list is still loading.";
            return false;
        }

        for (var i = 0; i < 16; i++)
        {
            var worldNamePointer = stringArray->StringArray[i];
            if (worldNamePointer.Value == null)
            {
                break;
            }

            var worldName = MemoryHelper.ReadStringNullTerminated((nint)worldNamePointer.Value).Trim();
            if (string.IsNullOrWhiteSpace(worldName))
            {
                break;
            }

            if (!worldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            this.FireCallback(worldServer, true, this.CreateAtkInt(SelectWorldCallbackId), this.CreateAtkInt(0), this.CreateAtkInt(i));
            return true;
        }

        return false;
    }

    private bool TryResolveDataCenterId(string homeWorld, ushort homeWorldId, out uint dataCenterId)
    {
        dataCenterId = 0;

        var worlds = this.services.DataManager.GetExcelSheet<World>();
        if (homeWorldId != 0 && worlds.TryGetRow(homeWorldId, out var worldById))
        {
            dataCenterId = worldById.DataCenter.RowId;
            return dataCenterId != 0;
        }

        var world = worlds.FirstOrDefault(row => row.Name.ToString().Equals(homeWorld, StringComparison.OrdinalIgnoreCase));
        dataCenterId = world.DataCenter.RowId;
        return dataCenterId != 0;
    }

    private bool TryGetAddon(string addonName, out AtkUnitBase* addon)
    {
        var handle = this.services.GameGui.GetAddonByName(addonName, 1);
        if (handle.IsNull || handle.Address == IntPtr.Zero || !handle.IsReady || !handle.IsVisible)
        {
            addon = null;
            return false;
        }

        addon = (AtkUnitBase*)handle.Address;
        return true;
    }

    private bool TryClickButton(AtkUnitBase* addon, uint buttonId, out string? error)
    {
        error = null;

        var button = addon->GetComponentButtonById(buttonId);
        if (button == null)
        {
            error = $"Button {buttonId} is unavailable.";
            return false;
        }

        return this.TryClickButton(addon, button, out error);
    }

    private bool TryClickButton(AtkUnitBase* addon, AtkComponentButton* button, out string? error)
    {
        error = null;

        if (!button->IsEnabled)
        {
            error = "The button is disabled.";
            return false;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null || !ownerNode->AtkResNode.IsVisible())
        {
            error = "The button is not visible.";
            return false;
        }

        var evt = (AtkEvent*)ownerNode->AtkResNode.AtkEventManager.Event;
        if (evt == null)
        {
            error = "The button click event is unavailable.";
            return false;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    private void ClickCharacter(AtkUnitBase* charaSelect, int characterIndex)
    {
        var eventIndex = LoginCharacterEventBase + characterIndex;
        var evt = CreateEvent(charaSelect, (byte)eventIndex);
        var data = new AtkEventData();
        WriteByte(ref data, 6, 0);

        charaSelect->ReceiveEvent(AtkEventType.MouseClick, eventIndex, &evt, &data);
    }

    private void FireCallback(AtkUnitBase* addon, bool updateState, params AtkValue[] values)
    {
        fixed (AtkValue* valuesPointer = values)
        {
            addon->FireCallback((uint)values.Length, valuesPointer, updateState);
        }
    }

    private AtkValue CreateAtkInt(int value)
    {
        return new AtkValue
        {
            Type = AtkValueType.Int,
            Int = value
        };
    }

    private static AtkEvent CreateEvent(AtkUnitBase* addon, byte stateFlags)
    {
        return new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &AtkStage.Instance()->AtkEventTarget,
            State = new AtkEventState
            {
                StateFlags = (AtkEventStateFlags)stateFlags
            }
        };
    }

    private static void WriteByte(ref AtkEventData data, int offset, byte value)
    {
        fixed (AtkEventData* dataPointer = &data)
        {
            *(byte*)((nint)dataPointer + offset) = value;
        }
    }

    private static bool IsTitleMenuReady(AtkUnitBase* titleMenu)
    {
        return titleMenu->UldManager.NodeListCount > 3
            && titleMenu->UldManager.NodeList[3]->Color.A == 0xFF;
    }

    private static bool CanLoginNormally(CharaSelectCharacterEntryLoginFlags flags)
    {
        return !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.Locked)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.NameChangeRequired)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.MissingExVersionForLogin)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.Unk32)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.DCTraveling);
    }

    private static string ReadSelectYesNoPrompt(AtkUnitBase* addon)
    {
        if (addon->AtkValues != null && addon->AtkValuesCount > 0)
        {
            var value = addon->AtkValues[0];
            return value.Type switch
            {
                AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString => value.String.ToString(),
                AtkValueType.WideString => value.WideString == null ? string.Empty : new string(value.WideString),
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    private static bool IsLoginPrompt(string prompt)
    {
        return prompt.Contains("Log in with", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Logging in with", StringComparison.OrdinalIgnoreCase);
    }
}
