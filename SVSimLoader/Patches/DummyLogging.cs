using System;
using Cute;
using HarmonyLib;
using Wizard;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public class DummyLogging
{
    [HarmonyPatch(typeof(LocalLog), "MakeTreceLogToSend")]
    [HarmonyPrefix]
    public static bool MakeTreceLogToSend(Action onSended)
    {
        onSended.Call();
        return false;
    }
}