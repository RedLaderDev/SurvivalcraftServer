using System.Reflection;

namespace SurvivalcraftServer;

internal sealed partial class HeadlessBootstrap
{
    private void InvokeOptionalStatic(string typeName, string methodName)
    {
        try
        {
            InvokeStatic(_game, typeName, methodName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] {typeName}.{methodName} 跳过: {Unwrap(ex).Message}");
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        return ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
    }

    private static void InvokeStatic(Assembly assembly, string typeName, string methodName, object[]? args = null)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var flags = BindingFlags.Public | BindingFlags.Static;
        var method = args is null
            ? type.GetMethod(methodName, flags, Type.EmptyTypes)
            : type.GetMethods(flags).FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
        if (method is null)
        {
            throw new MissingMethodException(typeName, methodName);
        }
        method.Invoke(null, args ?? []);
    }

    private static void InvokeInternalStatic(Assembly assembly, string typeName, string methodName)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeName, methodName);
        method.Invoke(null, []);
    }

    private static void SetStaticField(Assembly assembly, string typeName, string fieldName, object value)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
    }

    private static void SetStaticProperty(Assembly assembly, string typeName, string propertyName, object value)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);
    }

    private Type ResolveType(string fullName)
    {
        var type = _game.GetType(fullName, throwOnError: false) ?? _engine?.GetType(fullName, throwOnError: false);
        if (type is not null)
        {
            return type;
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }
        throw new TypeLoadException($"类型不存在: {fullName}");
    }

    private static T GetFieldValue<T>(object instance, string fieldName)
    {
        return (T)instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance)!;
    }

    private static T InvokeStaticMethod<T>(Assembly assembly, string typeName, string methodName, object[] args)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(typeName, methodName);
        return (T)method.Invoke(null, args)!;
    }

    private static void ClearStaticList(Type type, string fieldName)
    {
        var list = (System.Collections.IList)type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        list.Clear();
    }

    private static void AddToStaticList(Type type, string fieldName, object value)
    {
        var list = (System.Collections.IList)type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        list.Add(value);
    }

    private static void ClearStaticDictionary(Type type, string fieldName)
    {
        var dictionary = (System.Collections.IDictionary)type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        dictionary.Clear();
    }
}
