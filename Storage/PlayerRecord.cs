using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TogetherSeparateEconomy
{
    public enum RecordKind
    {
        /// <summary>Real saved data (inventory/warehouse/money).</summary>
        Normal = 0,
        /// <summary>First time this player is seen: apply new-player defaults (Money/Level/Exp/SkillPoints fields).</summary>
        NewPlayerDefaults = 1,
        /// <summary>Host has no record for this guid (client should upload its own local backup, if any).</summary>
        NoneExisting = 2,
    }

    /// <summary>
    /// One player's private state. Container: "TSE1" (legacy "AYRK" also accepted) | int headerLen | UTF-8 JSON header | int invLen | inv | int whLen | wh.
    /// </summary>
    public class PlayerRecord
    {
        public const int CurrentFormat = 1;
        const int MaxHeader = 1 << 20;
        const int MaxBlob = 64 << 20;

        public int FormatVersion = CurrentFormat;
        public string Guid = "";
        public string PlayerName = "";
        public long SavedAtUtcTicks;
        public int Money;
        /// <summary>Game ProfileData save version byte (inventory/warehouse bytes depend on it).</summary>
        public byte SaveVersion;
        public bool FirstSplitDone;
        public RecordKind Kind = RecordKind.Normal;
        public int Level;
        public int Exp;
        public int SkillPoints;
        /// <summary>True for the first-split copy of the host inventory: the client must give every item a fresh UID. Absent in old files = false.</summary>
        public bool RemapUids;

        [JsonIgnore] public byte[] InventoryBytes = new byte[0];
        [JsonIgnore] public byte[] WarehouseBytes = new byte[0];

        public static PlayerRecord Marker(RecordKind kind, string guid)
        {
            return new PlayerRecord { Kind = kind, Guid = guid ?? "", SavedAtUtcTicks = DateTime.UtcNow.Ticks };
        }

        public byte[] ToBytes()
        {
            string json = JsonConvert.SerializeObject(this);
            byte[] hdr = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new byte[] { (byte)'T', (byte)'S', (byte)'E', (byte)'1' });
                w.Write(hdr.Length); w.Write(hdr);
                var inv = InventoryBytes ?? new byte[0];
                var wh = WarehouseBytes ?? new byte[0];
                w.Write(inv.Length); w.Write(inv);
                w.Write(wh.Length); w.Write(wh);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Parses and validates. Throws InvalidDataException on any problem.</summary>
        public static PlayerRecord FromBytes(byte[] data)
        {
            if (data == null || data.Length < 16) throw new InvalidDataException("record too short");
            bool tse = data[0] == 'T' && data[1] == 'S' && data[2] == 'E' && data[3] == '1';
            bool legacy = data[0] == 'A' && data[1] == 'Y' && data[2] == 'R' && data[3] == 'K';
            if (!tse && !legacy) throw new InvalidDataException("bad magic");
            int pos = 4;
            int hl = ReadLen(data, ref pos, MaxHeader);
            Need(data, pos, hl);
            PlayerRecord r;
            try { r = JsonConvert.DeserializeObject<PlayerRecord>(Encoding.UTF8.GetString(data, pos, hl)); }
            catch (Exception e) { throw new InvalidDataException("header JSON corrupt: " + e.Message); }
            if (r == null) throw new InvalidDataException("header empty");
            pos += hl;
            if (r.FormatVersion < 1 || r.FormatVersion > CurrentFormat)
                throw new InvalidDataException("unsupported format " + r.FormatVersion);
            int il = ReadLen(data, ref pos, MaxBlob);
            Need(data, pos, il);
            r.InventoryBytes = new byte[il]; Buffer.BlockCopy(data, pos, r.InventoryBytes, 0, il); pos += il;
            int wl = ReadLen(data, ref pos, MaxBlob);
            Need(data, pos, wl);
            r.WarehouseBytes = new byte[wl]; Buffer.BlockCopy(data, pos, r.WarehouseBytes, 0, wl); pos += wl;
            if (pos != data.Length) throw new InvalidDataException("trailing data");
            if (r.Guid == null) r.Guid = "";
            if (r.PlayerName == null) r.PlayerName = "";
            return r;
        }

        static int ReadLen(byte[] d, ref int pos, int max)
        {
            Need(d, pos, 4);
            int v = BitConverter.ToInt32(d, pos); pos += 4;
            if (v < 0 || v > max) throw new InvalidDataException("invalid length " + v);
            return v;
        }

        static void Need(byte[] d, int pos, int n)
        {
            if (n < 0 || pos < 0 || (long)pos + n > d.Length) throw new InvalidDataException("record truncated");
        }
    }
}
