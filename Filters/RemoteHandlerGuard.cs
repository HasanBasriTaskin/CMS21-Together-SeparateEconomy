using System;
using System.Reflection;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>
    /// Filter 5 (revised). Instead of replacing Client.PacketHandlers entries (FixForTogether re-wraps entries that differ
    /// from its own - SChangeWhitelist.WrapBlockedHandlers - and flags a collision for id 200), we hook the three transport
    /// lambdas that invoke the handler dictionary on the main thread (ClientTCP/ClientSteam/ClientUDP) and raise RemoteDepth
    /// around them. Packets 68 (bulk sell) and 200 (inventory sync) are dropped by prefixing Fixes' own ClientReceive methods.
    /// </summary>
    public class RemoteHandlerGuard : IPatchGroup, IModPart
    {
        public string Name { get { return "RemoteHandlerGuard"; } }

        static bool _fixesPatched68, _fixesPatched200;
        static int _tries;
        static double _next;

        public void Apply(Hx h)
        {
            string[] types =
            {
                "CMS21Together.ClientSide.Transports.ClientTCP",
                "CMS21Together.ClientSide.Transports.ClientSteam",
                "CMS21Together.ClientSide.Transports.ClientUDP",
            };
            int patched = 0;
            foreach (var tn in types)
            {
                var t = typeof(CMS21Together.ClientSide.Client).Assembly.GetType(tn, false);
                if (t == null) { Log.Error("Patch target not found: " + tn); continue; }
                foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (var m in nt.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!(m.Name.StartsWith("<HandleData>b__") || m.Name.StartsWith("<OnMessage>b__"))) continue;
                        h.Patch(m,
                            prefix: new HarmonyMethod(typeof(RemoteHandlerGuard).GetMethod("Enter")) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(RemoteHandlerGuard).GetMethod("Exit")) { priority = Priority.Last });
                        patched++;
                        Log.Debug("Patched: " + tn + "/" + nt.Name + "." + m.Name);
                    }
                }
            }
            if (patched == 0) Log.Error("Remote packet handler hook not found: remote money guard will not work");
            TryPatchFixes(h);
        }

        public static void Enter() { Separation.RemoteDepth++; }
        public static void Exit() { if (Separation.RemoteDepth > 0) Separation.RemoteDepth--; }

        public static bool DropInventoryPacket()
        {
            try
            {
                Separation.NoteInventoryPacket();
                if (Separation.Active && Config.SeparateInventory) { Log.Debug("Fixes inventory packet ignored"); return false; }
            }
            catch (Exception e) { Log.Error("DropInventoryPacket: " + e.Message); }
            return true;
        }

        static MethodBase FindFixes(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(typeName, false); } catch (Exception) { continue; }
                if (t == null) continue;
                return AccessTools.Method(t, "ClientReceive");
            }
            return null;
        }

        static void TryPatchFixes(Hx h)
        {
            try
            {
                if (!_fixesPatched68)
                {
                    var m = FindFixes("FixForTogether.InventoryBulkSellPacket68");
                    if (m != null) { h.Patch(m, prefix: new HarmonyMethod(typeof(RemoteHandlerGuard).GetMethod("DropInventoryPacket")) { priority = Priority.First }); _fixesPatched68 = true; Log.Debug("Patched: Fixes packet 68"); }
                }
                if (!_fixesPatched200)
                {
                    var m = FindFixes("FixForTogether.InventorySyncPacket200");
                    if (m != null) { h.Patch(m, prefix: new HarmonyMethod(typeof(RemoteHandlerGuard).GetMethod("DropInventoryPacket")) { priority = Priority.First }); _fixesPatched200 = true; Log.Debug("Patched: Fixes packet 200"); }
                }
            }
            catch (Exception e) { Log.Error("Fixes patch failed: " + e.Message); _fixesPatched68 = _fixesPatched200 = true; }
        }

        public void Init() { }

        /// <summary>FixForTogether may load after us: retry for a minute.</summary>
        public void Update()
        {
            if (Main.Disabled || (_fixesPatched68 && _fixesPatched200) || _tries >= 12) return;
            double now = Separation.Now;
            if (now < _next) return;
            _next = now + 5;
            _tries++;
            TryPatchFixes(Main.HarmonyInst);
            if (_tries == 12 && !(_fixesPatched68 && _fixesPatched200))
                Log.Debug("FixForTogether not found (no packet 68/200 patch; not needed without it)");
        }
    }
}
