using System.Reflection;

namespace SurvivalcraftServer.Server;

internal static class ServerProjectSyncHook
{
    public static void EnsureInstalled(Assembly gameAssembly, object netNode)
    {
        var projectNetType = gameAssembly.GetType("Game.NetWork.ProjectNet", throwOnError: true)!;
        var project = projectNetType.GetField("project", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            ?? throw new InvalidOperationException("Game.NetWork.ProjectNet.project 为空，无法挂接项目同步 hook。");

        EnsureDelegate(netNode, project, "OnClientStateChanged");
        EnsureDelegate(netNode, project, "GrantClient");
    }

    private static void EnsureDelegate(object netNode, object target, string methodName)
    {
        var field = netNode.GetType().GetField("OnClientStateChanged", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("Game.NetWork.NetNode", "OnClientStateChanged");
        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var handler = Delegate.CreateDelegate(field.FieldType, target, method);
        var current = (Delegate?)field.GetValue(netNode);
        if (Contains(current, target, method))
        {
            ServerDiagnostics.Debug($"[SYNC] 已存在 ProjectNet.{methodName} hook。");
            return;
        }

        field.SetValue(netNode, Delegate.Combine(current, handler));
        ServerDiagnostics.Debug($"[SYNC] 已挂接 ProjectNet.{methodName} hook。");
    }

    private static bool Contains(Delegate? current, object target, MethodInfo method)
    {
        if (current is null)
        {
            return false;
        }

        return current.GetInvocationList().Any(item => ReferenceEquals(item.Target, target) && item.Method == method);
    }
}
