using System;
using HarmonyLib;
using Hx = HarmonyLib.Harmony;

namespace TogetherSeparateEconomy
{
    /// <summary>
    /// Filter 6: money changes made by code that runs inside a received packet handler (remote job payouts, car sales...)
    /// are dropped while separation is active. Our own sets run outside handlers (RemoteDepth == 0).
    /// </summary>
    public class MoneyGuard : IPatchGroup
    {
        public string Name { get { return "MoneyGuard"; } }
        public void Apply(Hx h)
        {
            PatchUtil.Do(h, "GlobalData.AddPlayerMoney(int)", typeof(GlobalData), "AddPlayerMoney", new[] { typeof(int) }, "MoneyGuard.Block", null);
            PatchUtil.Do(h, "GlobalData.SetPlayerMoney(int)", typeof(GlobalData), "SetPlayerMoney", new[] { typeof(int) }, "MoneyGuard.Block", null);
            // set_PlayerMoney has no patchable native MethodInfo in this IL2CPP build (Harmony NRE in CopyMethodInfoStruct); remote paths use AddPlayerMoney.
        }

        public static bool Block()
        {
            try
            {
                if (Separation.RemoteDepth > 0 && Separation.Active && Config.SeparateMoney)
                {
                    Log.Debug("Remote money change blocked");
                    return false;
                }
            }
            catch (Exception e) { Log.Error("MoneyGuard: " + e.Message); }
            return true;
        }
    }
}
