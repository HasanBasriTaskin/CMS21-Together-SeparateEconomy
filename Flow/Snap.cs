using System;
using System.Security.Cryptography;
using UnhollowerBaseLib;

namespace TogetherSeparateEconomy
{
    /// <summary>Game-state helpers: serialize / deserialize inventory + warehouse, on-screen messages.</summary>
    public static class Snap
    {
        public static GameManager GM { get { return GameManager.Instance; } }

        public static ProfileData Profile
        {
            get
            {
                var gm = GM;
                if (gm == null) return null;
                var gdm = gm.GameDataManager;
                return gdm == null ? null : gdm.CurrentProfileData;
            }
        }

        public static bool GameLoaded
        {
            get
            {
                try
                {
                    var gm = GM;
                    return gm != null && gm.Inventory != null && gm.Warehouse != null && Profile != null
                        && Profile.inventoryData != null;
                }
                catch (Exception) { return false; }
            }
        }

        // ---------------- (de)serialization ----------------
        public static byte[] SerializeInventory(NewInventoryData d, byte ver)
        {
            if (d == null) return new byte[0];
            var ms = new Il2CppSystem.IO.MemoryStream();
            var bw = new Il2CppSystem.IO.BinaryWriter(ms);
            d.Serialize(bw, ver);
            bw.Flush();
            byte[] r = ms.ToArray();
            return r;
        }

        public static byte[] SerializeWarehouse(NewWarehouseData d, byte ver)
        {
            if (d == null) return new byte[0];
            var ms = new Il2CppSystem.IO.MemoryStream();
            var bw = new Il2CppSystem.IO.BinaryWriter(ms);
            d.Serialize(bw, ver);
            bw.Flush();
            byte[] r = ms.ToArray();
            return r;
        }

        public static NewInventoryData DeserializeInventory(byte[] bytes, byte ver)
        {
            var ms = new Il2CppSystem.IO.MemoryStream((Il2CppStructArray<byte>)bytes);
            var br = new Il2CppSystem.IO.BinaryReader(ms);
            var d = new NewInventoryData();
            d.Deserialize(br, ver);
            return d;
        }

        public static NewWarehouseData DeserializeWarehouse(byte[] bytes, byte ver)
        {
            var ms = new Il2CppSystem.IO.MemoryStream((Il2CppStructArray<byte>)bytes);
            var br = new Il2CppSystem.IO.BinaryReader(ms);
            var d = new NewWarehouseData();
            d.Deserialize(br, ver);
            return d;
        }

        // ---------------- capture ----------------
        /// <summary>Calls Inventory.Save()/Warehouse.Save() and returns the serialized profile parts. Throws if the game is not ready.</summary>
        public static void Capture(out byte[] inv, out byte[] wh, out byte ver, out int money, bool includeWarehouse = true)
        {
            var gm = GM;
            if (!GameLoaded) throw new InvalidOperationException("game profile not ready");
            if (Config.SeparateInventory) gm.Inventory.Save();
            if (Config.SeparateWarehouse && includeWarehouse) gm.Warehouse.Save();
            var prof = Profile;
            ver = prof.saveVersion;
            inv = Config.SeparateInventory ? SerializeInventory(prof.inventoryData, ver) : new byte[0];
            wh = (Config.SeparateWarehouse && includeWarehouse) ? SerializeWarehouse(prof.warehouseData, ver) : new byte[0];
            money = GlobalData.PlayerMoney;
        }

        public static string Hash(byte[] inv, byte[] wh, int money)
        {
            using (var sha = SHA1.Create())
            {
                sha.TransformBlock(inv, 0, inv.Length, null, 0);
                sha.TransformBlock(wh, 0, wh.Length, null, 0);
                var m = BitConverter.GetBytes(money);
                sha.TransformFinalBlock(m, 0, m.Length);
                return BitConverter.ToString(sha.Hash);
            }
        }

        public static int CountParts(NewInventoryData d)
        {
            if (d == null) return 0;
            int n = 0;
            if (d.items != null) n += d.items.Count;
            if (d.groups != null) n += d.groups.Count;
            return n;
        }

        // ---------------- UI ----------------
        /// <summary>Non-blocking popup when possible (localized via L), always logged (English).</summary>
        public static void Say(string key, bool warn = false, params object[] args)
        {
            string logText = L.En(key, args);
            if (warn) Log.Warn(logText); else Log.Msg(logText);
            try
            {
                var ui = UIManager.Get();
                if (ui != null) ui.ShowPopup("Together Separate Economy", L.T(key, args), PopupType.Normal);
            }
            catch (Exception e) { Log.Debug("could not show popup: " + e.Message); }
        }
    }
}
