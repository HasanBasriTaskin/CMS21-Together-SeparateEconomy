using System;
using System.Text;
using CMS21Together.Shared;

namespace TogetherSeparateEconomy
{
    public enum MsgType
    {
        Hello = 1,          // client->host: guid, username, modVersion, config
        HelloAck = 2,       // host->client: accepted, modVersion, config, reason
        RequestRecord = 3,  // client->host
        RecordBegin = 4,    // either way: transferId, totalLength, chunkCount, kind
        RecordEnd = 5,      // either way: transferId, sha1
        ApplyAck = 6,       // client->host: transferId, ok
        Error = 7,          // either way: text
    }

    /// <summary>Packet helpers. Strings are UTF-8 with int length prefix; bool is an int; never uses generic Write&lt;T&gt;.</summary>
    static class PW
    {
        public static void Str(Packet p, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            p.Write(b.Length);
            if (b.Length > 0) p.Write(b);
        }
        public static void Bool(Packet p, bool v) { p.Write(v ? 1 : 0); }
        public static void Bytes(Packet p, byte[] b, int offset, int count)
        {
            p.Write(count);
            if (count == 0) return;
            if (offset == 0 && count == b.Length) p.Write(b);
            else { var c = new byte[count]; Buffer.BlockCopy(b, offset, c, 0, count); p.Write(c); }
        }
    }

    static class PR
    {
        const int MaxStr = 4096;
        public static int Int(Packet p)
        {
            if (p.UnreadLength() < 4) throw new InvalidOperationException("packet truncated (int)");
            return p.ReadInt(true);
        }
        public static bool Bool(Packet p) { return Int(p) != 0; }
        public static string Str(Packet p)
        {
            int n = Int(p);
            if (n < 0 || n > MaxStr || n > p.UnreadLength()) throw new InvalidOperationException("invalid packet string length: " + n);
            return n == 0 ? "" : Encoding.UTF8.GetString(p.ReadBytes(n, true));
        }
        public static byte[] Bytes(Packet p, int max)
        {
            int n = Int(p);
            if (n < 0 || n > max || n > p.UnreadLength()) throw new InvalidOperationException("invalid packet byte length: " + n);
            return n == 0 ? new byte[0] : p.ReadBytes(n, true);
        }
    }
}
