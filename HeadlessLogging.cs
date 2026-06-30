using System.Reflection;

namespace SurvivalcraftServer;

internal sealed partial class HeadlessBootstrap
{
    private void InstallHeadlessLogSinks()
    {
        if (_engine is null)
        {
            return;
        }

        var logType = _engine.GetType("Engine.Log", throwOnError: true)!;
        var logLevelType = _engine.GetType("Engine.LogType", throwOnError: true)!;
        var consoleLogSinkType = _engine.GetType("Engine.ConsoleLogSink", throwOnError: true)!;
        var debugLevel = Enum.Parse(logLevelType, "Debug");

        logType.GetProperty("MinimumLogType", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, debugLevel);
        logType.GetMethod("RemoveAllLogSinks", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, []);

        var consoleSink = Activator.CreateInstance(consoleLogSinkType)
            ?? throw new InvalidOperationException("无法创建 Engine.ConsoleLogSink。");
        consoleLogSinkType.GetProperty("MinimumLogType", BindingFlags.Public | BindingFlags.Instance)?.SetValue(consoleSink, debugLevel);
        logType.GetMethod("AddLogSink", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [consoleSink]);

        var msgAdded = logType.GetField("MsgAdded", BindingFlags.Public | BindingFlags.Static);
        if (msgAdded is not null)
        {
            Action<string> handler = message => ServerDiagnostics.Debug($"[GAMELOG] {message}");
            var existing = msgAdded.GetValue(null) as Delegate;
            msgAdded.SetValue(null, existing is null ? handler : Delegate.Combine(existing, handler));
        }

        ServerDiagnostics.Debug("[LOG] Engine.Log 控制台 sink 已启用，级别=Debug。");
    }
}
