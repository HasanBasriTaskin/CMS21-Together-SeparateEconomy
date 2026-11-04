using System;
using System.Reflection;
using CMS21Together.ClientSide.Data;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared;

namespace TogetherSeparateEconomy
{
    /// <summary>Client side: handshake, apply our record, periodic snapshots, save on disconnect/quit.</summary>
    public class ClientFlow : IModPart
    {
        public string Name { get { return "ClientFlow"; } }

        const double MaxWaitSeconds = 90;
        const double QuietSeconds = 3;
        const double RecordTimeoutSeconds = 90;

        static bool _wasClient, _helloSent, _forceUploadOnce;
        static double _readySince = -1, _recordDeadline, _lastSnap, _forceAt;
        static int _snapCount;
        static string _lastHash;
        static PlayerRecord _base;          // the record we applied (fallback bytes for disabled features)
        static bool _firstSplitDone;
        static PropertyInfo _roProp;

        public void Init()
        {
            Net.OnHelloAck += OnHelloAck;
            Net.OnRecordFromHost += OnRecordFromHost;
            Net.OnRemoteError += OnRemoteError;
            Net.OnTransferError += m => Log.Debug("transfer error: " + m);
        }

        // ---------------------------------------------------------------- per frame
        public void Update()
        {
            if (Main.Disabled) return;
            bool isClient = Net.IsClient;
            if (!isClient)
            {
                if (_wasClient) ResetConnection("connection closed");
                _wasClient = false;
                return;
            }
            _wasClient = true;
            if (!Config.SeparateInventory && !Config.SeparateWarehouse && !Config.SeparateMoney) return;

            if (!_helloSent) { TryHello(); return; }

            if (_recordDeadline > 0 && Separation.Now > _recordDeadline && !Separation.ClientActive)
            {
                _recordDeadline = 0;
                Snap.Say("host_no_record", true);
            }
            if (Separation.ClientActive) SnapshotTick();
        }

        static bool LateJoinReadOnly()
        {
            try
            {
                if (_roProp == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("FixForTogether.LateJoin", false);
                        if (t == null) continue;
                        _roProp = t.GetProperty("ReadOnlyActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_roProp != null) break;
                    }
                }
                if (_roProp == null) return false;
                return (bool)_roProp.GetValue(null, null);
            }
            catch (Exception e) { Log.Debug("Could not read LateJoin.ReadOnlyActive: " + e.Message); return false; }
        }

        static void TryHello()
        {
            if (!ClientData.GameReady || !Snap.GameLoaded) { _readySince = -1; return; }
            double now = Separation.Now;
            if (_readySince < 0) { _readySince = now; Log.Debug("Game ready, waiting before handshake"); }
            double waited = now - _readySince;
            bool readOnly = LateJoinReadOnly();
            bool quiet = now - Separation.LastInventoryPacketTime > QuietSeconds;
            if (waited < MaxWaitSeconds && (readOnly || !quiet || waited < QuietSeconds)) return;
            if (waited >= MaxWaitSeconds && (readOnly || !quiet)) Log.Warn("Wait limit reached (" + MaxWaitSeconds + " s); starting handshake anyway");
            _helloSent = true;
            Log.Msg("Starting handshake with host");
            Net.SendHello();
        }

        // ---------------------------------------------------------------- handshake
        static void OnHelloAck(bool accepted, string hostVer, string hostCfg, string reason)
        {
            if (Main.Disabled || !Net.IsClient) return;
            if (!accepted)
            {
                Separation.ClientActive = false;
                Snap.Say("host_refused", true, reason);
                return;
            }
            Log.Msg("Handshake complete (host version " + hostVer + ")");
            if (Config.ApplyHostSummary(hostCfg))
                Snap.Say("cfg_differs", true, Config.Summary());
            _recordDeadline = Separation.Now + RecordTimeoutSeconds;
            Net.RequestRecord();
        }

        static void OnRemoteError(int from, string text)
        {
            if (from != 0 || Separation.ClientActive) return;
            _recordDeadline = 0;
            Snap.Say("host_error", true, text);
        }

