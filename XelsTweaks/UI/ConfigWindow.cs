using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XelsTweaks.UI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private const float SidebarWidth = 150f;

    private readonly TweakManager tweakManager;
    private string searchText = string.Empty;
    private TweakCategory? selectedCategory;

    public ConfigWindow(TweakManager tweakManager)
        : base("XelsTweaks###XelsTweaksConfig")
    {
        this.tweakManager = tweakManager;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620f, 360f),
            MaximumSize = new Vector2(900f, 720f)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search", ref this.searchText, 128);
        ImGui.Spacing();

        if (ImGui.BeginTable("##xelstweaks_layout", 2, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##categories", ImGuiTableColumnFlags.WidthFixed, SidebarWidth);
            ImGui.TableSetupColumn("##tweaks", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            this.DrawCategoryList();
            ImGui.TableSetColumnIndex(1);
            this.DrawTweaks();
            ImGui.EndTable();
        }
    }

    private void DrawCategoryList()
    {
        this.DrawCategoryButton(null, "All");
        ImGui.Separator();

        foreach (var category in Enum.GetValues<TweakCategory>())
        {
            this.DrawCategoryButton(category, category.ToString());
        }
    }

    private void DrawCategoryButton(TweakCategory? category, string label)
    {
        var selected = this.selectedCategory == category;
        var count = this.CountTweaks(category);
        var buttonLabel = $"{label} ({count})";
        if (ImGui.Selectable(buttonLabel, selected, ImGuiSelectableFlags.None, new Vector2(SidebarWidth - 8f, 24f)))
        {
            this.selectedCategory = category;
        }
    }

    private int CountTweaks(TweakCategory? category)
    {
        return this.tweakManager.Tweaks.Count(tweak => category == null || tweak.Category == category.Value);
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
            ImGui.TextDisabled("No tweaks match the current filter.");
            return;
        }

        foreach (var tweak in tweaks)
        {
            this.DrawTweak(tweak);
            ImGui.Separator();
        }
    }

    private bool MatchesFilters(TweakBase tweak)
    {
        if (this.selectedCategory != null && tweak.Category != this.selectedCategory.Value)
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

        var enabled = tweak.IsEnabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            this.tweakManager.SetEnabled(tweak, enabled);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(tweak.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"({tweak.Id})");

        ImGui.TextWrapped(tweak.Description);

        if (tweak.LastError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Last error: {tweak.LastError}");
        }

        if (tweak.IsEnabled)
        {
            ImGui.Indent(24f);
            tweak.DrawConfig();
            ImGui.Unindent(24f);
        }

        ImGui.PopID();
    }
}

