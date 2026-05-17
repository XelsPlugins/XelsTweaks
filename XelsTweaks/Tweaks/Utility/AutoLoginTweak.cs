using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XelsTweaks.Tweaks.Utility;

internal sealed unsafe class AutoLoginTweak : TweakBase
{
    public const string TweakId = "utility.autoLogin";

    private const string SelectedCharacterNameKey = "selectedCharacterName";
    private const string SelectedHomeWorldKey = "selectedHomeWorld";
    private const string SelectedContentIdKey = "selectedContentId";
    private const string CachedCharactersJsonKey = "cachedCharactersJson";
    private const string LastStatusKey = "lastStatus";
    private const string LastErrorKey = "lastError";
    private const string CharaSelectAddonName = "_CharaSelectListMenu";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool isDisarmedForProcess;

    private readonly LifestreamIpc lifestreamIpc;
    private DateTimeOffset enabledAt;
    private DateTimeOffset nextAttemptAt;
    private DateTimeOffset? lifestreamAvailableAt;
    private bool hasAttemptedThisSession;
    private bool disarmedForSession;

    public AutoLoginTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
        this.lifestreamIpc = new LifestreamIpc(services.PluginInterface);
    }

    public override string Id => TweakId;
    public override string Name => "Auto Login";
    public override string Description => "Uses Lifestream to log in a selected character from startup or character select. Requires Lifestream.";
    public override TweakCategory Category => TweakCategory.Utility;
    public override bool DrawConfigWhenDisabled => true;

    public override bool DrawConfig()
    {
        this.TryRefreshCharacterCacheFromLobby();

        ImGui.TextWrapped("Requires Lifestream. XelsTweaks only asks Lifestream to log in the selected character; it does not click login dialogs directly.");

        var changed = false;
        var characters = this.GetCachedCharacters();
        var selectedCharacter = this.GetSelectedCharacter();
        var currentLabel = selectedCharacter != null
            ? selectedCharacter.DisplayName
            : "Select a character";

        ImGui.SetNextItemWidth(280f);
        if (ImGui.BeginCombo("Character", currentLabel))
        {
            if (characters.Count == 0)
            {
                ImGui.TextDisabled("Open character select to populate this list.");
            }

            foreach (var character in characters)
            {
                var isSelected = selectedCharacter?.Matches(character) == true;
                var label = character.CanLoginNormally
                    ? character.DisplayName
                    : $"{character.DisplayName} (unavailable)";

                if (ImGui.Selectable(label, isSelected) && character.CanLoginNormally)
                {
                    this.SetSelectedCharacter(character);
                    this.SetStatus($"Selected {character.DisplayName}.");
                    changed = true;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (selectedCharacter == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.74f, 0.25f, 1f), "No character selected. Auto Login will stay idle.");
        }

        var status = this.GetString(LastStatusKey, "Idle.");
        ImGui.TextWrapped($"Status: {status}");

        var error = this.GetString(LastErrorKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Last error: {error}");
        }

        return changed;
    }

    protected override void OnEnable()
    {
        this.enabledAt = DateTimeOffset.UtcNow;
        this.nextAttemptAt = this.enabledAt + StartupDelay;
        this.lifestreamAvailableAt = null;
        this.hasAttemptedThisSession = false;
        this.disarmedForSession = this.Services.ClientState.IsLoggedIn || isDisarmedForProcess;
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.ClientState.Login += this.OnLogin;

        if (this.disarmedForSession)
        {
            var status = this.Services.ClientState.IsLoggedIn
                ? "Already logged in; Auto Login is disarmed for this game session."
                : "Auto Login already ran this game session.";
            this.SetStatus(status);
            return;
        }

        this.SetStatus("Waiting for Lifestream and character select data.");
    }

    protected override void OnDisable()
    {
        this.Services.ClientState.Login -= this.OnLogin;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.SetStatus("Disabled.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.TryRefreshCharacterCacheFromLobby();

        if (this.Services.ClientState.IsLoggedIn)
        {
            this.DisarmForSession("Logged in; Auto Login is disarmed for this game session.");
            return;
        }

        if (this.disarmedForSession || this.hasAttemptedThisSession)
        {
            return;
        }

        var selectedCharacter = this.GetSelectedCharacter();
        if (selectedCharacter == null)
        {
            this.SetStatus("No character selected. Auto Login is idle.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < this.nextAttemptAt)
        {
            return;
        }

        this.nextAttemptAt = now + RetryDelay;
        this.TryStartLogin(selectedCharacter, now);
    }

    private void OnLogin()
    {
        this.DisarmForSession("Logged in; Auto Login is disarmed for this game session.");
    }

    private void TryStartLogin(CachedCharacter selectedCharacter, DateTimeOffset now)
    {
        if (!this.lifestreamIpc.TryCanAutoLogin(out var canAutoLogin, out var error))
        {
            this.SetWaitingError(error);
            return;
        }

        this.lifestreamAvailableAt ??= now;
        this.SetLastError(null);
        if (now - this.lifestreamAvailableAt > StartupTimeout)
        {
            this.DisarmForSession("Auto Login timed out after Lifestream became available.");
            this.SetLastError("Timed out before Lifestream accepted the login request.");
            return;
        }

        if (!canAutoLogin)
        {
            this.SetStatus("Waiting for Lifestream auto-login support.");
            return;
        }

        if (!this.lifestreamIpc.TryIsBusy(out var isBusy, out error))
        {
            this.SetWaitingError(error);
            return;
        }

        if (isBusy)
        {
            this.SetStatus("Waiting for Lifestream to become idle.");
            return;
        }

        var usedCharacterSelectLogin = this.lifestreamIpc.TryCanInitiateTravelFromCharaSelectList(out var canUseCharacterSelect, out error)
            && canUseCharacterSelect;

        bool accepted;
        var invoked = usedCharacterSelectLogin
            ? this.lifestreamIpc.TryInitiateLoginFromCharaSelectScreen(selectedCharacter.Name, selectedCharacter.HomeWorld, out accepted, out error)
                && accepted
            : this.lifestreamIpc.TryConnectAndLogin(selectedCharacter.Name, selectedCharacter.HomeWorld, out accepted, out error)
                && accepted;

        if (!invoked)
        {
            this.SetWaitingError(error ?? "Lifestream did not accept the login request yet.");
            return;
        }

        this.SetLastError(null);
        this.DisarmForSession($"Login request handed to Lifestream for {selectedCharacter.DisplayName}.");
    }

    private void TryRefreshCharacterCacheFromLobby()
    {
        if (this.Services.ClientState.IsLoggedIn)
        {
            return;
        }

        var charaSelect = this.Services.GameGui.GetAddonByName(CharaSelectAddonName, 1);
        if (charaSelect.IsNull || !charaSelect.IsReady || !charaSelect.IsVisible)
        {
            return;
        }

        var agentLobby = AgentLobby.Instance();
        if (agentLobby == null)
        {
            return;
        }

        var characters = new List<CachedCharacter>();
        foreach (var entryPointer in agentLobby->LobbyData.CharaSelectEntries.ToArray())
        {
            var entry = entryPointer.Value;
            if (entry == null)
            {
                continue;
            }

            var character = new CachedCharacter(
                entry->ContentId,
                entry->NameString,
                entry->HomeWorldNameString,
                entry->HomeWorldId,
                entry->CurrentWorldId,
                CanLoginNormally(entry->LoginFlags));

            if (!string.IsNullOrWhiteSpace(character.Name)
                && !string.IsNullOrWhiteSpace(character.HomeWorld)
                && !characters.Any(existing => existing.Matches(character)))
            {
                characters.Add(character);
            }
        }

        characters = characters
            .OrderBy(character => character.HomeWorld, StringComparer.OrdinalIgnoreCase)
            .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (characters.Count == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(characters, JsonOptions);
        if (this.GetString(CachedCharactersJsonKey, string.Empty) != json)
        {
            this.SetString(CachedCharactersJsonKey, json);
            this.SetStatus($"Cached {characters.Count} character{(characters.Count == 1 ? string.Empty : "s")} from character select.");
        }
    }

    private List<CachedCharacter> GetCachedCharacters()
    {
        var json = this.GetString(CachedCharactersJsonKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CachedCharacter>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            this.Services.Log.Warning(ex, "Failed to read cached Auto Login characters");
            return [];
        }
    }

    private CachedCharacter? GetSelectedCharacter()
    {
        var name = this.GetString(SelectedCharacterNameKey, string.Empty);
        var homeWorld = this.GetString(SelectedHomeWorldKey, string.Empty);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(homeWorld))
        {
            return null;
        }

        var selectedContentId = this.GetString(SelectedContentIdKey, string.Empty);
        if (ulong.TryParse(selectedContentId, out var contentId))
        {
            var cachedMatch = this.GetCachedCharacters().FirstOrDefault(character => character.ContentId == contentId);
            if (cachedMatch != null)
            {
                return cachedMatch;
            }
        }

        return new CachedCharacter(0, name, homeWorld, 0, 0, true);
    }

    private void SetSelectedCharacter(CachedCharacter character)
    {
        this.SetString(SelectedCharacterNameKey, character.Name);
        this.SetString(SelectedHomeWorldKey, character.HomeWorld);
        this.SetString(SelectedContentIdKey, character.ContentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        this.SetLastError(null);
    }

    private void DisarmForSession(string status)
    {
        isDisarmedForProcess = true;
        this.disarmedForSession = true;
        this.hasAttemptedThisSession = true;
        this.SetStatus(status);
    }

    private void SetWaitingError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            this.SetStatus("Waiting for Lifestream to accept the login request.");
            return;
        }

        this.SetLastError(error);
        this.SetStatus(error);
    }

    private void SetStatus(string status)
    {
        if (this.GetString(LastStatusKey, string.Empty) == status)
        {
            return;
        }

        this.SetString(LastStatusKey, status);
    }

    private void SetLastError(string? error)
    {
        var value = error ?? string.Empty;
        if (this.GetString(LastErrorKey, string.Empty) == value)
        {
            return;
        }

        this.LastError = string.IsNullOrWhiteSpace(value) ? null : value;
        this.SetString(LastErrorKey, value);
    }

    private static bool CanLoginNormally(CharaSelectCharacterEntryLoginFlags flags)
    {
        return !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.Locked)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.NameChangeRequired)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.MissingExVersionForLogin)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.Unk32)
            && !flags.HasFlag(CharaSelectCharacterEntryLoginFlags.DCTraveling);
    }

    private sealed record CachedCharacter(
        ulong ContentId,
        string Name,
        string HomeWorld,
        ushort HomeWorldId,
        ushort CurrentWorldId,
        bool CanLoginNormally)
    {
        public string DisplayName => $"{this.Name} @ {this.HomeWorld}";

        public bool Matches(CachedCharacter other)
        {
            if (this.ContentId != 0 && other.ContentId != 0)
            {
                return this.ContentId == other.ContentId;
            }

            return this.Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase)
                && this.HomeWorld.Equals(other.HomeWorld, StringComparison.OrdinalIgnoreCase);
        }
    }
}
