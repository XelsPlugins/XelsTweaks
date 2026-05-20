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
    private const string ServiceAccountIndexKey = "serviceAccountIndex";
    private const string CachedCharactersJsonKey = "cachedCharactersJson";
    private const string LastStatusKey = "lastStatus";
    private const string LastErrorKey = "lastError";
    private const string CharaSelectAddonName = "_CharaSelectListMenu";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly IReadOnlyList<TweakOptionDefinition> CommandOptions =
    [
        TweakOptionDefinition.Choice(
            ServiceAccountIndexKey,
            "Service account",
            "Selects which service account automatic login uses.",
            "0",
            [
                new TweakOptionChoice("1", "Service account 1", "0"),
                new TweakOptionChoice("2", "Service account 2", "1"),
                new TweakOptionChoice("3", "Service account 3", "2"),
                new TweakOptionChoice("4", "Service account 4", "3"),
                new TweakOptionChoice("5", "Service account 5", "4"),
                new TweakOptionChoice("6", "Service account 6", "5"),
                new TweakOptionChoice("7", "Service account 7", "6"),
                new TweakOptionChoice("8", "Service account 8", "7")
            ],
            "Login")
    ];

    private static bool isDisarmedForProcess;

    private readonly LobbyLoginService lobbyLoginService;
    private DateTimeOffset nextAttemptAt;
    private bool hasAttemptedThisSession;
    private bool disarmedForSession;

    public AutoLoginTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
        this.lobbyLoginService = new LobbyLoginService(services);
    }

    public override string Id => TweakId;
    public override string Name => "Auto Login";
    public override string Description => "Logs in the selected character from the title screen or character select.";
    public override TweakCategory Category => TweakCategory.Utility;
    public override bool DrawConfigWhenDisabled => true;
    public override IReadOnlyList<TweakOptionDefinition> Options => CommandOptions;

    public override bool DrawConfig()
    {
        this.TryRefreshCharacterCacheFromLobby();

        ImGui.TextWrapped("Uses the game's title and character select screens directly.");

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
                ImGui.TextDisabled("Open character select to load your characters.");
            }

            foreach (var character in characters)
            {
                var isSelected = selectedCharacter?.Matches(character) == true;
                var label = character.CanLoginNormally
                    ? character.DisplayName
                    : $"{character.DisplayName} (cannot log in)";

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
            ImGui.TextColored(new Vector4(1f, 0.74f, 0.25f, 1f), "Choose a character to use automatic login.");
        }

        var serviceAccountIndex = this.GetServiceAccountIndex();
        var serviceAccountLabel = $"Service account {serviceAccountIndex + 1}";
        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("Service account", serviceAccountLabel))
        {
            for (var i = 0; i < 8; i++)
            {
                var isSelected = i == serviceAccountIndex;
                if (ImGui.Selectable($"Service account {i + 1}", isSelected))
                {
                    this.SetInt(ServiceAccountIndexKey, i);
                    changed = true;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        var status = this.GetString(LastStatusKey, "Ready.");
        ImGui.TextWrapped($"Status: {status}");

        var error = this.GetString(LastErrorKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Problem: {error}");
        }

        return changed;
    }

    protected override void OnEnable()
    {
        this.nextAttemptAt = DateTimeOffset.UtcNow + StartupDelay;
        this.hasAttemptedThisSession = false;
        this.disarmedForSession = this.Services.ClientState.IsLoggedIn || isDisarmedForProcess;
        this.Services.Framework.Update += this.OnFrameworkUpdate;
        this.Services.ClientState.Login += this.OnLogin;

        if (this.disarmedForSession)
        {
            var status = this.Services.ClientState.IsLoggedIn
                ? "Already logged in; automatic login will not run again this session."
                : "Automatic login already ran this game session.";
            this.SetStatus(status);
            return;
        }

        this.SetStatus("Waiting for the title screen or character select.");
    }

    protected override void OnDisable()
    {
        this.Services.ClientState.Login -= this.OnLogin;
        this.Services.Framework.Update -= this.OnFrameworkUpdate;
        this.SetStatus("Off.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.TryRefreshCharacterCacheFromLobby();

        if (this.Services.ClientState.IsLoggedIn)
        {
            this.DisarmForSession("Logged in; automatic login will not run again this session.");
            return;
        }

        if (this.disarmedForSession || this.hasAttemptedThisSession)
        {
            return;
        }

        var selectedCharacter = this.GetSelectedCharacter();
        if (selectedCharacter == null)
        {
            this.SetStatus("Choose a character to use automatic login.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < this.nextAttemptAt)
        {
            return;
        }

        this.nextAttemptAt = now + RetryDelay;
        this.TryStartLogin(selectedCharacter);
    }

    private void OnLogin()
    {
        this.DisarmForSession("Logged in; automatic login will not run again this session.");
    }

    private void TryStartLogin(CachedCharacter selectedCharacter)
    {
        if (!this.lobbyLoginService.TryStepLogin(
                selectedCharacter.ContentId,
                selectedCharacter.Name,
                selectedCharacter.HomeWorld,
                selectedCharacter.HomeWorldId,
                this.GetServiceAccountIndex(),
                out var completed,
                out var status,
                out var error))
        {
            this.SetWaitingError(error ?? "Automatic login could not run yet.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            this.SetWaitingError(error);
            return;
        }

        this.SetLastError(null);
        if (completed)
        {
            this.DisarmForSession(status);
            return;
        }

        this.SetStatus(status);
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
            this.SetStatus($"Loaded {characters.Count} character{(characters.Count == 1 ? string.Empty : "s")} from character select.");
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

    private int GetServiceAccountIndex()
    {
        return Math.Clamp(this.GetInt(ServiceAccountIndexKey, 0), 0, 7);
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
            this.SetWaitingStatus("Waiting for the login request to be accepted.");
            return;
        }

        this.SetLastError(error);
        this.SetStatus(error);
    }

    private void SetWaitingStatus(string status)
    {
        this.SetLastError(null);
        this.SetStatus(status);
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
