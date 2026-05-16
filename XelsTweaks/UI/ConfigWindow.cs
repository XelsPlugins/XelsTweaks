using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace XelsTweaks.UI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private const float SidebarWidth = 168f;
    private const float LogoSize = SidebarWidth - 16f;
    private const float CategoryButtonHeight = 26f;
    private const float ToggleColumnWidth = 34f;
    private const float TweakConfigIndent = 18f;
    private const float MenuHeightAllowance = 1.2f;

    private static readonly Vector4 AccentColor = new(0.75f, 0.75f, 1.0f, 1.0f);
    private static readonly Vector4 MutedAccentColor = new(0.55f, 0.65f, 1.0f, 1.0f);
    private static readonly Vector4 EnabledColor = new(0.48f, 0.95f, 0.56f, 1.0f);
    private static readonly Vector4 DisabledColor = new(0.58f, 0.58f, 0.62f, 1.0f);
    private static readonly Vector4 WarningColor = new(1.0f, 0.74f, 0.25f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.0f, 0.35f, 0.35f, 1.0f);
    private static readonly Vector2 LogoUvMin = new(0.115f, 0.115f);
    private static readonly Vector2 LogoUvMax = new(0.885f, 0.885f);

    private readonly TweakManager tweakManager;
    private readonly string logoPath;
    private readonly ISharedImmediateTexture? logoTexture;
    private string searchText = string.Empty;
    private TweakCategory? selectedCategory;
    private bool showEnabledOnly;

    public ConfigWindow(TweakManager tweakManager, ITextureProvider textureProvider, string logoPath)
        : base("XelsTweaks Configuration###XelsTweaksConfig")
    {
        this.tweakManager = tweakManager;
        this.logoPath = logoPath;
        if (File.Exists(this.logoPath))
        {
            this.logoTexture = textureProvider.GetFromFile(this.logoPath);
        }

        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700f, 420f),
            MaximumSize = new Vector2(980f, 760f)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var contentStartY = ImGui.GetCursorPosY();
        this.DrawHeader();
        var headerHeight = ImGui.GetCursorPosY() - contentStartY;

        if (ImGui.BeginTable("##xelstweaks_layout", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("##categories", ImGuiTableColumnFlags.WidthFixed, SidebarWidth);
            ImGui.TableSetupColumn("##tweaks", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var menuStartY = ImGui.GetCursorPosY();
            this.DrawCategoryList();
            var menuHeight = ImGui.GetCursorPosY() - menuStartY;
            var maxTweakPaneHeight = MathF.Max(menuHeight, (menuHeight * MenuHeightAllowance) - headerHeight);

            ImGui.TableSetColumnIndex(1);
            this.DrawTweakPane(maxTweakPaneHeight);
            ImGui.EndTable();
        }
    }

    private void DrawHeader()
    {
        var totalTweaks = this.tweakManager.Tweaks.Count;
        var enabledTweaks = this.tweakManager.Tweaks.Count(tweak => tweak.IsEnabled);

        ImGui.TextColored(AccentColor, "XelsTweaks");
        ImGui.SameLine();
        ImGui.TextDisabled("Modular tweak hub");

        ImGui.SameLine();
        var summary = $"{enabledTweaks} enabled / {totalTweaks} total";
        var summaryWidth = ImGui.CalcTextSize(summary).X;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        if (contentWidth > summaryWidth)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + contentWidth - summaryWidth);
        }

        ImGui.TextColored(MutedAccentColor, summary);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawCategoryList()
    {
        this.DrawSidebarLogo();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawCategoryButton(null, "All tweaks");
        ImGui.Separator();

        foreach (var category in Enum.GetValues<TweakCategory>())
        {
            this.DrawCategoryButton(category, FormatCategoryName(category));
        }
    }

    private void DrawCategoryButton(TweakCategory? category, string label)
    {
        var selected = this.selectedCategory == category;
        var count = this.CountTweaks(category);
        var disabled = count == 0 && !selected;
        var buttonLabel = $"{label} ({count})";

        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.25f, 0.28f, 0.55f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.30f, 0.34f, 0.65f, 1.0f));
        }

        if (disabled)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Selectable(buttonLabel, selected, ImGuiSelectableFlags.None, new Vector2(SidebarWidth - 8f, CategoryButtonHeight)))
        {
            this.selectedCategory = category;
        }

        if (disabled)
        {
            ImGui.EndDisabled();
        }

        if (selected)
        {
            ImGui.PopStyleColor(2);
        }

        ImGui.PopStyleVar();
    }

    private int CountTweaks(TweakCategory? category)
    {
        return this.tweakManager.Tweaks.Count(tweak => category == null || tweak.Category == category.Value);
    }

    private void DrawTweakPane(float maxPaneHeight)
    {
        var paneStartY = ImGui.GetCursorPosY();
        this.DrawFilterBar();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var filterHeight = ImGui.GetCursorPosY() - paneStartY;
        var tweakListHeight = MathF.Max(1f, maxPaneHeight - filterHeight);

        if (ImGui.BeginChild("##tweaks_scroll", new Vector2(0f, tweakListHeight), false, ImGuiWindowFlags.None))
        {
            this.DrawTweaks();
        }

        ImGui.EndChild();
    }

    private void DrawFilterBar()
    {
        var selectedLabel = this.selectedCategory != null
            ? FormatCategoryName(this.selectedCategory.Value)
            : "All tweaks";
        var filteredCount = this.tweakManager.Tweaks.Count(this.MatchesFilters);

        ImGui.TextColored(AccentColor, selectedLabel);
        ImGui.SameLine();
        ImGui.TextDisabled($"{filteredCount} shown");

        ImGui.TextUnformatted("Search");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##search", ref this.searchText, 128);

        ImGui.Checkbox("Enabled only", ref this.showEnabledOnly);
        if (!string.IsNullOrWhiteSpace(this.searchText))
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear search"))
            {
                this.searchText = string.Empty;
            }
        }
    }

    private void DrawTweaks()
    {
        var tweaks = this.tweakManager.Tweaks
            .Where(this.MatchesFilters)
            .OrderBy(tweak => tweak.Category)
            .ThenBy(tweak => tweak.Name)
            .ToArray();

        if (tweaks.Length == 0)
        {
            this.DrawEmptyState();
            return;
        }

        TweakCategory? currentCategory = null;
        foreach (var tweak in tweaks)
        {
            if (this.selectedCategory == null && currentCategory != tweak.Category)
            {
                currentCategory = tweak.Category;
                this.DrawSectionHeader(FormatCategoryName(tweak.Category));
            }

            this.DrawTweak(tweak);
        }
    }

    private bool MatchesFilters(TweakBase tweak)
    {
        if (this.selectedCategory != null && tweak.Category != this.selectedCategory.Value)
        {
            return false;
        }

        if (this.showEnabledOnly && !tweak.IsEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(this.searchText))
        {
            return true;
        }

        return tweak.Name.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
            || tweak.Description.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
            || tweak.Id.Contains(this.searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawTweak(TweakBase tweak)
    {
        ImGui.PushID(tweak.Id);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));

        if (ImGui.BeginTable("##tweak_row", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("##toggle", ImGuiTableColumnFlags.WidthFixed, ToggleColumnWidth);
            ImGui.TableSetupColumn("##body", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var enabled = tweak.IsEnabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                this.tweakManager.SetEnabled(tweak, enabled);
            }

            ImGui.TableSetColumnIndex(1);
            this.DrawTweakBody(tweak);
            ImGui.EndTable();
        }

        ImGui.PopStyleVar();
        ImGui.PopID();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawTweakBody(TweakBase tweak)
    {
        ImGui.TextUnformatted(tweak.Name);
        ImGui.SameLine();
        ImGui.TextColored(tweak.IsEnabled ? EnabledColor : DisabledColor, tweak.IsEnabled ? "Enabled" : "Disabled");
        ImGui.SameLine();
        ImGui.TextDisabled(tweak.Id);

        ImGui.TextWrapped(tweak.Description);

        if (tweak.LastError != null)
        {
            ImGui.TextColored(ErrorColor, $"Last error: {tweak.LastError}");
        }

        if (tweak.IsEnabled || tweak.DrawConfigWhenDisabled)
        {
            ImGui.Spacing();
            ImGui.Indent(TweakConfigIndent);
            tweak.DrawConfig();
            ImGui.Unindent(TweakConfigIndent);
        }
    }

    private void DrawSidebarLogo()
    {
        var logo = this.logoTexture?.GetWrapOrDefault();
        if (logo?.Handle != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (SidebarWidth - LogoSize - 8f) * 0.5f));
            ImGui.Image(logo.Handle, new Vector2(LogoSize, LogoSize), LogoUvMin, LogoUvMax);
            return;
        }

        const string shortName = "XT";
        const string title = "Tweaks";

        var shortNameSize = ImGui.CalcTextSize(shortName);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (SidebarWidth - shortNameSize.X - 8f) * 0.5f));
        ImGui.TextColored(AccentColor, shortName);

        var titleSize = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (SidebarWidth - titleSize.X - 8f) * 0.5f));
        ImGui.TextDisabled(title);
    }

    private void DrawSectionHeader(string title)
    {
        ImGui.TextColored(AccentColor, title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawEmptyState()
    {
        ImGui.TextColored(WarningColor, "No tweaks match the current filters.");
        ImGui.TextDisabled("Try a different category or search term.");
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
