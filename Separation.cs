using System.Diagnostics;

namespace TogetherSeparateEconomy
{
    /// <summary>Shared state read by every filter. All access is on the Unity main thread.</summary>
    public static class Separation
    {
        /// <summary>Client side: our record was applied in this connection (outgoing sync blocked, incoming ignored).</summary>
        public static bool ClientActive;
        /// <summary>True only while we run the game's Inventory.Load for our own record.</summary>
        public static bool Applying;
        /// <summary>&gt; 0 while a received Together/Fixes packet handler is executing.</summary>
        public static int RemoteDepth;
        /// <summary>Set whenever any inventory-related packet arrived (seconds on <see cref="Now"/>); negative = never.</summary>
        public static double LastInventoryPacketTime = -1000;
        /// <summary>Inventory changed locally since the last snapshot.</summary>
        public static bool InventoryDirty;
        public static bool WarehouseDirty;

        static readonly Stopwatch _clock = Stopwatch.StartNew();
        public static double Now { get { return _clock.Elapsed.TotalSeconds; } }

        public static bool HostActive { get { return Net.IsHost && Net.HandshakenClientCount > 0; } }

        /// <summary>The separation currently shapes sync traffic on this machine.</summary>
        public static bool Active
        {
            get { return !Main.Disabled && ((Net.IsClient && ClientActive) || HostActive); }
        }

        public static void NoteInventoryPacket() { LastInventoryPacketTime = Now; }
    }
}
