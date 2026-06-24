using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SVSimLoader.Patches;

/// <summary>
/// Diagnostic for the battle-node `spin` investigation (gated on EnableSpinProbe). It records, per
/// battle frame, the cumulative shared-RNG draw tally (BattleManagerBase.stableRandomCount) alongside
/// the inbound `spin` crank count, so per-turn `count` deltas can be compared against `spin` offline.
///
/// Why this settles the open question: every shared-RNG draw funnels through StableRandom/
/// StableRandomDouble, which increment stableRandomCount — so that field is an EXACT local draw count
/// (decomp: BattleManagerBase.cs:1581,1592; the only `_stableRandom.` callers). The receiver cranks the
/// shared RNG `spin` times before dispatching each frame (OperateReceive.StartOperate:80-84). If our own
/// turns advance `count` by ~the magnitude of the `spin` the opponent's turns hand us, the draws are
/// client-side and `spin` is plausibly wire-derivable; if our local deltas stay near zero while inbound
/// `spin` is in the tens–hundreds, prod authors `spin` from server-side simulation (≈ an engine).
///
/// Run against PROD (servers live until end of June 2026) to capture real `spin`; a local Bot/AI battle
/// only ever sends spin=0. Output: spin-rng.ndjson in the capture session dir.
/// See docs/audits/battle-node-spin-rng-model-2026-06-04.md.
/// </summary>
[HarmonyPatch]
public static class SpinProbe
{
    // stableRandomCount is a private int on BattleManagerBase — read it reflectively.
    private static readonly FieldInfo CountField =
        AccessTools.Field(typeof(BattleManagerBase), "stableRandomCount");

    /// <summary>Current cumulative shared-RNG draw tally, or -1 when no battle manager is live / unreadable.</summary>
    private static int ReadCount()
    {
        try
        {
            var mgr = BattleManagerBase.GetIns();
            if (mgr == null || CountField == null) return -1;
            return (int)CountField.GetValue(mgr);
        }
        catch
        {
            return -1;
        }
    }

    // Receive side: logged BEFORE the spin crank runs, so `count` is the tally entering this frame and
    // `spin` is the value about to be applied.
    [HarmonyPatch(typeof(OperateReceive), nameof(OperateReceive.StartOperate))]
    [HarmonyPrefix]
    public static void OnReceiveFrame(NetworkBattleReceiver.ReceiveData receivedData)
    {
        if (!SvSimConfig.EnableSpinProbe || receivedData == null) return;
        try
        {
            CaptureWriter.AppendSpinProbe("receive", $"{receivedData.dataUri}", receivedData.spin, ReadCount());
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    // Send side: our own emitted frames. `count` shows how much our turn's actions advanced the tally;
    // `spin` is not applicable on a send (-1).
    [HarmonyPatch(typeof(NetworkBattleSender), "EmitMsg")]
    [HarmonyPrefix]
    public static void OnSendFrame(NetworkBattleDefine.NetworkBattleURI uri, Dictionary<string, object> dataList = null)
    {
        if (!SvSimConfig.EnableSpinProbe) return;
        try
        {
            CaptureWriter.AppendSpinProbe("send", $"{uri}", -1, ReadCount());
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }
}
