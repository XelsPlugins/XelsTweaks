using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace XelsTweaks.Tweaks.Interface;

internal sealed unsafe class TryOnCopyItemNameTweak : TweakBase
{
    public const string TweakId = "interface.tryOnCopyItemName";

    private const string InventoryKey = "inventory";
    private const string ArmouryChestKey = "armouryChest";
    private const string ChocoboSaddlebagKey = "chocoboSaddlebag";
    private const string FittingRoomKey = "fittingRoom";
    private const string RetainerInventoryKey = "retainerInventory";
    private const string FreeCompanyChestKey = "freeCompanyChest";
    private const string HousingStorageKey = "housingStorage";
    private const string OtherInventoryKey = "otherInventory";
    private const string ChatPrefix = "[XelsTweaks]";
    private const ulong MaxHoveredItemId = 2_000_000;
    private const ulong ItemIdModulo = 500_000;

    private static readonly string[] TryOnAddonNames = ["Tryon", "FittingShop"];
    private static readonly SeString CopyItemNameLabel = "Copy Item Name";
    private static readonly IReadOnlyList<TweakOptionDefinition> CommandOptions =
    [
        TweakOptionDefinition.Bool(
            InventoryKey,
            "Inventory",
            "Shows Copy Item Name in regular inventory item menus.",
            true,
            "Item sources"),
        TweakOptionDefinition.Bool(
            ArmouryChestKey,
            "Armoury Chest",
            "Shows Copy Item Name in armoury chest item menus.",
            true,
            "Item sources"),
        TweakOptionDefinition.Bool(
            ChocoboSaddlebagKey,
            "Chocobo Saddlebag",
            "Shows Copy Item Name in chocobo saddlebag item menus.",
            true,
            "Item sources"),
        TweakOptionDefinition.Bool(
            FittingRoomKey,
            "Fitting Room",
            "Shows Copy Item Name in fitting room item menus.",
            true,
            "Item sources"),
        TweakOptionDefinition.Bool(
            RetainerInventoryKey,
            "Retainer Inventory",
            "Shows Copy Item Name in retainer inventory item menus.",
            false,
            "Item sources"),
        TweakOptionDefinition.Bool(
            FreeCompanyChestKey,
            "Free Company Chest",
            "Shows Copy Item Name in free company chest item menus.",
            false,
            "Item sources"),
        TweakOptionDefinition.Bool(
            HousingStorageKey,
            "Housing Storage",
            "Shows Copy Item Name in housing storage item menus.",
            false,
            "Item sources"),
        TweakOptionDefinition.Bool(
            OtherInventoryKey,
            "Other item windows",
            "Shows Copy Item Name in other supported item menus.",
            false,
            "Item sources")
    ];

    private ulong? cachedHoverItemId;

