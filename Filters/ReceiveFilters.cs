using System;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>Filter 4: ClientHandle.ItemPacket / GroupItemPacket (incoming remote inventory changes).</summary>
    public class InventoryReceiveFilter : IPatchGroup
    {
        public string Name { get { return "InventoryReceiveFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "ClientHandle.ItemPacket(Packet)", typeof(ClientHandle), "ItemPacket",
                new[] { typeof(Packet) }, "InventoryReceiveFilter.Block", null);
            PatchUtil.Do(h, "ClientHandle.GroupItemPacket(Packet)", typeof(ClientHandle), "GroupItemPacket",
                new[] { typeof(Packet) }, "InventoryReceiveFilter.Block", null);
        }

        public static bool Block()
        {
            try
            {
                Separation.NoteInventoryPacket();
                if (Separation.Active && Config.SeparateInventory) { Log.Debug("Incoming inventory packet ignored"); return false; }
            }
            catch (Exception e) { Log.Error("InventoryReceiveFilter: " + e.Message); }
            return true;
        }
    }

    /// <summary>
    /// Filter 7: Together's own Harmony prefix Inventory.LoadHook(Inventory) (itself the prefix of the game's Inventory.Load).
    /// </summary>
    public class LoadHookFilter : IPatchGroup
    {
        public string Name { get { return "LoadHookFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "Together Inventory.LoadHook(Inventory)", typeof(CMS21Together.ClientSide.Data.Player.Inventory), "LoadHook",
                new[] { typeof(Inventory) }, "LoadHookFilter.Prefix", null);
        }

        public static bool Prefix(ref bool __result)
        {
            try
            {
                if (Main.Disabled) return true;
                if (Separation.Applying) { __result = true; return false; }
                if (Separation.ClientActive && Config.SeparateInventory && Net.IsClient)
                {
                    __result = false;   // skip vanilla load: keep the in-memory inventory (scene changes)
                    Log.Debug("Inventory.Load skipped (keeping in-memory inventory)");
                    return false;
                }
            }
            catch (Exception e) { Log.Error("LoadHookFilter: " + e.Message); }
            return true;
        }
    }

    /// <summary>Disconnect: capture the player's record synchronously and leave separated mode.</summary>
    public class DisconnectFilter : IPatchGroup
    {
        public string Name { get { return "DisconnectFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "Client.Disconnect(bool)", typeof(CMS21Together.ClientSide.Client), "Disconnect",
                new[] { typeof(bool) }, "DisconnectFilter.Prefix", null);
        }

        public static void Prefix()
        {
            try { ClientFlow.OnDisconnecting(); }
            catch (Exception e) { Log.Error("DisconnectFilter: " + e.Message); }
        }
    }
}