        // ---------------------------------------------------------------- apply
        static void OnRecordFromHost(PlayerRecord hostRec, int transferId)
        {
            if (Main.Disabled || !Net.IsClient) return;
            _recordDeadline = 0;
            if (Separation.ClientActive) { Log.Warn("Record already applied, second record ignored"); return; }
            int idx = SavesManager.currentSaveIndex;
            string guid = ClientData.UserData == null ? null : ClientData.UserData.playerGUID;
            Log.Msg("Host record received: kind=" + hostRec.Kind + " money=" + hostRec.Money + " split=" + hostRec.RemapUids);

            PlayerRecord rec = hostRec;
            bool useLocal = false;
            try
            {
                var local = string.IsNullOrEmpty(guid) ? null : Storage.LocalLoad(idx, guid);
                if (hostRec.Kind == RecordKind.Normal && (hostRec.FirstSplitDone || !hostRec.RemapUids) && local != null && local.Kind == RecordKind.Normal
                    && local.FirstSplitDone && local.SavedAtUtcTicks > hostRec.SavedAtUtcTicks)
                {
                    rec = local; useLocal = true;
                    Log.Msg("Local backup is newer than the host copy; using local record");
                }
            }
            catch (Exception e) { Log.Warn("Could not read local backup: " + e.Message); }

            try
            {
                if (!Snap.GameLoaded) throw new InvalidOperationException("game profile not ready");
                Separation.ClientActive = true;      // block outgoing sync first
                Log.Msg("UID probe: LastUID=" + UIDManager.LastUID + " (before apply)");
                if (rec.Kind == RecordKind.NewPlayerDefaults) ApplyDefaults(rec);
                else if (rec.Kind == RecordKind.Normal) ApplyNormal(rec);
                else throw new InvalidOperationException("record kind not supported: " + rec.Kind);

                _base = rec;
                Separation.InventoryDirty = Separation.WarehouseDirty = false;
                Net.SendApplyAck(transferId, true);
                bool upload = useLocal || rec.Kind == RecordKind.NewPlayerDefaults;
                _firstSplitDone = rec.FirstSplitDone || rec.Kind == RecordKind.NewPlayerDefaults || useLocal || rec.RemapUids;
                _lastHash = null;
                DoSnapshot(upload, false);
                _lastSnap = Separation.Now;
                if (rec.RemapUids) { _forceUploadOnce = true; _forceAt = Separation.Now + 15; }
                Snap.Say("loaded", false, Snap.CountParts(Snap.Profile.inventoryData), GlobalData.PlayerMoney);
            }
            catch (Exception e)
            {
                Log.Error("Could not apply record: " + e);
                Separation.ClientActive = false;
                Separation.Applying = false;
                _base = null; _forceUploadOnce = false; _lastHash = null;
                try { Net.SendApplyAck(transferId, false); } catch (Exception) { }
                Snap.Say("load_failed", true, e.Message);
            }
        }

        static void ApplyNormal(PlayerRecord rec)
        {
            var gm = Snap.GM;
            var prof = Snap.Profile;
            byte ver = rec.SaveVersion != 0 ? rec.SaveVersion : prof.saveVersion;
            if (ver != prof.saveVersion) Log.Warn("Record version (" + ver + ") differs from game profile version (" + prof.saveVersion + ")");

            if (Config.SeparateInventory && rec.InventoryBytes != null && rec.InventoryBytes.Length > 0)
            {
                var d = Snap.DeserializeInventory(rec.InventoryBytes, ver);
                if (rec.RemapUids) { RemapUids(d); Log.Msg("UID probe: LastUID=" + UIDManager.LastUID + " (after remap)"); }
                prof.inventoryData = d;
                if (gm.Inventory.items != null) gm.Inventory.items.Clear();
                if (gm.Inventory.groups != null) gm.Inventory.groups.Clear();
                Separation.Applying = true;
                try { gm.Inventory.Load(); }
                finally { Separation.Applying = false; }
                if (rec.RemapUids) gm.Inventory.Save();
                Log.Msg("Inventory loaded: " + Snap.CountParts(d) + " parts" + (rec.RemapUids ? " (UIDs regenerated)" : ""));
            }

            if (Config.SeparateWarehouse && rec.WarehouseBytes != null && rec.WarehouseBytes.Length > 0)
            {
                var w = Snap.DeserializeWarehouse(rec.WarehouseBytes, ver);
                int cur = Warehouse.amountOfUnlockedWarehouses;
                if (prof.warehouseData != null) cur = Math.Max(cur, prof.warehouseData.amountOfUnlockedWarehouses);
                w.amountOfUnlockedWarehouses = Math.Max(cur, w.amountOfUnlockedWarehouses);
                prof.warehouseData = w;
                gm.Warehouse.Load();
                Log.Msg("Warehouse loaded");
            }

            if (Config.SeparateMoney) GlobalData.SetPlayerMoney(rec.Money);
        }

        static void RemapUids(NewInventoryData d)
        {
            int n = 0;
            if (d.items != null)
                for (int i = 0; i < d.items.Count; i++) { var it = d.items[i]; if (it != null) { it.UID = UIDManager.GetNewUID(); n++; } }
            if (d.groups != null)
                for (int i = 0; i < d.groups.Count; i++)
                {
                    var g = d.groups[i];
                    if (g == null) continue;
                    g.UID = UIDManager.GetNewUID(); n++;
                    var list = g.ItemList;
                    if (list == null) continue;
                    for (int j = 0; j < list.Count; j++) { var it = list[j]; if (it != null) { it.UID = UIDManager.GetNewUID(); n++; } }
                }
            Log.Debug("UIDs regenerated: " + n);
        }

