using System;
using Cute;
using HarmonyLib;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public class DecryptPatch
{
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Connect), typeof(NetworkTask), typeof(Action<NetworkTask.ResultCode>), typeof(Action<NetworkTask.ResultCode>), typeof(Action<int>), typeof(bool), typeof(bool), typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    public static bool Connect(ref bool encrypt)
    {
        encrypt = !SvSimConfig.DisableEncryption && encrypt;
        Plugin.Log.LogInfo($"encryption is {encrypt}");
        return true;
    }
}