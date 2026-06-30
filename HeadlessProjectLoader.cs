using System.Reflection;
using System.Xml.Linq;

namespace SurvivalcraftServer;

internal sealed partial class HeadlessBootstrap
{
    private void LoadOrCreateWorld()
    {
        var worldName = _options.WorldName;
        var worldDirectoryName = HeadlessStoragePaths.ToConfigPath(Path.GetFullPath(_options.DataDir), _options.WorldDir);
        var worldsManager = _game.GetType("Game.WorldsManager", throwOnError: true)!;
        object? worldInfo = null;

        if (worldInfo is null && StorageDirectoryExists(worldDirectoryName))
        {
            worldInfo = worldsManager.GetMethod("GetWorldInfo", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!
                .Invoke(null, [worldDirectoryName]);
        }

        if (worldInfo is null)
        {
            var worldSettingsType = _game.GetType("Game.WorldSettings", throwOnError: true)!;
            var settings = Activator.CreateInstance(worldSettingsType)!;
            worldSettingsType.GetField("Name")!.SetValue(settings, worldName);
            worldSettingsType.GetField("Seed")!.SetValue(settings, ResolveWorldSeed());
            worldSettingsType.GetField("MaxOnlinePlayerCount")!.SetValue(settings, (ushort)_options.MaxPlayers);
            worldSettingsType.GetField("RunServer")!.SetValue(settings, true);
            var createWorld = worldsManager.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "CreateWorld")
                    {
                        return false;
                    }
                    var parameters = method.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType == worldSettingsType;
                })
                ?? throw new MissingMethodException("Game.WorldsManager", "CreateWorld(WorldSettings, string)");
            object?[] createArgs = createWorld.GetParameters().Length == 1 ? [settings] : [settings, worldDirectoryName];
            worldInfo = createWorld.Invoke(null, createArgs);
        }

        LoadHeadlessProject(worldInfo!);
    }

    private void LoadHeadlessProject(object worldInfo)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        InvokeStatic(_game, "Game.GameManager", "DisposeProject");
        var worldsManager = _game.GetType("Game.WorldsManager", throwOnError: true)!;
        worldsManager.GetMethod("RepairWorldIfNeeded", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [GetFieldValue<string>(worldInfo, "DirectoryName")]);
        _game.GetType("Game.VersionsManager", throwOnError: true)!
            .GetMethod("UpgradeWorld", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [GetFieldValue<string>(worldInfo, "DirectoryName")]);

        var projectData = CreateProjectData(worldInfo);
        RemoveProjectSubsystem(projectData, "SubsystemModelsRenderer");

        var databaseManager = _game.GetType("Game.DatabaseManager", throwOnError: true)!;
        var gameDatabase = databaseManager.GetProperty("GameDatabase", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var projectNetType = _game.GetType("Game.NetWork.ProjectNet", throwOnError: true)!;
        SetCommonLibWorkType("Server");
        var project = Activator.CreateInstance(projectNetType, [gameDatabase, projectData])!;
        var subsystemUpdateType = _game.GetType("Game.SubsystemUpdate", throwOnError: true)!;
        var subsystemUpdate = projectNetType.GetMethod("FindSubsystem", BindingFlags.Public | BindingFlags.Instance, [typeof(Type), typeof(string), typeof(bool)])!
            .Invoke(project, [subsystemUpdateType, null, true]);

        var gameManager = _game.GetType("Game.GameManager", throwOnError: true)!;
        gameManager.GetField("m_project", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, project);
        gameManager.GetField("m_subsystemUpdate", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, subsystemUpdate);
        gameManager.GetField("m_worldInfo", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, worldInfo);
    }

    private void SetCommonLibWorkType(string workTypeName)
    {
        var commonLibType = _game.GetType("Game.NetWork.CommonLib", throwOnError: true)!;
        var net = commonLibType.GetField("Net", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            ?? throw new InvalidOperationException("Game.NetWork.CommonLib.Net 为空。");
        var workTypeProperty = net.GetType().GetProperty("WorkType", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("Game.NetWork.NetNode", "WorkType");
        workTypeProperty.SetValue(net, Enum.Parse(workTypeProperty.PropertyType, workTypeName));
    }

    private object CreateProjectData(object worldInfo)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        var directoryName = GetFieldValue<string>(worldInfo, "DirectoryName");
        var projectJson = CombineStoragePath(directoryName, "Project.json");
        var projectXml = CombineStoragePath(directoryName, "Project.xml");
        var projectMpk = CombineStoragePath(directoryName, "Project.mpk");
        var projectBak = projectJson + ".bak";

        var databaseManager = _game.GetType("Game.DatabaseManager", throwOnError: true)!;
        var gameDatabase = databaseManager.GetProperty("GameDatabase", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var overrides = CreateLoadProjectOverrides(directoryName);
        var projectDataType = ResolveType("GameEntitySystem.ProjectData");

        if (InvokeStaticMethod<bool>(_engine, "Engine.Storage", "FileExists", [projectJson]))
        {
            var json = InvokeStaticMethod<string>(_engine, "Engine.Storage", "ReadAllText", [projectJson]);
            var valuesDictionaryType = ResolveType("TemplatesDatabase.ValuesDictionary");
            var valuesDictionary = Activator.CreateInstance(valuesDictionaryType)!;
            object?[] args = [json, null];
            valuesDictionaryType.GetMethod("ApplyOverridesUseJson", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(valuesDictionary, args);
            var data = (byte[])args[1]!;
            return Activator.CreateInstance(projectDataType, [gameDatabase, data, overrides, true])!;
        }

        if (InvokeStaticMethod<bool>(_engine, "Engine.Storage", "FileExists", [projectBak]))
        {
            var json = InvokeStaticMethod<string>(_engine, "Engine.Storage", "ReadAllText", [projectBak]);
            var valuesDictionaryType = ResolveType("TemplatesDatabase.ValuesDictionary");
            var valuesDictionary = Activator.CreateInstance(valuesDictionaryType)!;
            object?[] args = [json, null];
            valuesDictionaryType.GetMethod("ApplyOverridesUseJson", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(valuesDictionary, args);
            var data = (byte[])args[1]!;
            return Activator.CreateInstance(projectDataType, [gameDatabase, data, overrides, true])!;
        }

        if (InvokeStaticMethod<bool>(_engine, "Engine.Storage", "FileExists", [projectMpk]))
        {
            using var stream = OpenStorageFile(projectMpk, "Read");
            var data = new byte[stream.Length];
            _ = stream.Read(data, 0, data.Length);
            return Activator.CreateInstance(projectDataType, [gameDatabase, data, overrides, true])!;
        }

        if (InvokeStaticMethod<bool>(_engine, "Engine.Storage", "FileExists", [projectXml]))
        {
            using var stream = OpenStorageFile(projectXml, "Read");
            var node = XElement.Load(stream);
            return Activator.CreateInstance(projectDataType, [gameDatabase, node, overrides, true])!;
        }

        throw new FileNotFoundException("未找到 Project.json/Project.mpk/Project.xml", projectJson);
    }

    private object CreateLoadProjectOverrides(string directoryName)
    {
        var valuesDictionaryType = ResolveType("TemplatesDatabase.ValuesDictionary");
        var gamesWidgetType = _game.GetType("Game.GamesWidget", throwOnError: true)!;
        var root = Activator.CreateInstance(valuesDictionaryType)!;
        var gameInfo = Activator.CreateInstance(valuesDictionaryType)!;
        var views = Activator.CreateInstance(valuesDictionaryType)!;
        valuesDictionaryType.GetMethod("SetValue")!.MakeGenericMethod(typeof(object)).Invoke(root, ["GameInfo", gameInfo]);
        valuesDictionaryType.GetMethod("SetValue")!.MakeGenericMethod(typeof(object)).Invoke(gameInfo, ["WorldDirectoryName", directoryName]);
        valuesDictionaryType.GetMethod("SetValue")!.MakeGenericMethod(typeof(object)).Invoke(root, ["Views", views]);
        valuesDictionaryType.GetMethod("SetValue")!.MakeGenericMethod(typeof(object)).Invoke(views, ["GamesWidget", Activator.CreateInstance(gamesWidgetType)!]);
        return root;
    }

    private void RemoveProjectSubsystem(object projectData, string subsystemName)
    {
        var projectDataType = projectData.GetType();
        var valuesDictionary = projectDataType.GetField("ValuesDictionary", BindingFlags.Public | BindingFlags.Instance)!.GetValue(projectData)!;
        var backing = (System.Collections.IDictionary)valuesDictionary.GetType()
            .GetField("m_dictionary", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(valuesDictionary)!;
        var keysToRemove = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in backing)
        {
            if (entry.Key is not string key)
            {
                continue;
            }
            if (key == subsystemName)
            {
                keysToRemove.Add(key);
                continue;
            }
            if (entry.Value is null)
            {
                continue;
            }
            var childBackingField = entry.Value.GetType().GetField("m_dictionary", BindingFlags.NonPublic | BindingFlags.Instance);
            if (childBackingField?.GetValue(entry.Value) is not System.Collections.IDictionary childBacking)
            {
                continue;
            }
            if (childBacking["Class"] is string className
                && (className == "Game." + subsystemName || className.EndsWith("." + subsystemName, StringComparison.Ordinal)))
            {
                keysToRemove.Add(key);
            }
        }
        foreach (var key in keysToRemove)
        {
            backing.Remove(key);
        }
        if (keysToRemove.Count > 0)
        {
            ServerDiagnostics.Debug($"Headless: 已移除项目子系统 {string.Join(", ", keysToRemove)} ({subsystemName})");
        }
    }

    private Stream OpenStorageFile(string path, string modeName)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        var openFileModeType = _engine.GetType("Engine.OpenFileMode", throwOnError: true)!;
        var mode = Enum.Parse(openFileModeType, modeName);
        return (Stream)_engine.GetType("Engine.Storage", throwOnError: true)!
            .GetMethod("OpenFile", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [path, mode])!;
    }

    private bool StorageDirectoryExists(string path)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        return InvokeStaticMethod<bool>(_engine, "Engine.Storage", "DirectoryExists", [path]);
    }

    private static string CombineStoragePath(string left, string right)
    {
        return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\');
    }

    private string ResolveWorldSeed()
    {
        return string.IsNullOrWhiteSpace(_options.Seed) ? Random.Shared.NextInt64().ToString() : _options.Seed;
    }

}
