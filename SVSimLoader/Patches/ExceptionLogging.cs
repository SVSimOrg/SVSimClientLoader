extern alias game;
using System.Reflection;
using HarmonyLib;
using LitJson;
using Application = game::UnityEngine.Application;
using LogType = game::UnityEngine.LogType;
using StackTraceLogType = game::UnityEngine.StackTraceLogType;

namespace SVSimLoader.Patches;

[HarmonyPatch]
public class ExceptionLogging
{
    private static string _lastResponseJson;

    public static void Install()
    {
        Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        Application.logMessageReceived += OnLog;
    }

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error) return;
        if (message != null && message.StartsWith("[SVSim]")) return;

        Plugin.Log.LogError($"[SVSim] Unity {type}: {message}\n{stackTrace}");

        if (_lastResponseJson != null)
        {
            var snippet = _lastResponseJson.Length > 4000
                ? _lastResponseJson.Substring(0, 4000) + "…[truncated]"
                : _lastResponseJson;
            Plugin.Log.LogError($"[SVSim] Last parsed response JSON:\n{snippet}");
        }
    }

    // JsonMapper.ToObject(string) is ambiguous with the generic ToObject<T>(string) overload
    // when resolved through Harmony's attribute; pick the non-generic one explicitly.
    private static MethodBase TargetMethod()
    {
        foreach (var m in typeof(JsonMapper).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != nameof(JsonMapper.ToObject)) continue;
            if (m.IsGenericMethod) continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return m;
        }
        return null;
    }

    [HarmonyPostfix]
    public static void CaptureJson(string json)
    {
        _lastResponseJson = json;
    }
}
