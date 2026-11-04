using System;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>Filter 8a: game Warehouse add/delete -> keep the in-memory profile current (client, SeparateWarehouse).</summary>
    public class WarehousePersist : IPatchGroup
    {
        public string Name { get { return "WarehousePersist"; } }
        static bool _saving;

        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "Warehouse.Add(Item)", typeof(Warehouse), "Add", new[] { typeof(Item) }, null, "WarehousePersist.After");
            PatchUtil.Do(h, "Warehouse.Add(GroupItem)", typeof(Warehouse), "Add", new[] { typeof(GroupItem) }, null, "WarehousePersist.After");
            PatchUtil.Do(h, "Warehouse.Delete(Item)", typeof(Warehouse), "Delete", new[] { typeof(Item) }, null, "WarehousePersist.After");
            PatchUtil.Do(h, "Warehouse.Delete(GroupItem)", typeof(Warehouse), "Delete", new[] { typeof(GroupItem) }, null, "WarehousePersist.After");
        }

        public static void After(Warehouse __instance)
        {
            try
            {
                if (Main.Disabled || !Separation.ClientActive || !Config.SeparateWarehouse || _saving) return;
                Separation.WarehouseDirty = true;
                _saving = true;
                try { if (__instance != null) __instance.Save(); }
                finally { _saving = false; }
            }
            catch (Exception e) { Log.Error("WarehousePersist: " + e.Message); }
        }
    }

    /// <summary>Filter 8b: game Inventory add/delete -> dirty flag only (Together-independent).</summary>
    public class InventoryDirty : IPatchGroup
    {
        public string Name { get { return "InventoryDirty"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "Inventory.Add(Item,bool)", typeof(Inventory), "Add", new[] { typeof(Item), typeof(bool) }, null, "InventoryDirty.After");
            PatchUtil.Do(h, "Inventory.AddGroup(GroupItem)", typeof(Inventory), "AddGroup", new[] { typeof(GroupItem) }, null, "InventoryDirty.After");
            PatchUtil.Do(h, "Inventory.Delete(Item)", typeof(Inventory), "Delete", new[] { typeof(Item) }, null, "InventoryDirty.After");
            PatchUtil.Do(h, "Inventory.DeleteGroup(long)", typeof(Inventory), "DeleteGroup", new[] { typeof(long) }, null, "InventoryDirty.After");
        }

        public static void After()
        {
            if (Separation.ClientActive) Separation.InventoryDirty = true;
        }
    }
}
