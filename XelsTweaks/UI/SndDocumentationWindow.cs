using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XelsTweaks.Services;
using XelsTweaks.Tweaks.MenuControl;

namespace XelsTweaks.UI;

internal sealed class SndDocumentationWindow : Window, IDisposable
{
    private const float WindowWidth = 980f;
    private const float WindowHeight = 680f;
    private const float SidebarWidth = 300f;
    private const float CopyButtonWidth = 54f;
    private const string ListCommand = "/xt menu list";
    private const string StatusSchema = "state, status, isVisible, isBusy, isPaused, completed, total, skipped, currentItem, error";
    private const string ResultSchema = "success, message, snapshot";

    private static readonly Vector4 AccentColor = new(0.75f, 0.75f, 1.0f, 1.0f);
    private static readonly Vector4 AvailableColor = new(0.48f, 0.95f, 0.56f, 1.0f);
    private static readonly Vector4 MutedColor = new(0.62f, 0.62f, 0.68f, 1.0f);
    private static readonly Vector4 UnavailableColor = new(1.0f, 0.74f, 0.25f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.0f, 0.35f, 0.35f, 1.0f);

    private readonly TweakManager tweakManager;
    private string? selectedMenuId;

    public SndDocumentationWindow(TweakManager tweakManager)
        : base("XelsTweaks SND Menu API###XelsTweaksSndDocs")
    {
        this.tweakManager = tweakManager;
        this.Size = new Vector2(WindowWidth, WindowHeight);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820f, 520f),
            MaximumSize = new Vector2(4096f, 2160f)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var menus = this.tweakManager.ControllableMenus
            .OrderBy(menu => ((TweakBase)menu).Name)
            .ToArray();

        if (menus.Length == 0)
        {
            ImGui.TextWrapped("No controllable tweak menus are registered.");
            return;
        }

        if (this.selectedMenuId == null || menus.All(menu => !menu.MenuId.Equals(this.selectedMenuId, StringComparison.OrdinalIgnoreCase)))
        {
            this.selectedMenuId = menus[0].MenuId;
        }

        var available = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##xelstweaks_snd_sidebar", new Vector2(SidebarWidth, available.Y), true))
        {
            this.DrawSidebar(menus);
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##xelstweaks_snd_content", new Vector2(0f, available.Y), false))
        {
            var selected = menus.First(menu => menu.MenuId.Equals(this.selectedMenuId, StringComparison.OrdinalIgnoreCase));
            this.DrawMenuReference(selected);
        }

        ImGui.EndChild();
    }

    private void DrawSidebar(IControllableTweakMenu[] menus)
    {
        ImGui.TextColored(AccentColor, "SND Menu API");
        ImGui.TextWrapped("Scriptable workflow controls for XelsTweaks menus.");
        ImGui.Spacing();
        this.DrawCopyableLine(ListCommand);
        this.DrawCopyableLine($"yield(\"{ListCommand}\")");
        ImGui.Separator();

        foreach (var menu in menus)
        {
            this.DrawMenuSidebarItem(menu);
        }
    }

    private void DrawMenuSidebarItem(IControllableTweakMenu menu)
    {
        var tweak = (TweakBase)menu;
        var snapshot = menu.GetMenuSnapshot();
        var actions = menu.GetMenuActions();
        var selected = menu.MenuId.Equals(this.selectedMenuId, StringComparison.OrdinalIgnoreCase);
        var availableActions = actions.Count(action => action.Available && !action.Id.Equals("status", StringComparison.OrdinalIgnoreCase));

        if (ImGui.Selectable($"{tweak.Name}##{menu.MenuId}", selected))
        {
            this.selectedMenuId = menu.MenuId;
        }

        ImGui.TextDisabled(menu.MenuId);
        ImGui.TextColored(snapshot.Error == null ? GetStateColor(snapshot) : ErrorColor, snapshot.State);
        ImGui.SameLine();
        ImGui.TextDisabled($"{availableActions} ready / {actions.Count} actions");
        ImGui.Spacing();
    }

    private void DrawMenuReference(IControllableTweakMenu menu)
    {
        var tweak = (TweakBase)menu;
        var snapshot = menu.GetMenuSnapshot();
        var actions = menu.GetMenuActions();

        this.DrawHeader(tweak, menu, snapshot);

        if (ImGui.BeginTabBar("##xelstweaks_snd_tabs", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("Use"))
            {
                this.DrawUseTab(menu, snapshot, actions);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Actions"))
            {
                this.DrawActionsTab(menu, actions);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("IPC"))
            {
                this.DrawIpcTab(menu);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Schema"))
            {
                this.DrawSchemaTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader(TweakBase tweak, IControllableTweakMenu menu, TweakMenuSnapshot snapshot)
    {
        ImGui.TextColored(AccentColor, tweak.Name);
        ImGui.SameLine();
        ImGui.TextColored(GetStateColor(snapshot), snapshot.State);
        ImGui.TextDisabled(menu.MenuId);
        ImGui.TextWrapped(tweak.Description);
        ImGui.Separator();
    }

    private void DrawUseTab(IControllableTweakMenu menu, TweakMenuSnapshot snapshot, IReadOnlyList<TweakMenuAction> actions)
    {
        var primaryAction = actions.FirstOrDefault(action => action.Available && !action.Id.Equals("status", StringComparison.OrdinalIgnoreCase))
            ?? actions.FirstOrDefault(action => !action.Id.Equals("status", StringComparison.OrdinalIgnoreCase));

        ImGui.TextColored(AccentColor, "Live state");
        this.DrawStateTable(snapshot);
        ImGui.Spacing();

        ImGui.TextColored(AccentColor, "Common SND lines");
        this.DrawCopyableLine($"/xt menu {menu.MenuId} status");
        this.DrawCopyableLine($"/xt menu {menu.MenuId} actions");
        this.DrawCopyableLine($"yield(\"/xt menu {menu.MenuId} status\")");

        if (primaryAction != null)
        {
            this.DrawCopyableLine($"yield(\"/xt menu {menu.MenuId} {primaryAction.Id}\")");
        }

        ImGui.Spacing();
        ImGui.TextColored(AccentColor, "Result shape");
        ImGui.TextWrapped(ResultSchema);
    }

    private void DrawActionsTab(IControllableTweakMenu menu, IReadOnlyList<TweakMenuAction> actions)
    {
        if (ImGui.BeginTable("##xelstweaks_snd_actions", 4, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
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
        if (ImGui.BeginTable("##xelstweaks_snd_ipc", 4, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
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
        [
            ("success", "bool", "Whether the action ran."),
            ("message", "string", "Result or failure reason."),
            ("snapshot", "object", "Updated snapshot after the action.")
        ]);
    }

    private void DrawStateTable(TweakMenuSnapshot snapshot)
    {
        if (!ImGui.BeginTable("##xelstweaks_snd_state", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        this.DrawStateRow("state", snapshot.State, GetStateColor(snapshot));
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
        this.DrawCopyableLine($"/xt menu {menu.MenuId} {action.Id}");
        this.DrawCopyableLine($"yield(\"/xt menu {menu.MenuId} {action.Id}\")");
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

    private void DrawSchemaTable(IReadOnlyList<(string Field, string Type, string Description)> rows)
    {
        if (!ImGui.BeginTable("##schema", 3, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
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
        var remainingWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - CopyButtonWidth);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + remainingWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopID();
    }

    private static Vector4 GetStateColor(TweakMenuSnapshot snapshot)
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
}
