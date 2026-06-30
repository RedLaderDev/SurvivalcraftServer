using System.Reflection;
using System.Runtime.CompilerServices;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private void InitializeBuiltInModOnly(string assetsRoot)
    {
        var modsManager = _game.GetType("Game.ModsManager", throwOnError: true)!;
        ClearStaticList(modsManager, "ModListAll");
        ClearStaticList(modsManager, "ModList");
        ClearStaticList(modsManager, "ModLoaders");
        ClearStaticDictionary(modsManager, "ModHooks");
        var builtIn = CreateSurvivalCraftModEntity(assetsRoot);
        modsManager.GetField("SurvivalCraftModEntity", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, builtIn);
        AddToStaticList(modsManager, "ModListAll", builtIn);
        AddToStaticList(modsManager, "ModList", builtIn);
    }

    private object CreateSurvivalCraftModEntity(string assetsRoot)
    {
        var type = _game.GetType("Game.SurvivalCraftModEntity", throwOnError: true)!;
        var builtIn = RuntimeHelpers.GetUninitializedObject(type);
        var archive = ManualLoadContentScpak(assetsRoot);
        type.GetField("ModArchive", BindingFlags.Public | BindingFlags.Instance)!.SetValue(builtIn, archive);
        type.GetField("ModFilePath", BindingFlags.Public | BindingFlags.Instance)!.SetValue(builtIn, "app:Content.scpak");
        type.GetField("ModFiles", BindingFlags.Public | BindingFlags.Instance)!.SetValue(
            builtIn,
            Activator.CreateInstance(type.GetField("ModFiles", BindingFlags.Public | BindingFlags.Instance)!.FieldType));
        type.GetField("Blocks", BindingFlags.Public | BindingFlags.Instance)!.SetValue(
            builtIn,
            Activator.CreateInstance(type.GetField("Blocks", BindingFlags.Public | BindingFlags.Instance)!.FieldType));
        type.GetField("ResourcesMd5", BindingFlags.Public | BindingFlags.Instance)!.SetValue(builtIn, string.Empty);
        type.GetMethod("InitResources", BindingFlags.Public | BindingFlags.Instance)!.Invoke(builtIn, []);
        type.GetMethod("LoadDll", BindingFlags.Public | BindingFlags.Instance)!.Invoke(builtIn, []);
        ServerDiagnostics.Debug("已用手动 Content.scpak 初始化内置 SurvivalCraft mod。");
        return builtIn;
    }
}