    public TryOnCopyItemNameTweak(DalamudServices services, TweakState state, System.Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Copy Item Names";
    public override string Description => "Adds Copy Item Name to supported item right-click menus.";
    public override TweakCategory Category => TweakCategory.Interface;
    public override bool DrawConfigWhenDisabled => true;
    public override IReadOnlyList<TweakOptionDefinition> Options => CommandOptions;

    private bool EnableInventory => this.GetBool(InventoryKey, true);
    private bool EnableArmouryChest => this.GetBool(ArmouryChestKey, true);
    private bool EnableChocoboSaddlebag => this.GetBool(ChocoboSaddlebagKey, true);
    private bool EnableFittingRoom => this.GetBool(FittingRoomKey, true);
    private bool EnableRetainerInventory => this.GetBool(RetainerInventoryKey, false);
    private bool EnableFreeCompanyChest => this.GetBool(FreeCompanyChestKey, false);
    private bool EnableHousingStorage => this.GetBool(HousingStorageKey, false);
    private bool EnableOtherInventory => this.GetBool(OtherInventoryKey, false);

    public override bool DrawConfig()
    {
        var changed = false;
        ImGui.TextWrapped("Show the menu option in:");
        changed |= this.DrawBool("Inventory", InventoryKey, true);
        changed |= this.DrawBool("Armoury Chest", ArmouryChestKey, true);
        changed |= this.DrawBool("Chocobo Saddlebag", ChocoboSaddlebagKey, true);
        changed |= this.DrawBool("Fitting Room", FittingRoomKey, true);
        changed |= this.DrawBool("Retainer Inventory", RetainerInventoryKey, false);
        changed |= this.DrawBool("Free Company Chest", FreeCompanyChestKey, false);
        changed |= this.DrawBool("Housing Storage", HousingStorageKey, false);
        changed |= this.DrawBool("Other item windows", OtherInventoryKey, false);
        return changed;
    }

    protected override void OnEnable()
    {
        this.Services.GameGui.HoveredItemChanged += this.OnHoveredItemChanged;
        this.Services.ContextMenu.OnMenuOpened += this.OnMenuOpened;
    }

    protected override void OnDisable()
    {
        this.Services.ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        this.Services.GameGui.HoveredItemChanged -= this.OnHoveredItemChanged;
        this.cachedHoverItemId = null;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!this.TryGetContextItem(args, out var itemId, out var source)
            || !this.IsSourceEnabled(source)
            || !this.TryGetItemName(itemId, out var itemName))
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = CopyItemNameLabel,
            PrefixChar = 'C',
            OnClicked = _ => this.CopyItemName(itemName),
        });
    }

    private void OnHoveredItemChanged(object? sender, ulong itemId)
    {
        if (itemId != 0)
        {
            this.cachedHoverItemId = itemId;
        }
    }

    private bool DrawBool(string label, string key, bool defaultValue)
    {
        var value = this.GetBool(key, defaultValue);
        if (!ImGui.Checkbox(label, ref value))
        {
            return false;
        }

        this.SetBool(key, value);
        return true;
    }

    private bool IsFittingRoomContext(IMenuOpenedArgs args)
    {
        if (IsTryOnAddon(args.AddonName))
        {
            return true;
        }

        if (args.MenuType != ContextMenuType.Inventory || args.AgentPtr == IntPtr.Zero)
        {
            return false;
        }

        var inventoryContext = (AgentInventoryContext*)args.AgentPtr;
        foreach (var addonName in TryOnAddonNames)
        {
            var tryOnAddon = this.Services.GameGui.GetAddonByName(addonName, 1);
            if (tryOnAddon.IsNull || tryOnAddon.Address == IntPtr.Zero)
            {
                continue;
            }

            if (args.AddonPtr == tryOnAddon.Address || inventoryContext->OwnerAddonId == tryOnAddon.Id)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTryOnAddon(string? addonName)
    {
        return !string.IsNullOrEmpty(addonName)
            && TryOnAddonNames.Any(name => addonName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetContextItem(IMenuOpenedArgs args, out uint itemId, out ItemContextSource source)
    {
        var isFittingRoomContext = this.IsFittingRoomContext(args);

        if (args.Target is MenuTargetInventory target)
        {
            var targetItem = target.TargetItem;
            if (targetItem is { IsEmpty: false })
            {
                itemId = targetItem.Value.BaseItemId;
                source = isFittingRoomContext
                    ? ItemContextSource.FittingRoom
                    : ClassifyInventoryType(targetItem.Value.ContainerType);
                return itemId != 0;
            }
        }

        if (TryGetDummyTargetItem(args, out itemId, out source))
        {
            if (isFittingRoomContext)
            {
                source = ItemContextSource.FittingRoom;
            }

            return true;
        }

        if (isFittingRoomContext && this.TryGetHoveredItem(args, out itemId))
        {
            source = ItemContextSource.FittingRoom;
            return true;
        }

        source = ItemContextSource.OtherInventory;
        return false;
    }

    private static bool TryGetDummyTargetItem(IMenuOpenedArgs args, out uint itemId, out ItemContextSource source)
    {
        itemId = 0;
        source = ItemContextSource.OtherInventory;

        if (args.MenuType != ContextMenuType.Inventory || args.AgentPtr == IntPtr.Zero)
        {
            return false;
        }

        var inventoryContext = (AgentInventoryContext*)args.AgentPtr;
        var dummyItem = &inventoryContext->TargetDummyItem;
        if (dummyItem->IsEmpty())
        {
            return false;
        }

        itemId = dummyItem->GetBaseItemId();
        source = ClassifyInventoryType((GameInventoryType)inventoryContext->TargetInventoryId);
        return itemId != 0;
    }

    private bool TryGetHoveredItem(IMenuOpenedArgs args, out uint itemId)
    {
        var hoveredItemId = this.Services.GameGui.HoveredItem;
        if (hoveredItemId == 0 && this.cachedHoverItemId != null && IsTryOnAddon(args.AddonName))
        {
            hoveredItemId = this.cachedHoverItemId.Value;
            this.cachedHoverItemId = null;
        }

        if (hoveredItemId == 0 || hoveredItemId >= MaxHoveredItemId)
        {
            itemId = 0;
            return false;
        }

        itemId = (uint)(hoveredItemId % ItemIdModulo);
        return itemId != 0;
    }

    private bool IsSourceEnabled(ItemContextSource source)
    {
        return source switch
        {
            ItemContextSource.Inventory => this.EnableInventory,
            ItemContextSource.ArmouryChest => this.EnableArmouryChest,
            ItemContextSource.ChocoboSaddlebag => this.EnableChocoboSaddlebag,
            ItemContextSource.FittingRoom => this.EnableFittingRoom,
            ItemContextSource.RetainerInventory => this.EnableRetainerInventory,
            ItemContextSource.FreeCompanyChest => this.EnableFreeCompanyChest,
            ItemContextSource.HousingStorage => this.EnableHousingStorage,
            ItemContextSource.OtherInventory => this.EnableOtherInventory,
            _ => false,
        };
    }

    private static ItemContextSource ClassifyInventoryType(GameInventoryType inventoryType)
    {
        return inventoryType switch
        {
            GameInventoryType.Inventory1
                or GameInventoryType.Inventory2
                or GameInventoryType.Inventory3
                or GameInventoryType.Inventory4 => ItemContextSource.Inventory,
            GameInventoryType.ArmoryOffHand
                or GameInventoryType.ArmoryHead
                or GameInventoryType.ArmoryBody
                or GameInventoryType.ArmoryHands
                or GameInventoryType.ArmoryWaist
                or GameInventoryType.ArmoryLegs
                or GameInventoryType.ArmoryFeets
                or GameInventoryType.ArmoryEar
                or GameInventoryType.ArmoryNeck
                or GameInventoryType.ArmoryWrist
                or GameInventoryType.ArmoryRings
                or GameInventoryType.ArmorySoulCrystal
                or GameInventoryType.ArmoryMainHand => ItemContextSource.ArmouryChest,
            GameInventoryType.SaddleBag1
                or GameInventoryType.SaddleBag2
                or GameInventoryType.PremiumSaddleBag1
                or GameInventoryType.PremiumSaddleBag2 => ItemContextSource.ChocoboSaddlebag,
            >= GameInventoryType.RetainerPage1 and <= GameInventoryType.RetainerPage7 => ItemContextSource.RetainerInventory,
            GameInventoryType.RetainerEquippedItems => ItemContextSource.RetainerInventory,
            >= GameInventoryType.FreeCompanyPage1 and <= GameInventoryType.FreeCompanyPage5 => ItemContextSource.FreeCompanyChest,
            >= GameInventoryType.HousingExteriorAppearance and <= GameInventoryType.HousingInteriorPlacedItems12 => ItemContextSource.HousingStorage,
            GameInventoryType.HousingExteriorPlacedItems2 => ItemContextSource.HousingStorage,
            >= GameInventoryType.HousingExteriorStoreroom and <= GameInventoryType.HousingExteriorStoreroom2 => ItemContextSource.HousingStorage,
            _ => ItemContextSource.OtherInventory,
        };
    }

    private bool TryGetItemName(uint itemId, out string itemName)
    {
        var itemSheet = this.Services.DataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var itemRow))
        {
            itemName = string.Empty;
            return false;
        }

        itemName = itemRow.Name.ToString();
        return !string.IsNullOrWhiteSpace(itemName);
    }

    private void CopyItemName(string itemName)
    {
        ImGui.SetClipboardText(itemName);
        this.Services.ChatGui.Print($"{ChatPrefix} Copied item name: {itemName}");
    }

    private enum ItemContextSource
    {
        Inventory,
        ArmouryChest,
        ChocoboSaddlebag,
        FittingRoom,
        RetainerInventory,
        FreeCompanyChest,
        HousingStorage,
        OtherInventory,
    }
}
