using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XelsTweaks.Services;
using XelsTweaks.Tweaks.MenuControl;

namespace XelsTweaks.UI;

internal sealed class CommandReferenceWindow : Window, IDisposable
{
    private const float WindowWidth = 1040f;
    private const float WindowHeight = 720f;
    private const float SidebarWidth = 240f;
    private const float OptionListWidth = 320f;
    private const string StatusSchema = "state, status, isVisible, isBusy, isPaused, completed, total, skipped, currentItem, error";
    private const string ResultSchema = "success, message, snapshot";

    private static readonly Vector4 AccentColor = new(0.75f, 0.75f, 1.0f, 1.0f);
    private static readonly Vector4 AvailableColor = new(0.48f, 0.95f, 0.56f, 1.0f);
    private static readonly Vector4 MutedColor = new(0.62f, 0.62f, 0.68f, 1.0f);
    private static readonly Vector4 UnavailableColor = new(1.0f, 0.74f, 0.25f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.0f, 0.35f, 0.35f, 1.0f);

    private readonly TweakManager tweakManager;
    private string? selectedTweakId;
    private string? selectedOptionId;

    public CommandReferenceWindow(TweakManager tweakManager)
        : base("XelsTweaks Command Reference###XelsTweaksCommandReference")
    {
        this.tweakManager = tweakManager;
        this.Size = new Vector2(WindowWidth, WindowHeight);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860f, 540f),
            MaximumSize = new Vector2(4096f, 2160f)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var tweaks = this.tweakManager.Tweaks
            .OrderBy(tweak => tweak.Category)
            .ThenBy(tweak => tweak.Name)
            .ThenBy(tweak => tweak.Id)
            .ToArray();

        if (tweaks.Length == 0)
        {
            ImGui.TextWrapped("No tweaks are registered.");
            return;
        }

        if (this.selectedTweakId == null || tweaks.All(tweak => !tweak.Id.Equals(this.selectedTweakId, StringComparison.OrdinalIgnoreCase)))
        {
            this.selectedTweakId = tweaks[0].Id;
            this.selectedOptionId = null;
        }

        var available = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##xelstweaks_cmdref_sidebar", new Vector2(SidebarWidth, available.Y), true))
        {
            this.DrawSidebar(tweaks);
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##xelstweaks_cmdref_content", new Vector2(0f, available.Y), false))
        {
            var selected = tweaks.First(tweak => tweak.Id.Equals(this.selectedTweakId, StringComparison.OrdinalIgnoreCase));
            this.DrawTweakReference(selected);
        }

        ImGui.EndChild();
    }

    private void DrawSidebar(TweakBase[] tweaks)
    {
        ImGui.TextDisabled("Tweaks");
        ImGui.Separator();

        foreach (var tweak in tweaks)
        {
            this.DrawTweakSidebarItem(tweak);
        }
    }

    private void DrawTweakSidebarItem(TweakBase tweak)
    {
        var selected = tweak.Id.Equals(this.selectedTweakId, StringComparison.OrdinalIgnoreCase);
        if (ImGui.Selectable($"{tweak.Name}##{tweak.Id}", selected))
        {
            this.selectedTweakId = tweak.Id;
            this.selectedOptionId = null;
        }
    }

