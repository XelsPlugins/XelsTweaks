using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace XelsTweaks.UI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private const float WindowWidth = 900f;
    private const float WindowHeight = 630f;
    private const float SidebarWidth = 300f;
    private const float SidebarPadding = 12f;
    private const float ContentPadding = 16f;
    private const float TweakRowMinHeight = 24f;
    private const float CheckboxColumnWidth = 26f;
    private const float CheckboxTextAlignmentOffset = 2f;
    private const float TweakConfigIndent = 18f;
    private const float HomeLogoSize = 192f;
    private const int SearchBufferSize = 128;
    private const string TweakRowTextPadding = "  ";

    private static readonly Vector4 AccentColor = new(0.75f, 0.75f, 1.0f, 1.0f);
    private static readonly Vector4 MutedAccentColor = new(0.55f, 0.65f, 1.0f, 1.0f);
    private static readonly Vector4 SidebarBackgroundColor = new(1.0f, 1.0f, 1.0f, 0.045f);
    private static readonly Vector4 EnabledColor = new(0.48f, 0.95f, 0.56f, 1.0f);
    private static readonly Vector4 DisabledColor = new(0.58f, 0.58f, 0.62f, 1.0f);
    private static readonly Vector4 RequirementColor = new(1.0f, 0.28f, 0.28f, 1.0f);
    private static readonly Vector4 WarningColor = new(1.0f, 0.74f, 0.25f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.0f, 0.35f, 0.35f, 1.0f);

    private readonly TweakManager tweakManager;
    private readonly ISharedImmediateTexture? logoTexture;
    private string searchText = string.Empty;
    private TweakBase? selectedTweak;
    private TweakBase? pendingAgreementTweak;
    private IRequiresEnableAgreement? pendingAgreement;
    private bool pendingAgreementAccepted;
    private bool pendingAgreementOpenRequested;

    public ConfigWindow(TweakManager tweakManager, ITextureProvider textureProvider, string logoPath)
        : base("XelsTweaks Configuration###XelsTweaksConfig")
    {
        this.tweakManager = tweakManager;
        if (File.Exists(logoPath))
        {
            this.logoTexture = textureProvider.GetFromFile(logoPath);
        }

        this.Size = new Vector2(WindowWidth, WindowHeight);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(WindowWidth, WindowHeight),
            MaximumSize = new Vector2(4096f, 2160f)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.ClearMissingSelection();

        var available = ImGui.GetContentRegionAvail();
        this.DrawSidebarBackground(available);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SidebarPadding, SidebarPadding));
        if (ImGui.BeginChild("##xelstweaks_sidebar", new Vector2(SidebarWidth, available.Y), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            this.DrawSidebar();
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();

        this.DrawEnableAgreementPopup();

        ImGui.SameLine(0f, 0f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ContentPadding, ContentPadding));
        if (ImGui.BeginChild("##xelstweaks_content", new Vector2(0f, available.Y), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            this.DrawContent();
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void ClearMissingSelection()
    {
        if (this.selectedTweak != null && !this.tweakManager.Tweaks.Contains(this.selectedTweak))
        {
            this.selectedTweak = null;
        }
    }

    private void DrawSidebarBackground(Vector2 available)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(SidebarWidth, available.Y);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, ImGui.GetColorU32(SidebarBackgroundColor));
    }

    private void DrawSidebar()
    {
        var shownTweaks = this.GetShownTweaks();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##xelstweaks_search", "Search", ref this.searchText, SearchBufferSize);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.BeginChild("##xelstweaks_tweak_list", new Vector2(0f, 0f), false, ImGuiWindowFlags.None))
        {
            if (shownTweaks.Length == 0)
            {
                this.DrawEmptySearchState();
            }
            else
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                foreach (var tweak in shownTweaks)
                {
                    this.DrawTweakListItem(tweak);
                }

                ImGui.PopStyleVar();
            }
        }

        ImGui.EndChild();
    }

    private TweakBase[] GetShownTweaks()
    {
        return this.tweakManager.Tweaks
            .Where(this.MatchesSearch)
            .OrderBy(tweak => tweak.Name)
            .ThenBy(tweak => tweak.Id)
            .ToArray();
    }

    private bool MatchesSearch(TweakBase tweak)
    {
        if (string.IsNullOrWhiteSpace(this.searchText))
        {
            return true;
        }

        return tweak.Name.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
            || tweak.Description.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
            || tweak.Id.Contains(this.searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawTweakListItem(TweakBase tweak)
    {
        ImGui.PushID(tweak.Id);
        this.tweakManager.RefreshRequirementState(tweak);

        var rowHeight = MathF.Max(TweakRowMinHeight, ImGui.GetFrameHeight());
        if (ImGui.BeginTable("##tweak_row", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingFixedFit, new Vector2(0f, rowHeight)))
        {
            ImGui.TableSetupColumn("##toggle", ImGuiTableColumnFlags.WidthFixed, CheckboxColumnWidth);
            ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
            ImGui.TableSetColumnIndex(0);

            var checkboxOffset = MathF.Max(0f, ((rowHeight - ImGui.GetFrameHeight()) * 0.5f) + CheckboxTextAlignmentOffset);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + checkboxOffset);
            var enabled = tweak.IsEnabled;
            var requirementMet = tweak.IsRequirementMet;
            if (!requirementMet)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                if (enabled && tweak is IRequiresEnableAgreement agreement && agreement.RequiresEnableAgreement)
                {
                    this.OpenEnableAgreement(tweak, agreement);
                }
                else
                {
                    this.tweakManager.SetEnabled(tweak, enabled);
                }
            }

            if (!requirementMet)
            {
                ImGui.EndDisabled();
            }

            ImGui.TableSetColumnIndex(1);

            var selected = this.selectedTweak == tweak;
            var textColorPushed = false;
            if (!requirementMet)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, RequirementColor);
                textColorPushed = true;
            }
            else if (tweak.LastError != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);
                textColorPushed = true;
            }
            else if (!tweak.IsEnabled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, DisabledColor);
                textColorPushed = true;
            }

            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
            var rowWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            if (ImGui.Selectable($"{TweakRowTextPadding}{tweak.Name}##select", selected, ImGuiSelectableFlags.None, new Vector2(rowWidth, rowHeight)))
            {
                this.selectedTweak = selected ? null : tweak;
            }

            ImGui.PopStyleVar();

            if (textColorPushed)
            {
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.PopID();
    }

    private void DrawContent()
    {
        if (this.selectedTweak == null)
        {
            this.DrawHomeScreen();
            return;
        }

        this.DrawSelectedTweak(this.selectedTweak);
    }

    private void DrawHomeScreen()
    {
        var enabledTweaks = this.tweakManager.Tweaks.Count(tweak => tweak.IsEnabled);
        var totalTweaks = this.tweakManager.Tweaks.Count;
        var start = ImGui.GetCursorPos();
        var available = ImGui.GetContentRegionAvail();
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var blockHeight = HomeLogoSize + (lineHeight * 4f) + (ImGui.GetStyle().ItemSpacing.Y * 4f);
        var top = start.Y + MathF.Max(0f, (available.Y - blockHeight) * 0.45f);

        ImGui.SetCursorPosY(top);
        this.DrawCenteredLogo(HomeLogoSize);
        ImGui.Spacing();
        DrawCenteredText("XelsTweaks", AccentColor);
        DrawCenteredText("For when staring at frustrations angrily doesn't resolve them!", DisabledColor);
        ImGui.Spacing();
        DrawCenteredText($"{enabledTweaks} enabled / {totalTweaks} total", MutedAccentColor);
    }

    private void DrawCenteredLogo(float size)
    {
        var logo = this.logoTexture?.GetWrapOrDefault();
        if (logo?.Handle != null)
        {
            var imageSize = new Vector2(size, size);
            var cursorX = ImGui.GetCursorPosX();
            var availableX = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(cursorX + MathF.Max(0f, (availableX - imageSize.X) * 0.5f));
            ImGui.Image(logo.Handle, imageSize);
            return;
        }

        DrawCenteredText("XT", AccentColor);
    }

    private void DrawSelectedTweak(TweakBase tweak)
    {
        this.tweakManager.RefreshRequirementState(tweak);
        this.DrawTweakHeader(tweak);
        ImGui.Spacing();
        ImGui.TextWrapped(tweak.Description);

        if (tweak.LastError != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ErrorColor, $"Needs attention: {tweak.LastError}");
        }

        if (tweak.IsEnabled || tweak.DrawConfigWhenDisabled)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.Indent(TweakConfigIndent);
            tweak.DrawConfig();
            ImGui.Unindent(TweakConfigIndent);
        }
    }

    private void OpenEnableAgreement(TweakBase tweak, IRequiresEnableAgreement agreement)
    {
        this.selectedTweak = tweak;
        this.pendingAgreementTweak = tweak;
        this.pendingAgreement = agreement;
        this.pendingAgreementAccepted = false;
        this.pendingAgreementOpenRequested = true;
    }

    private void DrawEnableAgreementPopup()
    {
        if (this.pendingAgreementTweak == null || this.pendingAgreement == null)
        {
            return;
        }

        if (!this.tweakManager.Tweaks.Contains(this.pendingAgreementTweak))
        {
            this.ClearEnableAgreement();
            return;
        }

        var title = this.pendingAgreement.EnableAgreementTitle;
        if (this.pendingAgreementOpenRequested)
        {
            ImGui.OpenPopup(title);
            this.pendingAgreementOpenRequested = false;
        }

        ImGui.SetNextWindowSize(new Vector2(560f, 0f), ImGuiCond.Always);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextColored(WarningColor, "Read before enabling");
        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520f);
        ImGui.TextUnformatted(this.pendingAgreement.EnableAgreementText);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Checkbox(this.pendingAgreement.EnableAgreementCheckboxLabel, ref this.pendingAgreementAccepted);
        ImGui.Spacing();

        var acceptDisabled = !this.pendingAgreementAccepted;
        if (acceptDisabled)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Accept and Enable", new Vector2(160f, 0f)))
        {
            this.pendingAgreement.AcceptEnableAgreement();
            this.tweakManager.SetEnabled(this.pendingAgreementTweak, true);
            this.ClearEnableAgreement();
            ImGui.CloseCurrentPopup();
        }

        if (acceptDisabled)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
        {
            this.ClearEnableAgreement();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void ClearEnableAgreement()
    {
        this.pendingAgreementTweak = null;
        this.pendingAgreement = null;
        this.pendingAgreementAccepted = false;
        this.pendingAgreementOpenRequested = false;
    }

    private void DrawTweakHeader(TweakBase tweak)
    {
        var statusText = GetStatusText(tweak);
        var statusColor = GetStatusColor(tweak);
        var startX = ImGui.GetCursorPosX();
        var availableX = ImGui.GetContentRegionAvail().X;
        var statusWidth = ImGui.CalcTextSize(statusText).X;

        ImGui.TextColored(AccentColor, tweak.Name);
        ImGui.SameLine();

        var statusX = startX + availableX - statusWidth;
        if (ImGui.GetCursorPosX() < statusX)
        {
            ImGui.SetCursorPosX(statusX);
        }

        ImGui.TextColored(statusColor, statusText);
        if (!tweak.IsRequirementMet && tweak.Requirement is { } requirement)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (ImGui.IsItemClicked())
            {
                Util.OpenLink(requirement.RepositoryUrl);
            }
        }

        ImGui.TextDisabled(FormatCategoryName(tweak.Category));
        ImGui.Separator();
    }

    private void DrawEmptySearchState()
    {
        ImGui.TextColored(WarningColor, "No matching tweaks.");
    }

    private static void DrawCenteredText(string text, Vector4 color)
    {
        var cursorX = ImGui.GetCursorPosX();
        var availableX = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(cursorX + MathF.Max(0f, (availableX - textWidth) * 0.5f));
        ImGui.TextColored(color, text);
    }

    private static string GetStatusText(TweakBase tweak)
    {
        if (!tweak.IsRequirementMet && tweak.Requirement is { } requirement)
        {
            return $"Needs {requirement.PluginName}";
        }

        if (tweak.LastError != null)
        {
            return "Needs attention";
        }

        return tweak.IsEnabled ? "On" : "Off";
    }

    private static Vector4 GetStatusColor(TweakBase tweak)
    {
        if (!tweak.IsRequirementMet)
        {
            return RequirementColor;
        }

        if (tweak.LastError != null)
        {
            return ErrorColor;
        }

        return tweak.IsEnabled ? EnabledColor : DisabledColor;
    }

    private static string FormatCategoryName(TweakCategory category)
    {
        return category switch
        {
            TweakCategory.General => "General",
            TweakCategory.Interface => "Interface",
            TweakCategory.Chat => "Chat",
            TweakCategory.Targeting => "Targeting",
            TweakCategory.Utility => "Utility",
            TweakCategory.Experimental => "Experimental",
            _ => category.ToString()
        };
    }
}
