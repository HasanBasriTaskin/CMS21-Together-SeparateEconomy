using System.IO;
using MelonLoader;

namespace TogetherSeparateEconomy
{
    public static class Config
    {
        static MelonPreferences_Category _cat;
        static MelonPreferences_Entry<bool> _inventory, _warehouse, _money, _debug, _crashGuard;
        static MelonPreferences_Entry<string> _lang;
        static MelonPreferences_Entry<int> _interval, _backup, _newMoney, _newLevel;

        // Session-only overrides (host values win for these three; never written to the cfg file).
        static bool? _oInventory, _oWarehouse, _oMoney;

        public static bool SeparateInventory { get { return _oInventory ?? (_inventory != null && _inventory.Value); } }
        public static bool SeparateWarehouse { get { return _oWarehouse ?? (_warehouse != null && _warehouse.Value); } }
        public static bool SeparateMoney { get { return _oMoney ?? (_money != null && _money.Value); } }
        public static int SnapshotIntervalSeconds { get { return _interval == null ? 60 : System.Math.Max(5, _interval.Value); } }
        public static int BackupCount { get { return _backup == null ? 30 : System.Math.Max(1, _backup.Value); } }
        public static int NewPlayerMoney { get { return _newMoney == null ? 15000 : System.Math.Max(0, _newMoney.Value); } }
        public static int NewPlayerLevel { get { return _newLevel == null ? 5 : System.Math.Max(0, _newLevel.Value); } }
        public static bool StartupCrashGuard { get { return _crashGuard == null || _crashGuard.Value; } }
        public static string Language { get { string v = _lang == null ? "en" : _lang.Value; return string.Equals((v ?? "").Trim(), "tr", System.StringComparison.OrdinalIgnoreCase) ? "tr" : "en"; } }
        public static bool DebugLog { get { return _debug != null && _debug.Value; } }

        /// <summary>Game root (folder of the exe).</summary>
        public static string GameDir { get { return MelonUtils.BaseDirectory; } }
        /// <summary>Mods\TogetherSeparateEconomy</summary>
        public static string ModDir { get { return Path.Combine(GameDir, "Mods", "TogetherSeparateEconomy"); } }

        public static void Load()
        {
            Directory.CreateDirectory(ModDir);
            _cat = MelonPreferences.CreateCategory("TogetherSeparateEconomy", "Together Separate Economy");
            _cat.SetFilePath(Path.Combine(ModDir, "TogetherSeparateEconomy.cfg"));
            _inventory = _cat.CreateEntry("SeparateInventory", true, "Each player has their own inventory");
            _warehouse = _cat.CreateEntry("SeparateWarehouse", true, "Each player has their own warehouse");
            _money = _cat.CreateEntry("SeparateMoney", true, "Each player has their own money");
            _interval = _cat.CreateEntry("SnapshotIntervalSeconds", 60, "Automatic snapshot interval (seconds, minimum 5)");
            _backup = _cat.CreateEntry("BackupCount", 30, "Number of rolling backups kept per player (host)");
            _newMoney = _cat.CreateEntry("NewPlayerMoney", 15000, "Starting money of a brand-new player");
            _newLevel = _cat.CreateEntry("NewPlayerLevel", 5, "Starting level of a brand-new player");
            _debug = _cat.CreateEntry("DebugLog", false, "Verbose logging");
            _crashGuard = _cat.CreateEntry("StartupCrashGuard", true, "Reset an invalid selectedProfile value to a vanilla slot (startup crash guard)");
            _lang = _cat.CreateEntry("Language", "en", "Popup language: en or tr");
            _cat.SaveToFile(false);
        }

        public static void ClearOverrides() { _oInventory = _oWarehouse = _oMoney = null; }

        /// <summary>
        /// Parses the output of Summary() ("inventory=1;warehouse=1;money=1") and applies it as a session override.
        /// Returns true when the parsed values differ from the local file values.
        /// </summary>
        public static bool ApplyHostSummary(string summary)
        {
            bool? e = null, d = null, p = null;
            if (!string.IsNullOrEmpty(summary))
            {
                foreach (var part in summary.Split(';'))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2) continue;
                    bool v = kv[1].Trim() == "1";
                    switch (kv[0].Trim().ToLowerInvariant())
                    {
                        case "inventory": e = v; break;
                        case "warehouse": d = v; break;
                        case "money": p = v; break;
                    }
                }
            }
            bool differs = (e.HasValue && e.Value != (_inventory != null && _inventory.Value))
                        || (d.HasValue && d.Value != (_warehouse != null && _warehouse.Value))
                        || (p.HasValue && p.Value != (_money != null && _money.Value));
            _oInventory = e; _oWarehouse = d; _oMoney = p;
            return differs;
        }

        /// <summary>Short text exchanged in the handshake; hosts and clients should have the same one.</summary>
        public static string Summary()
        {
            return "inventory=" + (SeparateInventory ? 1 : 0) + ";warehouse=" + (SeparateWarehouse ? 1 : 0) + ";money=" + (SeparateMoney ? 1 : 0);
        }
    }
}
