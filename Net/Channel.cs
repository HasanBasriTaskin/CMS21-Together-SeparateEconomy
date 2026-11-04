using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CMS21Together.ClientSide;
using CMS21Together.ClientSide.Data;
using CMS21Together.ServerSide;
using CMS21Together.Shared;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    // ---------------------------------------------------------------------------------------------
    // What was verified in CMS21-Together.dll (Mono.Cecil):
    //  * ClientSend.SendData(Packet p, bool reliable):  p.WriteLength(); Client.Instance.SendData(p, reliable);
    //  * ServerSend.SendData(int to, Packet p, bool reliable): p.WriteLength(); Server.Instance.clients[to].SendData(p, reliable);
    //    -> we mirror exactly this (WriteLength is called by the sender, exactly once, right before sending).
    //  * Receive (ClientTCP/ServerSide TCP/Steam HandleData): length-prefix frames are split by the transport, then
    //    ThreadManager.ExecuteOnMainThread runs: new Packet(bytes); id = packet.ReadInt(true); handler(packet) /
    //    handler(fromClient, packet). So the handler gets the packet with the id ALREADY consumed (readPos = 4),
    //    on the Unity main thread. Handlers still just parse here and re-post to Main.Post for safety.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Network channel over Together's own connection. See README / report for the API.</summary>
    public static partial class Net
    {
        public const int PktControl = 170;
        public const int PktData = 171;

        // ---------------- events (all raised on the main thread) ----------------
        // client side
        public static event Action<bool, string, string, string> OnHelloAck;           // accepted, hostVersion, hostConfig, reason
        public static event Action<PlayerRecord, int> OnRecordFromHost;                 // record, transferId
        public static event Action<string> OnTransferError;                             // any side
        public static event Action<int, string> OnRemoteError;                          // fromId (0 = host), text
        // host side
        public static event Action<int, string, string, string, string> OnHelloFromClient; // clientId, guid, username, version, config
        public static event Action<int> OnRecordRequest;                                // clientId
        public static event Action<int, PlayerRecord> OnRecordFromClient;               // clientId, record
        public static event Action<int, int, bool> OnApplyAck;                          // clientId, transferId, ok
        public static event Action<int> OnClientGone;                                   // clientId

        // ---------------- session state ----------------
        public static bool IsHost { get { var s = Server.Instance; return s != null && s.isRunning; } }
        public static bool IsClient { get { var c = Client.Instance; return c != null && c.isConnected && !IsHost; } }
        /// <summary>Client side: HelloAck(accepted) received in this connection.</summary>
        public static bool HandshakeDone { get; private set; }
        public static int HandshakenClientCount { get { return _guids.Count; } }

        static readonly Dictionary<int, string> _guids = new Dictionary<int, string>();     // host: handshaken clients
        static readonly Dictionary<int, string> _pendingHello = new Dictionary<int, string>(); // host: hello seen, ack not yet sent
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static double _helloDeadline;       // 0 = none
        static double _nextPoll;
        static bool _wasClient;
        static bool _wasHost;

        public static string GuidOf(int clientId) { string g; return _guids.TryGetValue(clientId, out g) ? g : null; }
        public static int ClientIdOf(string guid)
        {
            foreach (var kv in _guids) if (string.Equals(kv.Value, guid, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return -1;
        }
        public static int[] HandshakenClientIds { get { return _guids.Keys.ToArray(); } }

        // ---------------- registration ----------------
        public static void TryRegisterAll()
        {
            if (Main.Disabled) return;
            RegisterClient();
            RegisterServer();
        }

        static void RegisterClient()
        {
            var d = Client.PacketHandlers;
            if (d == null) return;
            if (!Reg(d, PktControl, new Client.PacketHandler(ClientControl))) return;
            Reg(d, PktData, new Client.PacketHandler(ClientData_));
        }

        static void RegisterServer()
        {
            var d = Server.packetHandlers;
            if (d == null) return;
            if (!Reg(d, PktControl, new Server.packetHandler(ServerControl))) return;
            Reg(d, PktData, new Server.packetHandler(ServerData));
        }

        static bool Reg<T>(Dictionary<int, T> d, int id, T handler) where T : class
        {
            T existing;
            if (d.TryGetValue(id, out existing))
            {
                var del = existing as Delegate;
                if (del != null && del.Method.DeclaringType == typeof(Net)) return true; // already ours
                Main.Disabled = true;
                Log.Error("Packet ID " + id + " is used by another mod; Together Separate Economy is DISABLED this session.");
                return false;
            }
            d[id] = handler;
            Log.Debug("Packet ID " + id + " registered");
            return true;
        }

        internal static void OnClientTablesRebuilt() { ResetClient(); RegisterClient(); }
        internal static void OnServerTablesRebuilt() { ResetHost(); RegisterServer(); }

        // ---------------- raw send ----------------
        /// <summary>target 0 = to host (via Client), otherwise server-side client id. Mirrors ClientSend/ServerSend.SendData.</summary>
        internal static bool RawSend(int target, Packet p, bool reliable)
        {
            try
            {
                if (target == 0)
                {
                    var c = Client.Instance;
                    if (c == null || !c.isConnected) { Log.Debug("Not sent: client not connected"); return false; }
                    p.WriteLength();
                    c.SendData(p, reliable);
                    return true;
                }
                var s = Server.Instance;
                ServerConnection sc;
                if (s == null || !s.isRunning || !s.clients.TryGetValue(target, out sc) || sc == null || !sc.isConnected)
                { Log.Debug("Not sent: client " + target + " not found"); return false; }
                p.WriteLength();
                sc.SendData(p, reliable);
                return true;
            }
            catch (Exception e) { Log.Error("RawSend error: " + e.Message); return false; }
        }

        static void SendControl(int target, MsgType type, Action<Packet> fill)
        {
            var p = new Packet(PktControl);
            p.Write((int)type);
            if (fill != null) fill(p);
            RawSend(target, p, true);
        }

        // ---------------- client API ----------------
        public static void SendHello()
        {
            if (Main.Disabled) { FireHelloAck(false, "", "", "mod disabled (packet ID clash)"); return; }
            if (IsHost) { Log.Warn("SendHello: the host does not send hello to itself; it handles its own record locally."); return; }
            if (!IsClient) { FireHelloAck(false, "", "", "not connected"); return; }
            var ud = ClientData.UserData;
            string guid = ud == null ? null : ud.playerGUID;
            if (string.IsNullOrEmpty(guid)) { FireHelloAck(false, "", "", "player id (guid) not available yet"); return; }
            string name = ud.username ?? "";
            HandshakeDone = false;
            _helloDeadline = _clock.Elapsed.TotalSeconds + 20;
            SendControl(0, MsgType.Hello, p =>
            {
                PW.Str(p, guid); PW.Str(p, name); PW.Str(p, Main.ModVersion); PW.Str(p, Config.Summary());
            });
            Log.Debug("Hello sent guid=" + guid);
        }

        public static void RequestRecord()
        {
            if (!ClientReady("RequestRecord")) return;
            SendControl(0, MsgType.RequestRecord, null);
        }

        /// <summary>Queues a paced upload of the record to the host. Returns transferId, or -1 on failure.</summary>
        public static int SendRecordToHost(PlayerRecord rec)
        {
            if (!ClientReady("SendRecordToHost") || rec == null) return -1;
            try { return Transfers.Enqueue(0, rec); }
            catch (Exception e) { Log.Error("SendRecordToHost: " + e.Message); FireTransferError("send error: " + e.Message); return -1; }
        }

        public static void SendApplyAck(int transferId, bool ok)
        {
            if (!ClientReady("SendApplyAck")) return;
            SendControl(0, MsgType.ApplyAck, p => { p.Write(transferId); PW.Bool(p, ok); });
        }

        static bool ClientReady(string what)
        {
            if (Main.Disabled || !IsClient) { Log.Warn(what + ": not in client mode / disabled"); return false; }
            if (!HandshakeDone) { Log.Warn(what + ": handshake not complete"); return false; }
            return true;
        }

        // ---------------- host API ----------------
        /// <summary>Answer a Hello. accepted=true also registers clientId -> guid as handshaken.</summary>
        public static void SendHelloAck(int clientId, bool accepted, string reason)
        {
            if (!IsHost) return;
            if (accepted)
            {
                string guid;
                if (!_pendingHello.TryGetValue(clientId, out guid)) { Log.Warn("SendHelloAck: no pending hello, client=" + clientId); return; }
                _guids[clientId] = guid;
            }
            _pendingHello.Remove(clientId);
            SendControl(clientId, MsgType.HelloAck, p =>
            {
                PW.Bool(p, accepted); PW.Str(p, Main.ModVersion); PW.Str(p, Config.Summary()); PW.Str(p, reason ?? "");
            });
        }

        /// <summary>Queues a paced send of a record to a handshaken client. Returns transferId, -1 on failure.</summary>
        public static int SendRecordToClient(int clientId, PlayerRecord rec)
        {
            if (!IsHost || rec == null) return -1;
            if (!_guids.ContainsKey(clientId)) { Log.Warn("SendRecordToClient: client " + clientId + " has not completed handshake"); return -1; }
            var s = Server.Instance;
            if (!s.clients.ContainsKey(clientId)) { Log.Warn("SendRecordToClient: client " + clientId + " is not in the connection list (is it the host itself?)"); return -1; }
            try { return Transfers.Enqueue(clientId, rec); }
            catch (Exception e) { Log.Error("SendRecordToClient: " + e.Message); FireTransferError("send error: " + e.Message); return -1; }
        }

        /// <summary>Either side. clientId 0 = to host.</summary>
        public static void SendError(int clientId, string text)
        {
            SendControl(clientId, MsgType.Error, p => PW.Str(p, text));
        }

        // ---------------- packet handlers ----------------
        static void ClientControl(Packet p) { HandleControl(0, p); }
        static void ServerControl(int from, Packet p) { HandleControl(from, p); }
        static void ClientData_(Packet p) { HandleData(0, p); }
        static void ServerData(int from, Packet p) { HandleData(from, p); }

        static void HandleControl(int sender, Packet p)
        {
            try
            {
                if (Main.Disabled) return;
                var type = (MsgType)PR.Int(p);
                bool fromClient = sender != 0; // handler on server side
                Log.Debug("control packet " + type + " sender=" + sender);
                switch (type)
                {
                    case MsgType.Hello:
                        if (!fromClient) return;
                        {
                            string guid = PR.Str(p), name = PR.Str(p), ver = PR.Str(p), cfg = PR.Str(p);
                            if (!Storage.IsSafeGuid(guid)) { SendControl(sender, MsgType.HelloAck, q => { PW.Bool(q, false); PW.Str(q, Main.ModVersion); PW.Str(q, Config.Summary()); PW.Str(q, "invalid guid"); }); return; }
                            _guids.Remove(sender);
                            _pendingHello[sender] = guid;
                            Post(() => Fire(OnHelloFromClient, sender, guid, name, ver, cfg));
                        }
                        break;
                    case MsgType.HelloAck:
                        if (fromClient) return;
                        {
                            bool ok = PR.Bool(p); string ver = PR.Str(p), cfg = PR.Str(p), reason = PR.Str(p);
                            Post(() =>
                            {
                                _helloDeadline = 0;
                                HandshakeDone = ok;
                                FireHelloAck(ok, ver, cfg, reason);
                            });
                        }
                        break;
                    case MsgType.RequestRecord:
                        if (!RequireHandshake(sender)) return;
                        Post(() => Fire(OnRecordRequest, sender));
                        break;
                    case MsgType.RecordBegin:
                        if (!RequireHandshake(sender)) return;
                        Transfers.OnBegin(sender, p);
                        break;
                    case MsgType.RecordEnd:
                        {
                            if (!RequireHandshake(sender)) return;
                            int id;
                            var rec = Transfers.OnEnd(sender, p, out id);
                            if (rec != null) Post(() => Deliver(sender, rec, id));
                        }
                        break;
                    case MsgType.ApplyAck:
                        {
                            if (!RequireHandshake(sender)) return;
                            int tid = PR.Int(p); bool ok = PR.Bool(p);
                            Post(() => Fire(OnApplyAck, sender, tid, ok));
                        }
                        break;
                    case MsgType.Error:
                        {
                            string text = PR.Str(p);
                            Post(() => { Log.Warn("Error from peer (" + sender + "): " + text); Fire(OnRemoteError, sender, text); });
                        }
                        break;
                    default:
                        Log.Warn("Unknown control message: " + (int)type);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error("Could not process control packet (sender=" + sender + "): " + e.Message);
                Transfers.DropFrom(sender);
                string msg = e.Message;
                Post(() => FireTransferError("received packet invalid: " + msg));
            }
        }

        static void HandleData(int sender, Packet p)
        {
            try
            {
                if (Main.Disabled) return;
                if (!RequireHandshake(sender)) return;
                int id;
                var rec = Transfers.OnData(sender, p, out id);
                if (rec != null) Post(() => Deliver(sender, rec, id));
            }
            catch (Exception e)
            {
                Log.Error("Could not process data packet (sender=" + sender + "): " + e.Message);
                string msg = e.Message;
                Post(() => FireTransferError("received data invalid: " + msg));
            }
        }

        /// <summary>Host side requires sender handshaken; client side (sender 0) requires our own handshake.</summary>
        static bool RequireHandshake(int sender)
        {
            bool ok = sender == 0 ? HandshakeDone : _guids.ContainsKey(sender);
            if (!ok) Log.Debug("packet from sender without handshake ignored: " + sender);
            return ok;
        }

        static void Deliver(int sender, PlayerRecord rec, int transferId)
        {
            if (sender == 0) { Fire(OnRecordFromHost, rec, transferId); return; }
            string guid = GuidOf(sender);
            if (guid == null || !string.Equals(guid, rec.Guid, StringComparison.OrdinalIgnoreCase))
            {
                string m = "record guid mismatch (client " + sender + ")";
                Log.Error(m);
                SendError(sender, m);
                FireTransferError(m);
                return;
            }
            Fire(OnRecordFromClient, sender, rec);
        }

        // ---------------- per-frame ----------------
        internal static void Tick()
        {
            if (Main.Disabled) return;
            Transfers.PumpOut();
            double now = _clock.Elapsed.TotalSeconds;

            if (_helloDeadline > 0 && now > _helloDeadline && !HandshakeDone)
            {
                _helloDeadline = 0;
                Log.Warn("HelloAck timeout (20 s)");
                FireHelloAck(false, "", "", "timeout");
            }

            if (now < _nextPoll) return;
            _nextPoll = now + 1;

            bool isClient = IsClient, isHost = IsHost;
            if (_wasClient && !isClient) ResetClient();
            if (_wasHost && !isHost) ResetHost();
            _wasClient = isClient; _wasHost = isHost;

            if (isHost)
            {
                var s = Server.Instance;
                foreach (int id in _guids.Keys.Concat(_pendingHello.Keys).Distinct().ToArray())
                {
                    ServerConnection sc;
                    if (!s.clients.TryGetValue(id, out sc) || sc == null || !sc.isConnected) ClientGone(id);
                }
            }
            var stale = Transfers.ExpireStale();
            if (stale != null) foreach (var m in stale) { Log.Warn(m); FireTransferError(m); }
        }

        /// <summary>Host: stop treating this client as separated (failed apply / error before a successful ack).</summary>
        public static void ForgetClient(int clientId)
        {
            bool had = _guids.Remove(clientId) | _pendingHello.Remove(clientId);
            Transfers.DropTarget(clientId);
            Transfers.DropFrom(clientId);
            if (had) Log.Msg("Separation closed: client " + clientId);
        }

        static void ClientGone(int id)
        {
            bool had = _guids.Remove(id) | _pendingHello.Remove(id);
            Transfers.DropTarget(id);
            Transfers.DropFrom(id);
            if (had) { Log.Msg("Player left: client " + id); Fire(OnClientGone, id); }
        }

        static void ResetHost()
        {
            foreach (int id in _guids.Keys.Concat(_pendingHello.Keys).Distinct().ToArray()) ClientGone(id);
            _guids.Clear(); _pendingHello.Clear();
        }

        static void ResetClient()
        {
            HandshakeDone = false;
            _helloDeadline = 0;
            Transfers.DropTarget(0);
            Transfers.DropFrom(0);
        }

        // ---------------- event helpers ----------------
        static void Post(Action a) { Main.Post(a); }

        static void FireHelloAck(bool ok, string ver, string cfg, string reason) { Fire(OnHelloAck, ok, ver, cfg, reason); }
        static void FireTransferError(string msg) { Fire(OnTransferError, msg); }

        static void Fire(Delegate d, params object[] args)
        {
            if (d == null) return;
            foreach (var h in d.GetInvocationList())
            {
                try { h.DynamicInvoke(args); }
                catch (Exception e) { Log.Error("Event subscriber error (" + h.Method.Name + "): " + (e.InnerException ?? e)); }
            }
        }
    }

    /// <summary>Re-registers our packet IDs whenever Together rebuilds its handler tables (same trick as FixForTogether).</summary>
    public class NetPatches : IPatchGroup
    {
        public string Name { get { return "Net"; } }

        public void Apply(Hx h)
        {
            Patch(h, typeof(Client), "InitializeClientData", "ClientInitPostfix");
            Patch(h, typeof(Server), "InitializeServerData", "ServerInitPostfix");
        }

        static void Patch(Hx h, Type t, string target, string postfix)
        {
            var m = AccessTools.Method(t, target);
            if (m == null) { Log.Error("Patch target not found: " + t.Name + "." + target); return; }
            var hm = new HarmonyMethod(typeof(NetPatches).GetMethod(postfix)) { priority = Priority.Last };
            h.Patch(m, postfix: hm);
        }

        public static void ClientInitPostfix()
        {
            try { Net.OnClientTablesRebuilt(); } catch (Exception e) { Log.Error("ClientInit postfix: " + e); }
        }

        public static void ServerInitPostfix()
        {
            try { Net.OnServerTablesRebuilt(); } catch (Exception e) { Log.Error("ServerInit postfix: " + e); }
        }
    }
}