        static void ApplyDefaults(PlayerRecord rec)
        {
            var gm = Snap.GM;
            if (Config.SeparateInventory) { gm.Inventory.DeleteAll(); gm.Inventory.Save(); }
            if (Config.SeparateMoney) GlobalData.SetPlayerMoney(rec.Money);
            int level = Math.Max(0, rec.Level - 1);   // the game shows stored level + 1
            GlobalData.PlayerLevel = level;
            GlobalData.PlayerExp = 0;
            GlobalData.PrevPlayerLevel = level;
            GlobalData.PrevPlayerExp = 0;
            int points = Math.Max(0, rec.SkillPoints);
            gm.UpgradeSystem.availablePoints = points;
            if (ClientData.Instance != null) ClientData.Instance.exp = 0;
            ClientSend.ExpPacket(0, level);
            ClientSend.PointPacket(points);
            Log.Msg("New player defaults applied: level " + rec.Level + ", money " + rec.Money + ", points " + points);
        }

        // ---------------------------------------------------------------- snapshots
        static void SnapshotTick()
        {
            double now = Separation.Now;
            if (_forceUploadOnce && now >= _forceAt) { _forceUploadOnce = false; _lastHash = null; DoSnapshot(true, false); _lastSnap = now; return; }
            double interval = Config.SnapshotIntervalSeconds;
            bool dirty = Separation.InventoryDirty || Separation.WarehouseDirty;
            double wait = dirty ? Math.Min(interval, 10) : interval;
            if (now - _lastSnap < wait) return;
            _lastSnap = now;
            _snapCount++;
            DoSnapshot(true, _snapCount % 5 == 0);   // every 5th: re-send even if unchanged (host may have ignored earlier uploads)
        }

        static PlayerRecord MakeRecord(out string hash)
        {
            byte[] inv, wh; byte ver; int money;
            Snap.Capture(out inv, out wh, out ver, out money);
            var b = _base;
            if (!Config.SeparateInventory && b != null) inv = b.InventoryBytes ?? new byte[0];
            if (!Config.SeparateWarehouse && b != null) wh = b.WarehouseBytes ?? new byte[0];
            if (!Config.SeparateMoney && b != null) money = b.Money;
            hash = Snap.Hash(inv, wh, money);
            var ud = ClientData.UserData;
            return new PlayerRecord
            {
                Guid = ud == null ? "" : ud.playerGUID,
                PlayerName = ud == null ? "" : ud.username,
                SavedAtUtcTicks = DateTime.UtcNow.Ticks,
                Money = money,
                SaveVersion = ver,
                FirstSplitDone = _firstSplitDone,
                Kind = RecordKind.Normal,
                RemapUids = false,
                InventoryBytes = inv,
                WarehouseBytes = wh,
            };
        }

        /// <summary>Capture, store locally and (optionally) upload when the content changed (or force).</summary>
        static void DoSnapshot(bool upload, bool force)
        {
            try
            {
                string hash;
                var rec = MakeRecord(out hash);
                bool changed = hash != _lastHash;
                if (!changed && !force) return;
                if (changed) Storage.LocalSave(SavesManager.currentSaveIndex, rec);
                _lastHash = hash;
                Separation.InventoryDirty = Separation.WarehouseDirty = false;
                if (upload && Net.IsClient && Net.HandshakeDone)
                {
                    int tid = Net.SendRecordToHost(rec);
                    Log.Debug("Snapshot " + (tid >= 0 ? "sent (#" + tid + ")" : "not sent") + ", money=" + rec.Money);
                }
            }
            catch (Exception e) { Log.Warn("Snapshot failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- disconnect / quit
        public static void OnDisconnecting()
        {
            if (Main.Disabled || !Separation.ClientActive) return;
            Log.Msg("Disconnecting: saving local record");
            DoSnapshot(false, false);
            ResetConnection("disconnecting");
        }

        public static void OnQuit()
        {
            try
            {
                if (Main.Disabled || !Separation.ClientActive) return;
                Log.Msg("Game quitting: saving local record");
                DoSnapshot(false, false);
            }
            catch (Exception e) { Log.Warn("Could not save on quit: " + e.Message); }
        }

        static void ResetConnection(string why)
        {
            if (Separation.ClientActive) Log.Msg("Separation ended (" + why + ")");
            Separation.ClientActive = false;
            Separation.Applying = false;
            Separation.InventoryDirty = Separation.WarehouseDirty = false;
            _helloSent = false; _readySince = -1; _recordDeadline = 0;
            _lastHash = null; _base = null; _firstSplitDone = false; _forceUploadOnce = false; _snapCount = 0;
            Config.ClearOverrides();
        }
    }
}
