using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using CMS21Together.Shared;

namespace TogetherSeparateEconomy
{
    /// <summary>Chunked record transfer: outgoing paced queue + incoming reassembly with SHA1 check.</summary>
    static class Transfers
    {
        public const int ChunkSize = 8192;
        public const int MaxTotal = 130 << 20;
        const double IncomingTimeoutSec = 90;
        const double ChunkIntervalSec = 0.02;

        // ---------- outgoing ----------
        class OutItem { public int Target; public Packet Pkt; public bool IsData; }
        static readonly Queue<OutItem> _out = new Queue<OutItem>();
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static double _nextData;
        static int _nextId = new Random().Next(1, 1 << 20);

        public static int QueuedCount { get { return _out.Count; } }

        /// <summary>Queue a whole record transfer (Begin, chunks, End). target 0 = to host (client side), else server client id.</summary>
        public static int Enqueue(int target, PlayerRecord rec)
        {
            byte[] data = rec.ToBytes();
            if (data.Length > MaxTotal) throw new InvalidOperationException("record too large: " + data.Length);
            int id = _nextId++;
            int chunks = (data.Length + ChunkSize - 1) / ChunkSize;
            if (chunks == 0) chunks = 1;

            var begin = new Packet(Net.PktControl);
            begin.Write((int)MsgType.RecordBegin);
            begin.Write(id); begin.Write(data.Length); begin.Write(chunks); begin.Write((int)rec.Kind);
            _out.Enqueue(new OutItem { Target = target, Pkt = begin });

            for (int i = 0; i < chunks; i++)
            {
                int off = i * ChunkSize;
                int len = Math.Min(ChunkSize, data.Length - off);
                var p = new Packet(Net.PktData);
                p.Write(id); p.Write(i);
                PW.Bytes(p, data, off, Math.Max(0, len));
                _out.Enqueue(new OutItem { Target = target, Pkt = p, IsData = true });
            }

            var end = new Packet(Net.PktControl);
            end.Write((int)MsgType.RecordEnd);
            end.Write(id);
            using (var sha = SHA1.Create()) PW.Bytes(end, sha.ComputeHash(data), 0, 20);
            _out.Enqueue(new OutItem { Target = target, Pkt = end });
            return id;
        }

        /// <summary>Called every frame: sends queued packets in order, at most one data chunk per ~20 ms.</summary>
        public static void PumpOut()
        {
            while (_out.Count > 0)
            {
                var it = _out.Peek();
                double now = _clock.Elapsed.TotalSeconds;
                if (it.IsData && now < _nextData) return;
                _out.Dequeue();
                if (it.IsData) _nextData = now + ChunkIntervalSec;
                Net.RawSend(it.Target, it.Pkt, true);
            }
        }

        public static void DropTarget(int target)
        {
            if (_out.Count == 0) return;
            var keep = new Queue<OutItem>();
            foreach (var it in _out) if (it.Target != target) keep.Enqueue(it);
            _out.Clear();
            foreach (var it in keep) _out.Enqueue(it);
        }

        public static void ClearAll() { _out.Clear(); _in.Clear(); }

        // ---------- incoming ----------
        class Incoming
        {
            public int Sender, Id, Total, ChunkCount, Kind;
            public byte[] Buf;
            public bool[] Got;
            public int GotCount;
            public byte[] Hash;      // set when End arrived
            public double Last;
        }
        static readonly Dictionary<long, Incoming> _in = new Dictionary<long, Incoming>();
        static long Key(int sender, int id) { return ((long)sender << 32) | (uint)id; }

        public static void OnBegin(int sender, Packet p)
        {
            int id = PR.Int(p), total = PR.Int(p), chunks = PR.Int(p), kind = PR.Int(p);
            if (total < 0 || total > MaxTotal) throw new InvalidOperationException("RecordBegin: invalid total " + total);
            int expect = Math.Max(1, (total + ChunkSize - 1) / ChunkSize);
            if (chunks != expect) throw new InvalidOperationException("RecordBegin: inconsistent chunk count " + chunks + "/" + expect);
            _in[Key(sender, id)] = new Incoming
            {
                Sender = sender, Id = id, Total = total, ChunkCount = chunks, Kind = kind,
                Buf = new byte[total], Got = new bool[chunks], Last = _clock.Elapsed.TotalSeconds
            };
            Log.Debug("Transfer started id=" + id + " total=" + total + " chunks=" + chunks + " sender=" + sender);
        }

        /// <summary>Returns the finished record (non-null) when this chunk completed a transfer that already had its End.</summary>
        public static PlayerRecord OnData(int sender, Packet p, out int doneId)
        {
            doneId = 0;
            int id = PR.Int(p), idx = PR.Int(p);
            Incoming t;
            if (!_in.TryGetValue(Key(sender, id), out t)) throw new InvalidOperationException("Unknown transfer id=" + id);
            if (idx < 0 || idx >= t.ChunkCount) throw new InvalidOperationException("Invalid chunk index " + idx);
            byte[] chunk = PR.Bytes(p, ChunkSize);
            int off = idx * ChunkSize;
            int expect = Math.Min(ChunkSize, t.Total - off);
            if (chunk.Length != Math.Max(0, expect)) { _in.Remove(Key(sender, id)); throw new InvalidOperationException("Wrong chunk size idx=" + idx); }
            if (!t.Got[idx]) { Buffer.BlockCopy(chunk, 0, t.Buf, off, chunk.Length); t.Got[idx] = true; t.GotCount++; }
            t.Last = _clock.Elapsed.TotalSeconds;
            return t.Hash != null && t.GotCount == t.ChunkCount ? Finish(t, out doneId) : null;
        }

        public static PlayerRecord OnEnd(int sender, Packet p, out int doneId)
        {
            doneId = 0;
            int id = PR.Int(p);
            byte[] hash = PR.Bytes(p, 64);
            Incoming t;
            if (!_in.TryGetValue(Key(sender, id), out t)) throw new InvalidOperationException("RecordEnd: unknown transfer id=" + id);
            t.Hash = hash;
            return t.GotCount == t.ChunkCount ? Finish(t, out doneId) : null;
        }

        static PlayerRecord Finish(Incoming t, out int doneId)
        {
            doneId = t.Id;
            _in.Remove(Key(t.Sender, t.Id));
            byte[] actual;
            using (var sha = SHA1.Create()) actual = sha.ComputeHash(t.Buf);
            if (actual.Length != t.Hash.Length) throw new InvalidOperationException("Checksum length mismatch");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != t.Hash[i]) throw new InvalidOperationException("Checksum mismatch (id=" + t.Id + "), transfer rejected");
            return PlayerRecord.FromBytes(t.Buf);
        }

        public static void DropFrom(int sender)
        {
            var dead = new List<long>();
            foreach (var kv in _in) if (kv.Value.Sender == sender) dead.Add(kv.Key);
            foreach (var k in dead) _in.Remove(k);
        }

        /// <summary>Returns descriptions of timed-out incoming transfers (and removes them).</summary>
        public static List<string> ExpireStale()
        {
            List<string> res = null;
            List<long> dead = null;
            double now = _clock.Elapsed.TotalSeconds;
            foreach (var kv in _in)
                if (now - kv.Value.Last > IncomingTimeoutSec)
                {
                    if (dead == null) { dead = new List<long>(); res = new List<string>(); }
                    dead.Add(kv.Key);
                    res.Add("transfer timed out id=" + kv.Value.Id + " sender=" + kv.Value.Sender);
                }
            if (dead != null) foreach (var k in dead) _in.Remove(k);
            return res;
        }
    }
}

