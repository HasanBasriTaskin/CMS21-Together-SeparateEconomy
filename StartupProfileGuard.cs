using System;
using System.IO;
using CMS21Together.ClientSide;
using CMS21Together.ServerSide;
using UnityEngine;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>
    /// Together stores its extra slot (4) in "selectedProfile"; the vanilla profile array is shorter,
    /// so loading crashes (~80% of the first load). The game reads the value through GameManager.Instance.RDGPlayerPrefs
    /// and ProfileManager.selectedProfile, so all three stores are fixed.
    /// </summary>
    public static class StartupProfileGuard
    {
        const string Key = "selectedProfile";

        /// <summary>Early check: raw PlayerPrefs only (GameManager does not exist yet).</summary>
        public static void Check()
        {
            try
            {
                if (!Config.StartupCrashGuard) return;
                int v = PlayerPrefs.GetInt(Key, 0);
                if (v < 0 || v > 3)
                {
                    int slot = PickSlot();
                    Log.Warn("selectedProfile " + v + " -> " + slot + " (startup crash guard)");
                    PlayerPrefs.SetInt(Key, slot);
                    PlayerPrefs.Save();
                }
            }
            catch (Exception e) { Log.Error("StartupProfileGuard error: " + e.Message); }
        }

        /// <summary>Full check (wrapper + PlayerPrefs + ProfileManager). skipIfSession: do nothing while Together hosts/joins.</summary>
        public static void CheckFull(bool skipIfSession)
        {
            try
            {
                if (!Config.StartupCrashGuard) return;
                if (skipIfSession && SessionActive()) return;

                GameManager gm = null;
                try { gm = GameManager.Instance; } catch (Exception) { gm = null; }
                var rdg = gm != null ? gm.RDGPlayerPrefs : null;
                var pmgr = gm != null ? gm.ProfileManager : null;

                int pp = PlayerPrefs.GetInt(Key, 0);
                bool ppBad = pp < 0 || pp >= 4;
                int sel = rdg != null ? rdg.GetInt(Key, 0) : pp;
                bool rdgBad = rdg != null && (sel < 0 || sel >= 4);
                int pm = pmgr != null ? pmgr.selectedProfile : -1;
                bool pmBad = pm >= 4 || pm < -1;
                if (!ppBad && !rdgBad && !pmBad) return;

                int slot = PickSlot();
                int old = ppBad ? pp : (rdgBad ? sel : pm);
                if (old != _lastLogged || slot != _lastSlot)
                {
                    _lastLogged = old; _lastSlot = slot;
                    Log.Warn("selectedProfile " + old + " -> " + slot + " (startup crash guard)");
                }
                if (rdgBad) { rdg.SetInt(Key, slot); rdg.Save(); }
                if (ppBad) { PlayerPrefs.SetInt(Key, slot); PlayerPrefs.Save(); }
                if (pmBad) pmgr.selectedProfile = slot;
            }
            catch (Exception e) { Log.Error("StartupProfileGuard.CheckFull error: " + e.Message); }
        }

        static int _lastLogged = int.MinValue, _lastSlot = -1;

        static bool SessionActive()
        {
            try
            {
                var s = Server.Instance;
                if (s != null && s.isRunning) return true;
                var c = Client.Instance;
                if (c != null && c.isConnected) return true;
            }
            catch (Exception e) { Log.Error("Session check error: " + e.Message); }
            return false;
        }

        static string SaveDir()
        {
            try
            {
                string d = GlobalStrings.SaveDirectory;
                if (!string.IsNullOrEmpty(d)) return d;
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "AppData", "LocalLow",
                "Red Dot Games", "Car Mechanic Simulator 2021", "Save");
        }

        /// <summary>Vanilla slot 0-3 whose save file is newest; 0 when none exists.</summary>
        static int PickSlot()
        {
            int best = 0;
            try
            {
                string dir = SaveDir();
                DateTime bestT = DateTime.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    foreach (var ext in new[] { ".cms21b", ".cms21" })
                    {
                        string f = Path.Combine(dir, "profile" + i + ext);
                        if (!File.Exists(f)) continue;
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > bestT) { bestT = t; best = i; }
                    }
                }
            }
            catch (Exception e) { Log.Error("Profile slot selection error: " + e.Message); }
            return best;
        }
    }

    /// <summary>Harmony safety net: fix selectedProfile right before the game loads its data.</summary>
    public class StartupProfilePatches : IPatchGroup
    {
        public string Name { get { return "StartupProfilePatches"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "GameDataManager.Load(PlatformManager)", typeof(GameDataManager), "Load",
                new[] { typeof(CMS.Platforms.PlatformManager) }, "StartupProfilePatches.Guard", null);
            PatchUtil.Do(h, "GameDataManager.LoadProfile()", typeof(GameDataManager), "LoadProfile",
                new Type[0], "StartupProfilePatches.Guard", null);
        }

        public static void Guard()
        {
            try { StartupProfileGuard.CheckFull(true); }
            catch (Exception e) { Log.Error("StartupProfilePatches: " + e.Message); }
        }
    }
}
