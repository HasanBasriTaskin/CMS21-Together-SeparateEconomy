using System;
using System.Collections.Generic;
using CMS21Together.Shared;

namespace TogetherSeparateEconomy
{
    /// <summary>Host side: answer handshakes, serve records, run the one-time money/inventory split safely.</summary>
    public class HostFlow : IModPart
    {
        public string Name { get { return "HostFlow"; } }

        const double AckTimeoutSeconds = 120;

        sealed class Pending
        {
            public int ClientId, Tid, Half, Idx;
            public string Guid;
            public double Deadline;
            public PlayerRecord Rec;
            public bool IsNewPlayer;
        }

        static readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        static readonly HashSet<int> _acked = new HashSet<int>();   // clients that acked a record successfully
        static readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        public void Init()
        {
            Net.OnHelloFromClient += OnHelloFromClient;
            Net.OnRecordRequest += OnRecordRequest;
            Net.OnRecordFromClient += OnRecordFromClient;
            Net.OnApplyAck += OnApplyAck;
            Net.OnClientGone += OnClientGone;
            Net.OnRemoteError += OnRemoteError;
        }

        public void Update()
        {
            if (_pending.Count == 0) return;
            double now = Separation.Now;
            List<int> expired = null;
            foreach (var kv in _pending)
                if (now > kv.Value.Deadline) { if (expired == null) expired = new List<int>(); expired.Add(kv.Key); }
            if (expired == null) return;
            foreach (int id in expired)
            {
                Log.Warn("Ack timeout (client " + id + "): host money unchanged, split will be retried later if needed");
                _pending.Remove(id);
            }
        }

        // ---------------------------------------------------------------- handshake
        static void OnHelloFromClient(int clientId, string guid, string name, string version, string cfg)
        {
            if (Main.Disabled || !Net.IsHost) return;
            _names[clientId] = name ?? "";
            _acked.Remove(clientId);
            if (version != Main.ModVersion)
            {
                Log.Warn("Version mismatch: client " + clientId + " (" + version + ") host (" + Main.ModVersion + ")");
                Net.SendHelloAck(clientId, false, "version mismatch: host " + Main.ModVersion);
                return;
            }
            if (cfg != Config.Summary()) Log.Warn("Client settings differ (client: " + cfg + ", host: " + Config.Summary() + "); host settings will apply");
            Net.SendHelloAck(clientId, true, "");
            Log.Msg("Player handshake: " + name + " (client " + clientId + ")");
        }

        // ---------------------------------------------------------------- record request / split
        static void OnRecordRequest(int clientId)
        {
            if (Main.Disabled || !Net.IsHost) return;
            try
            {
                string guid = Net.GuidOf(clientId);
                if (guid == null) { Net.SendError(clientId, "no handshake"); return; }
                int idx = SavesManager.currentSaveIndex;
                var rec = Storage.HostLoad(idx, guid);
                string name; if (!_names.TryGetValue(clientId, out name)) name = "";

                if (rec == null && Storage.HostFileExists(idx, guid))
                {
                    Log.Error("Player record exists but could not be read (" + guid + "); split not performed");
                    Net.SendError(clientId, "host record unreadable");
                    return;
                }

                if (rec != null && rec.FirstSplitDone)
                {
                    int tid = Net.SendRecordToClient(clientId, rec);
                    Log.Msg("Record sent: " + name + " (money " + rec.Money + ", transfer #" + tid + ")");
                    return;
                }

                if (rec == null && !Storage.IsKnownTogetherPlayer(idx, guid))
                {
                    var nr = PlayerRecord.Marker(RecordKind.NewPlayerDefaults, guid);
                    nr.PlayerName = name;
                    nr.Money = Config.NewPlayerMoney;
                    nr.Level = Config.NewPlayerLevel;
                    nr.Exp = 0;
                    nr.SkillPoints = Math.Max(0, Config.NewPlayerLevel - 1);
                    int tid = Net.SendRecordToClient(clientId, nr);
                    Log.Msg("New player: defaults sent to " + name + " (transfer #" + tid + ")");
                    if (tid >= 0)
                    {
                        // Stub stored on the client ack: otherwise a lost first upload would make the next join look like a
                        // known Together player and trigger a money split. SavedAt=0 so any real upload supersedes it.
                        var stub = new PlayerRecord
                        {
                            Kind = RecordKind.Normal, Guid = guid, PlayerName = name, Money = Config.NewPlayerMoney,
                            SaveVersion = Snap.GameLoaded ? Snap.Profile.saveVersion : (byte)0,
                            FirstSplitDone = true, RemapUids = false, SavedAtUtcTicks = 0,
                            InventoryBytes = new byte[0], WarehouseBytes = new byte[0],
                        };
                        _pending[clientId] = new Pending { ClientId = clientId, Tid = tid, Idx = idx, Guid = guid, Rec = stub, IsNewPlayer = true, Deadline = Separation.Now + AckTimeoutSeconds };
                    }
                    return;
                }

                FirstSplit(clientId, guid, name, idx);
            }
            catch (Exception e)
            {
                Log.Error("Could not process record request: " + e);
                try { Net.SendError(clientId, "host error"); } catch (Exception) { }
            }
        }

