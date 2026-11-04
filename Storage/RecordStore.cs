using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TogetherSeparateEconomy
{
    /// <summary>File storage for PlayerRecords. All methods are main-thread, never throw (log + null/false).</summary>
    public static class Storage
    {
        public static string RecordsRoot { get { return Path.Combine(Config.ModDir, "Records"); } }
        public static string SaveDir(int saveIndex) { return Path.Combine(RecordsRoot, "save_" + saveIndex); }
        public static string LocalDir { get { return Path.Combine(Config.ModDir, "LocalBackup"); } }

        /// <summary>Guid is client-supplied: accept only [A-Za-z0-9-] (blocks path traversal and '_' used as backup separator).</summary>
        public static bool IsSafeGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid.Length > 64) return false;
            foreach (char c in guid)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-') return false;
            return true;
        }

        public static string HostPath(int saveIndex, string guid) { return Path.Combine(SaveDir(saveIndex), guid + ".tse"); }
        static string BackupDir(int saveIndex) { return Path.Combine(SaveDir(saveIndex), "backups"); }

        // ---------------- host ----------------

        public static PlayerRecord HostLoad(int saveIndex, string guid)
        {
            try
            {
                if (!IsSafeGuid(guid)) { Log.Warn("HostLoad: invalid guid"); return null; }
                string path = HostPath(saveIndex, guid);
                if (File.Exists(path))
                {
                    try { return PlayerRecord.FromBytes(File.ReadAllBytes(path)); }
                    catch (Exception e) { Log.Error("Record corrupt (" + path + "): " + e.Message + " - checking backups"); }
                    // fall back to newest valid backup
                    foreach (var b in ListBackups(saveIndex, guid).Reverse())
                    {
                        try
                        {
                            var r = PlayerRecord.FromBytes(File.ReadAllBytes(b));
                            Log.Warn("Record loaded from backup: " + Path.GetFileName(b));
                            return r;
                        }
                        catch (Exception) { }
                    }
                    Log.Error("No valid backup found: " + guid);
                }
                return null;
            }
            catch (Exception e) { Log.Error("HostLoad error: " + e); return null; }
        }

        /// <summary>True when the file exists but could not be read (distinguish from "no record").</summary>
        public static bool HostFileExists(int saveIndex, string guid)
        {
            return IsSafeGuid(guid) && File.Exists(HostPath(saveIndex, guid));
        }

        public static bool HostSave(int saveIndex, PlayerRecord rec)
        {
            try
            {
                if (rec == null || !IsSafeGuid(rec.Guid)) { Log.Warn("HostSave: invalid record/guid"); return false; }
                string dir = SaveDir(saveIndex);
                Directory.CreateDirectory(dir);
                string path = HostPath(saveIndex, rec.Guid);
                if (!AtomicWrite(path, rec.ToBytes())) return false;
                try { MakeBackup(saveIndex, rec.Guid, path); }
                catch (Exception e) { Log.Warn("Backup failed: " + e.Message); }
                return true;
            }
            catch (Exception e) { Log.Error("HostSave error: " + e); return false; }
        }

        static string[] ListBackups(int saveIndex, string guid)
        {
            string dir = BackupDir(saveIndex);
            if (!Directory.Exists(dir)) return new string[0];
            // names are <guid>_<yyyyMMdd-HHmmss>[_n].tse -> lexical order == chronological
            return Directory.GetFiles(dir, guid + "_*.tse").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
        }

        static void MakeBackup(int saveIndex, string guid, string source)
        {
            string dir = BackupDir(saveIndex);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dest = Path.Combine(dir, guid + "_" + stamp + ".tse");
            for (int n = 1; File.Exists(dest); n++) dest = Path.Combine(dir, guid + "_" + stamp + "_" + n + ".tse");
            File.Copy(source, dest, false);

            // keep the oldest ever + newest BackupCount
            var all = ListBackups(saveIndex, guid);
            int keep = Config.BackupCount;
            if (all.Length <= keep + 1) return;
            for (int i = 1; i < all.Length - keep; i++)
            {
                try { File.Delete(all[i]); } catch (Exception e) { Log.Debug("could not delete backup: " + e.Message); }
            }
        }

        // ---------------- client local backup ----------------

        public static string LocalPath(int saveIndex, string guid) { return Path.Combine(LocalDir, "save_" + saveIndex + "_" + guid + ".tse"); }

        public static PlayerRecord LocalLoad(int saveIndex, string guid)
        {
            try
            {
                if (!IsSafeGuid(guid)) return null;
                string path = LocalPath(saveIndex, guid);
                if (!File.Exists(path)) return null;
                try { return PlayerRecord.FromBytes(File.ReadAllBytes(path)); }
                catch (Exception e) { Log.Error("Local backup corrupt: " + e.Message); }
                for (int i = 1; i <= 4; i++)
                {
                    string b = path + ".bak" + i;
                    if (!File.Exists(b)) continue;
                    try { var r = PlayerRecord.FromBytes(File.ReadAllBytes(b)); Log.Warn("Local backup loaded from .bak" + i); return r; }
                    catch (Exception) { }
                }
                return null;
            }
            catch (Exception e) { Log.Error("LocalLoad error: " + e); return null; }
        }

        public static bool LocalSave(int saveIndex, PlayerRecord rec)
        {
            try
            {
                if (rec == null || !IsSafeGuid(rec.Guid)) return false;
                Directory.CreateDirectory(LocalDir);
                string path = LocalPath(saveIndex, rec.Guid);
                // rotate: current -> .bak1 -> ... -> .bak4 (5 versions in total)
                if (File.Exists(path))
                {
                    try
                    {
                        for (int i = 4; i >= 2; i--)
                        {
                            string from = path + ".bak" + (i - 1), to = path + ".bak" + i;
                            if (File.Exists(from)) File.Copy(from, to, true);
                        }
                        File.Copy(path, path + ".bak1", true);
                    }
                    catch (Exception e) { Log.Warn("Local backup rotation failed: " + e.Message); }
                }
                return AtomicWrite(path, rec.ToBytes());
            }
            catch (Exception e) { Log.Error("LocalSave error: " + e); return false; }
        }

        // ---------------- pre-split backup / known players ----------------

        public static string ProfileFile(int saveIndex)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Red Dot Games", "Car Mechanic Simulator 2021", "Save", "profile" + saveIndex + ".cms21b");
        }

        public static string TogetherSaveFile(int saveIndex)
        {
            return Path.Combine(Config.GameDir, "Mods", "togetherMod", "saves", "save_" + saveIndex + ".cms21mp");
        }

        /// <summary>Copies the host's game profile and Together save to before-first-split\&lt;timestamp&gt;. True only if both copies exist with identical sizes.</summary>
        public static bool BackupHostProfileBeforeSplit(int saveIndex)
        {
            try
            {
                string dest = Path.Combine(SaveDir(saveIndex), "before-first-split", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dest);
                string[] srcs = { ProfileFile(saveIndex), TogetherSaveFile(saveIndex) };
                foreach (var s in srcs)
                {
                    if (!File.Exists(s)) { Log.Error("Pre-split backup: source missing: " + s); return false; }
                    string d = Path.Combine(dest, Path.GetFileName(s));
                    File.Copy(s, d, true);
                    if (!File.Exists(d) || new FileInfo(d).Length != new FileInfo(s).Length)
                    {
                        Log.Error("Pre-split backup size mismatch: " + s);
                        return false;
                    }
                }
                Log.Msg("Pre-split backup created: " + dest);
                return true;
            }
            catch (Exception e) { Log.Error("BackupHostProfileBeforeSplit error: " + e); return false; }
        }

        public static bool IsKnownTogetherPlayer(int saveIndex, string guid)
        {
            try
            {
                if (string.IsNullOrEmpty(guid)) return false;
                string path = TogetherSaveFile(saveIndex);
                if (!File.Exists(path)) return false;
                var jo = JObject.Parse(File.ReadAllText(path));
                var arr = jo["playerInfos"] as JArray;
                if (arr == null) return false;
                return arr.Any(p => string.Equals((string)p["id"], guid, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) { Log.Warn("IsKnownTogetherPlayer could not read: " + e.Message); return false; }
        }

        // ---------------- atomic write ----------------

        public static bool AtomicWrite(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("File write failed (" + path + "): " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return false;
            }
        }
    }
}