    private void DrawTweakReference(TweakBase tweak)
    {
        this.tweakManager.RefreshRequirementState(tweak);
        var menu = tweak as IControllableTweakMenu;

        this.DrawHeader(tweak, menu);

        if (ImGui.BeginTabBar("##xelstweaks_cmdref_tabs", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Commands"))
            {
                this.DrawCommandsTab(tweak, menu);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Options"))
            {
                this.DrawOptionsTab(tweak);
                ImGui.EndTabItem();
            }

            if (menu != null && ImGui.BeginTabItem("Menu Actions"))
            {
                this.DrawMenuActionsTab(menu);
                ImGui.EndTabItem();
            }

            if (menu != null && ImGui.BeginTabItem("Menu IPC"))
            {
                this.DrawIpcTab(menu);
                ImGui.EndTabItem();
            }

            if (menu != null && ImGui.BeginTabItem("Menu Schema"))
            {
                this.DrawSchemaTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader(TweakBase tweak, IControllableTweakMenu? menu)
    {
        ImGui.TextColored(AccentColor, tweak.Name);
        ImGui.SameLine();
        ImGui.TextColored(GetTweakStatusColor(tweak), GetTweakStatusText(tweak));
        ImGui.TextDisabled($"{FormatCategoryName(tweak.Category)} - {tweak.Id}");
        if (menu != null)
        {
            ImGui.TextDisabled($"Menu: {menu.MenuId}");
        }

        ImGui.TextWrapped(tweak.Description);

        if (!tweak.IsRequirementMet && tweak.Requirement is { } requirement)
        {
            ImGui.TextColored(ErrorColor, $"Needs {requirement.PluginName}.");
        }
        else if (tweak.LastError != null)
        {
            ImGui.TextColored(ErrorColor, $"Needs attention: {tweak.LastError}");
        }

        ImGui.Separator();
    }

    private void DrawCommandsTab(TweakBase tweak, IControllableTweakMenu? menu)
    {
        ImGui.TextColored(AccentColor, "Current state");
        this.DrawTweakStatusTable(tweak, menu);
        ImGui.Spacing();

        ImGui.TextColored(AccentColor, "Tweak state");
        this.DrawCommandLine($"/xt on {tweak.Id}");
        this.DrawCommandLine($"/xt off {tweak.Id}");
        this.DrawCommandLine($"/xt toggle {tweak.Id}");

        if (tweak.Options.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, "Options");
            this.DrawCommandLine($"/xt option {tweak.Id} list");
        }

        if (menu != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(AccentColor, "Workflow menu");
            this.DrawCommandLine($"/xt menu {menu.MenuId} status");
            this.DrawCommandLine($"/xt menu {menu.MenuId} actions");
        }
    }

    private void DrawOptionsTab(TweakBase tweak)
    {
        var options = tweak.Options
            .OrderBy(option => option.Group)
            .ThenBy(option => option.Label)
            .ThenBy(option => option.Id)
            .ToArray();

        if (options.Length == 0)
        {
            ImGui.TextDisabled("This tweak has no scriptable options.");
            return;
        }

        if (this.selectedOptionId == null || options.All(option => !option.Id.Equals(this.selectedOptionId, StringComparison.OrdinalIgnoreCase)))
        {
            this.selectedOptionId = options[0].Id;
        }

        var available = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##xelstweaks_cmdref_option_list", new Vector2(OptionListWidth, available.Y), true))
        {
            this.DrawOptionList(tweak, options);
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##xelstweaks_cmdref_option_details", new Vector2(0f, available.Y), false))
        {
            var selected = options.First(option => option.Id.Equals(this.selectedOptionId, StringComparison.OrdinalIgnoreCase));
            this.DrawOptionDetails(tweak, selected);
        }

        ImGui.EndChild();
    }

    private void DrawOptionList(TweakBase tweak, IReadOnlyList<TweakOptionDefinition> options)
    {
        var currentGroup = string.Empty;
        foreach (var option in options)
        {
            if (!option.Group.Equals(currentGroup, StringComparison.Ordinal))
            {
                currentGroup = option.Group;
                ImGui.Spacing();
                ImGui.TextColored(AccentColor, currentGroup);
            }

            var selected = option.Id.Equals(this.selectedOptionId, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{option.Label}##{option.Id}", selected))
            {
                this.selectedOptionId = option.Id;
            }

            if (tweak.TryGetOptionValue(option.Id, out var value))
            {
                ImGui.TextDisabled($"{option.Id} = {value.Value}");
            }
            else
            {
                ImGui.TextDisabled(option.Id);
            }
        }
    }

    private void DrawOptionDetails(TweakBase tweak, TweakOptionDefinition option)
    {
        tweak.TryGetOptionValue(option.Id, out var value);

        ImGui.TextColored(AccentColor, option.Label);
        ImGui.TextDisabled(option.Id);
        ImGui.TextWrapped(option.Description);
        ImGui.Spacing();

        if (ImGui.BeginTable("##xelstweaks_cmdref_option_state", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            this.DrawStateRow("current", value?.Value ?? string.Empty);
            this.DrawStateRow("type", FormatOptionKind(option.Kind));
            this.DrawStateRow("group", option.Group);
            this.DrawStateRow("default", FormatDefaultValue(option));
            this.DrawStateRow("values", FormatChoiceValues(option));
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Commands");
        this.DrawCommandLine($"/xt option {tweak.Id} get {option.Id}");

        switch (option.Kind)
        {
            case TweakOptionKind.Boolean:
                this.DrawCommandLine($"/xt option {tweak.Id} set {option.Id} on");
                this.DrawCommandLine($"/xt option {tweak.Id} set {option.Id} off");
                this.DrawCommandLine($"/xt option {tweak.Id} toggle {option.Id}");
                break;
            case TweakOptionKind.Choice:
                foreach (var choice in option.Choices)
                {
                    this.DrawCommandLine($"/xt option {tweak.Id} set {option.Id} {choice.Value}");
                }

                break;
            case TweakOptionKind.Integer:
            case TweakOptionKind.Text:
                this.DrawCommandLine($"/xt option {tweak.Id} set {option.Id} <value>");
                break;
        }
    }

    private void DrawMenuActionsTab(IControllableTweakMenu menu)
    {
        var snapshot = menu.GetMenuSnapshot();
        var actions = menu.GetMenuActions();

        ImGui.TextColored(AccentColor, "Live state");
        this.DrawMenuStateTable(snapshot);
        ImGui.Spacing();

        ImGui.TextColored(AccentColor, "Actions");
        this.DrawActionsTable(menu, actions);
        ImGui.Spacing();

        ImGui.TextColored(AccentColor, "Result shape");
        ImGui.TextWrapped(ResultSchema);
    }

    private void DrawActionsTable(IControllableTweakMenu menu, IReadOnlyList<TweakMenuAction> actions)
    {
        if (ImGui.BeginTable("##xelstweaks_cmdref_actions", 4, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 115f);
            ImGui.TableSetupColumn("Purpose", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var action in actions)
            {
                this.DrawActionRow(menu, action);
            }

            ImGui.EndTable();
        }
    }

    private void DrawIpcTab(IControllableTweakMenu menu)
    {
        ImGui.TextColored(AccentColor, "IPC channels");
        if (ImGui.BeginTable("##xelstweaks_cmdref_ipc", 4, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Operation", ImGuiTableColumnFlags.WidthFixed, 115f);
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Args", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("Returns", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableHeadersRow();

            this.DrawIpcRow("List", TweakMenuIpcService.ListChannel, string.Empty, "menu[] json");
            this.DrawIpcRow("GetStatus", TweakMenuIpcService.GetStatusChannel, "menuId", "snapshot json");
            this.DrawIpcRow("GetActions", TweakMenuIpcService.GetActionsChannel, "menuId", "action[] json");
            this.DrawIpcRow("ExecuteAction", TweakMenuIpcService.ExecuteActionChannel, "menuId, action", "result json");

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Current menu");
        this.DrawCopyableLine(menu.MenuId);
    }

    private void DrawSchemaTab()
    {
        ImGui.TextColored(AccentColor, "Snapshot");
        this.DrawSchemaTable(
            "snapshot",
        [
            ("state", "string", "Workflow state name."),
            ("status", "string", "Current user-facing status."),
            ("isVisible", "bool", "Whether the controlled game menu is currently visible."),
            ("isBusy", "bool", "Whether the workflow is active."),
            ("isPaused", "bool", "Whether the workflow is paused."),
            ("completed", "int", "Completed work count."),
            ("total", "int", "Queued work count."),
            ("skipped", "int", "Skipped work count."),
            ("currentItem", "string?", "Current item or outfit, when known."),
            ("error", "string?", "Current error, when failed.")
        ]);

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Action");
        this.DrawSchemaTable(
            "action",
        [
            ("id", "string", "Stable action identifier."),
            ("label", "string", "Short display label."),
            ("description", "string", "Action purpose."),
            ("requires", "string", "State required before the action can run."),
            ("available", "bool", "Whether it can run now."),
            ("disabledReason", "string?", "Why it cannot run now.")
        ]);

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Result");
        this.DrawSchemaTable(
            "result",
        [
            ("success", "bool", "Whether the action ran."),
            ("message", "string", "Result or failure reason."),
            ("snapshot", "object", "Updated snapshot after the action.")
        ]);

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Snapshot fields");
        ImGui.TextWrapped(StatusSchema);
    }

    private void DrawTweakStatusTable(TweakBase tweak, IControllableTweakMenu? menu)
    {
        if (!ImGui.BeginTable("##xelstweaks_cmdref_tweak_state", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        this.DrawStateRow("status", GetTweakStatusText(tweak), GetTweakStatusColor(tweak));
        this.DrawStateRow("enabled", tweak.IsEnabled ? "true" : "false");
        this.DrawStateRow("requirement", tweak.Requirement?.PluginName ?? string.Empty);
        this.DrawStateRow("lastError", tweak.LastError ?? string.Empty, tweak.LastError == null ? null : ErrorColor);
        this.DrawStateRow("options", tweak.Options.Count.ToString());
        this.DrawStateRow("menuId", menu?.MenuId ?? string.Empty);
        ImGui.EndTable();
    }

    private void DrawMenuStateTable(TweakMenuSnapshot snapshot)
    {
        if (!ImGui.BeginTable("##xelstweaks_cmdref_menu_state", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        this.DrawStateRow("state", snapshot.State, GetMenuStateColor(snapshot));
        this.DrawStateRow("status", snapshot.Status);
        this.DrawStateRow("isVisible", snapshot.IsVisible ? "true" : "false");
        this.DrawStateRow("isBusy", snapshot.IsBusy ? "true" : "false");
        this.DrawStateRow("isPaused", snapshot.IsPaused ? "true" : "false");
        this.DrawStateRow("completed", snapshot.Completed.ToString());
        this.DrawStateRow("total", snapshot.Total.ToString());
        this.DrawStateRow("skipped", snapshot.Skipped.ToString());
        this.DrawStateRow("currentItem", snapshot.CurrentItem ?? string.Empty);
        this.DrawStateRow("error", snapshot.Error ?? string.Empty, string.IsNullOrWhiteSpace(snapshot.Error) ? null : ErrorColor);
        ImGui.EndTable();
    }

    private void DrawActionRow(IControllableTweakMenu menu, TweakMenuAction action)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(action.Id);
        ImGui.TextDisabled(action.Label);

        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(action.Available ? AvailableColor : UnavailableColor, action.Available ? "Available" : "Blocked");
        if (!action.Available && !string.IsNullOrWhiteSpace(action.DisabledReason))
        {
            ImGui.TextWrapped(action.DisabledReason);
        }

        ImGui.TableSetColumnIndex(2);
        ImGui.TextWrapped(action.Description);
        ImGui.TextDisabled(action.Requires);

        ImGui.TableSetColumnIndex(3);
        this.DrawCommandLine($"/xt menu {menu.MenuId} {action.Id}");
    }

    private void DrawIpcRow(string operation, string channel, string args, string returns)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(operation);
        ImGui.TableSetColumnIndex(1);
        this.DrawCopyableLine(channel);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled(args);
        ImGui.TableSetColumnIndex(3);
        ImGui.TextDisabled(returns);
    }

    private void DrawSchemaTable(string id, IReadOnlyList<(string Field, string Type, string Description)> rows)
    {
        if (!ImGui.BeginTable($"##schema_{id}", 3, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Field);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextDisabled(row.Type);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(row.Description);
        }

        ImGui.EndTable();
    }

    private void DrawStateRow(string field, string value, Vector4? color = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(field);
        ImGui.TableSetColumnIndex(1);
        if (color.HasValue)
        {
            ImGui.TextColored(color.Value, value);
            return;
        }

        ImGui.TextWrapped(value);
    }

    private void DrawCopyableLine(string text)
    {
        ImGui.PushID(text);
        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(text);
        }

        ImGui.SameLine();
        var remainingWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + remainingWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopID();
    }

    private void DrawCommandLine(string command)
    {
        ImGui.PushID($"command:{command}");
        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(command);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("SND"))
        {
            ImGui.SetClipboardText(FormatSndCommand(command));
        }

        ImGui.SameLine();
        var remainingWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + remainingWidth);
        ImGui.TextUnformatted(command);
        ImGui.PopTextWrapPos();
        ImGui.PopID();
    }

    private static string FormatSndCommand(string command)
    {
        return $"yield(\"{command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\")";
    }

    private static string FormatChoiceValues(TweakOptionDefinition option)
    {
        return option.Kind == TweakOptionKind.Choice
            ? string.Join(", ", option.Choices.Select(choice => $"{choice.Value} ({choice.Label})"))
            : string.Empty;
    }

    private static string FormatDefaultValue(TweakOptionDefinition option)
    {
        return option.Kind switch
        {
            TweakOptionKind.Boolean => IsEnabledText(option.DefaultValue),
            TweakOptionKind.Choice => option.Choices.FirstOrDefault(choice => choice.StoredValue.Equals(option.DefaultValue, StringComparison.OrdinalIgnoreCase))?.Value ?? option.DefaultValue,
            _ => option.DefaultValue
        };
    }

    private static string FormatOptionKind(TweakOptionKind kind)
    {
        return kind switch
        {
            TweakOptionKind.Boolean => "bool",
            TweakOptionKind.Choice => "choice",
            TweakOptionKind.Integer => "integer",
            TweakOptionKind.Text => "text",
            _ => kind.ToString()
        };
    }

    private static string GetTweakStatusText(TweakBase tweak)
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

    private static Vector4 GetTweakStatusColor(TweakBase tweak)
    {
        if (!tweak.IsRequirementMet || tweak.LastError != null)
        {
            return ErrorColor;
        }

        return tweak.IsEnabled ? AvailableColor : MutedColor;
    }

    private static Vector4 GetMenuStateColor(TweakMenuSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            return ErrorColor;
        }

        if (snapshot.IsBusy)
        {
            return snapshot.IsPaused ? UnavailableColor : AvailableColor;
        }

        return snapshot.IsVisible ? AccentColor : MutedColor;
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

    private static string IsEnabledText(string value)
    {
        return bool.TryParse(value, out var enabled) && enabled ? "on" : "off";
    }
}