        static void FirstSplit(int clientId, string guid, string name, int idx)
        {
            if (!Snap.GameLoaded) { Log.Error("Split: host game not ready"); Net.SendError(clientId, "host game not ready"); return; }

            int m = GlobalData.PlayerMoney;
            if (!Storage.BackupHostProfileBeforeSplit(idx))
            {
                Log.Error("Split cancelled: backup failed");
                Net.SendError(clientId, "host could not back up");
                return;
            }

            int half = Config.SeparateMoney ? m / 2 : 0;
            byte[] inv = new byte[0];
            byte ver = Snap.Profile.saveVersion;
            if (Config.SeparateInventory)
            {
                Snap.GM.Inventory.Save();
                inv = Snap.SerializeInventory(Snap.Profile.inventoryData, ver);
            }
            var r = new PlayerRecord
            {
                Kind = RecordKind.Normal,
                Guid = guid,
                PlayerName = name,
                Money = half,
                SaveVersion = ver,
                InventoryBytes = inv,
                WarehouseBytes = new byte[0],
                RemapUids = true,
                FirstSplitDone = false,
                SavedAtUtcTicks = DateTime.UtcNow.Ticks,
            };
            if (!Storage.HostSave(idx, r))
            {
                Log.Error("Split cancelled: record write failed");
                Net.SendError(clientId, "host could not write record");
                return;
            }
            int tid = Net.SendRecordToClient(clientId, r);
            if (tid < 0) { Log.Warn("Split record not sent; host money unchanged"); return; }
            _pending[clientId] = new Pending { ClientId = clientId, Tid = tid, Half = half, Idx = idx, Guid = guid, Rec = r, Deadline = Separation.Now + AckTimeoutSeconds };
            Log.Msg("Split ready: host money " + m + ", friend gets " + half + " (waiting for ack)");
        }

        static void OnApplyAck(int clientId, int tid, bool ok)
        {
            if (Main.Disabled || !Net.IsHost) return;
            if (ok) _acked.Add(clientId);
            else { _acked.Remove(clientId); HandleFailedAck(clientId, tid); return; }
            Pending p;
            if (!_pending.TryGetValue(clientId, out p) || p.Tid != tid) { Log.Debug("Ack (not a split) client " + clientId + " #" + tid + " ok=" + ok); return; }
            _pending.Remove(clientId);
            if (p.IsNewPlayer)
            {
                if (!ok) { Log.Warn("New player defaults could not be applied (client " + clientId + ")"); return; }
                if (Storage.HostLoad(p.Idx, p.Guid) == null && Storage.HostSave(p.Idx, p.Rec)) Log.Msg("New player record created: " + p.Guid);
                return;
            }
            if (!ok) { Log.Warn("Client rejected the split; host money unchanged"); return; }
            try
            {
                int before = GlobalData.PlayerMoney;
                int after = Math.Max(0, before - p.Half);
                if (Config.SeparateMoney && p.Half > 0) GlobalData.SetPlayerMoney(after);
                Log.Msg("Split done: host money " + before + " -> " + GlobalData.PlayerMoney + " (friend got " + p.Half + ")");
                try
                {
                    var gl = GarageLoader.Get();
                    if (gl != null) gl.Save(false); else Log.Warn("GarageLoader missing; host game not saved");
                }
                catch (Exception e) { Log.Warn("Host game not saved: " + e.Message); }
                p.Rec.FirstSplitDone = true;
                p.Rec.SavedAtUtcTicks = 0;   // any later client upload wins regardless of clock skew
                if (!Storage.HostSave(p.Idx, p.Rec)) Log.Error("Split record (FirstSplitDone) write failed: " + p.Guid);
            }
            catch (Exception e) { Log.Error("Could not process split ack: " + e); }
        }

        /// <summary>Client failed to apply: it falls back to shared sync, so the host must stop filtering its packets.</summary>
        static void HandleFailedAck(int clientId, int tid)
        {
            Pending p;
            if (_pending.TryGetValue(clientId, out p) && p.Tid == tid)
            {
                _pending.Remove(clientId);
                if (p.IsNewPlayer) Log.Warn("New player defaults could not be applied (client " + clientId + ")");
                else Log.Warn("Client rejected the split; host money unchanged");
            }
            else
            {
                Log.Warn("Client could not apply the record (client " + clientId + " #" + tid + ")");
                _pending.Remove(clientId);
            }
            Net.ForgetClient(clientId);
        }

        static void OnRemoteError(int from, string text)
        {
            if (Main.Disabled || !Net.IsHost || from == 0) return;
            if (_acked.Contains(from)) return;
            _pending.Remove(from);
            Net.ForgetClient(from);
        }

        static void OnClientGone(int clientId)
        {
            _acked.Remove(clientId);
            _names.Remove(clientId);
            if (_pending.Remove(clientId)) Log.Warn("Player left during split; host money unchanged");
        }

        // ---------------------------------------------------------------- uploads
        static void OnRecordFromClient(int clientId, PlayerRecord rec)
        {
            if (Main.Disabled || !Net.IsHost) return;
            try
            {
                int idx = SavesManager.currentSaveIndex;
                if (_pending.ContainsKey(clientId)) { Log.Debug("Record received before split finished, ignored"); return; }
                var stored = Storage.HostLoad(idx, rec.Guid);
                if (stored != null && !stored.FirstSplitDone) { Log.Debug("Record received before split completed, ignored (" + rec.Guid + ")"); return; }
                rec.FirstSplitDone = true;
                rec.RemapUids = false;
                if (stored != null && rec.SavedAtUtcTicks < stored.SavedAtUtcTicks) { Log.Debug("Stale record ignored"); return; }
                if (Storage.HostSave(idx, rec)) Log.Debug("Player record stored: " + rec.PlayerName + " money=" + rec.Money);
            }
            catch (Exception e) { Log.Error("Could not process client record: " + e); }
        }
    }
}
