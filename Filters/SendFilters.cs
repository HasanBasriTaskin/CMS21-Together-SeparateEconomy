using System;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.ClientSide.Data.Player;
using CMS21Together.Shared.Data.Vanilla;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>Common helper for resolving a patch target and logging when it is missing.</summary>
    static class PatchUtil
    {
        public static bool Do(Hx h, string label, Type t, string method, Type[] args, string prefix, string postfix, bool last = false)
        {
            var m = args == null ? AccessTools.Method(t, method) : AccessTools.Method(t, method, args);
            if (m == null) { Log.Error("Patch target not found: " + label); return false; }
            var host = typeof(PatchUtil).Assembly;
            HarmonyMethod pre = null, post = null;
            if (prefix != null) pre = new HarmonyMethod(FindPatch(prefix)) { priority = Priority.First };
            if (postfix != null) post = new HarmonyMethod(FindPatch(postfix)) { priority = last ? Priority.Last : Priority.Normal };
            h.Patch(m, prefix: pre, postfix: post);
            Log.Debug("Patched: " + label);
            return true;
        }

        static System.Reflection.MethodInfo FindPatch(string full)
        {
            int i = full.LastIndexOf('.');
            var t = typeof(PatchUtil).Assembly.GetType("TogetherSeparateEconomy." + full.Substring(0, i), true);
            var mi = t.GetMethod(full.Substring(i + 1), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi == null) throw new MissingMethodException(full);
            return mi;
        }
    }

    /// <summary>Filter 1: ClientSend.ItemPacket / GroupItemPacket.</summary>
    public class InventorySendFilter : IPatchGroup
    {
        public string Name { get { return "InventorySendFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "ClientSend.ItemPacket(ModItem,InventoryAction)", typeof(ClientSend), "ItemPacket",
                new[] { typeof(ModItem), typeof(InventoryAction) }, "InventorySendFilter.Block", null);
            PatchUtil.Do(h, "ClientSend.GroupItemPacket(ModGroupItem,InventoryAction)", typeof(ClientSend), "GroupItemPacket",
                new[] { typeof(ModGroupItem), typeof(InventoryAction) }, "InventorySendFilter.Block", null);
        }

        public static bool Block()
        {
            try
            {
                if (Separation.Active && Config.SeparateInventory) { Log.Debug("Inventory packet not sent"); return false; }
            }
            catch (Exception e) { Log.Error("InventorySendFilter: " + e.Message); }
            return true;
        }
    }

    /// <summary>Filter 2: ClientSend.StatPacket (money only).</summary>
    public class MoneySendFilter : IPatchGroup
    {
        public string Name { get { return "MoneySendFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "ClientSend.StatPacket(int,ModStats,bool)", typeof(ClientSend), "StatPacket",
                new[] { typeof(int), typeof(ModStats), typeof(bool) }, "MoneySendFilter.Block", null);
        }

        public static bool Block(ModStats type)
        {
            try
            {
                if (type == ModStats.money && Separation.Active && Config.SeparateMoney) { Log.Debug("Money packet not sent"); return false; }
            }
            catch (Exception e) { Log.Error("MoneySendFilter: " + e.Message); }
            return true;
        }
    }

    /// <summary>Filter 3: Stats.UpdateStats(ModStats,int,bool) - remote money updates become a no-op coroutine.</summary>
    public class MoneyReceiveFilter : IPatchGroup
    {
        public string Name { get { return "MoneyReceiveFilter"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "Stats.UpdateStats(ModStats,int,bool)", typeof(Stats), "UpdateStats",
                new[] { typeof(ModStats), typeof(int), typeof(bool) }, "MoneyReceiveFilter.Block", null);
        }

        public static bool Block(ModStats type, ref System.Collections.IEnumerator __result)
        {
            try
            {
                if (type == ModStats.money && Separation.Active && Config.SeparateMoney)
                {
                    __result = new object[0].GetEnumerator();
                    Log.Debug("Remote money update ignored");
                    return false;
                }
            }
            catch (Exception e) { Log.Error("MoneyReceiveFilter: " + e.Message); }
            return true;
        }
    }
}
