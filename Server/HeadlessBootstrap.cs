using System.Reflection;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private readonly SurvivalcraftLoadContext _context;
    private readonly Assembly _game;
    private readonly ServerOptions _options;
    private Assembly? _engine;
    private MethodInfo? _gameManagerUpdateProject;
    private object? _netNode;
    private MethodInfo? _netNodeUpdate;

    public HeadlessBootstrap(SurvivalcraftLoadContext context, Assembly game, ServerOptions options)
    {
        _context = context;
        _game = game;
        _options = options;
    }

    public void Initialize()
    {
        _engine = _context.LoadFromAssemblyPath(Path.Combine(_options.LibDir, "Engine.dll"));
        var dataRoot = Path.GetFullPath(_options.DataDir);
        var assetsRoot = Path.GetFullPath(_options.AssetsDir);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(HeadlessStoragePaths.ResolveDataPath(dataRoot, _options.FurniturePacksDir));
        Directory.CreateDirectory(HeadlessStoragePaths.ResolveDataPath(dataRoot, _options.CharacterSkinsDir));
        Directory.CreateDirectory(HeadlessStoragePaths.ResolveDataPath(dataRoot, _options.TexturePacksDir));

        SetStaticField(_engine, "Engine.EngineActivity", "BasePath", dataRoot);
        SetStaticField(_engine, "Engine.EngineActivity", "ConfigPath", dataRoot);

        InitializeEngineFrameHooks(_engine);
        InstallHeadlessLogSinks();
        SetDisplayViewport(_engine);

        InitializeServerSettings();
        SetStaticProperty(_game, "Game.SettingsManager", "ServerPort", _options.Port);
        SetStaticProperty(_game, "Game.SettingsManager", "AllowLanConnection", true);
        SetStaticProperty(_game, "Game.SettingsManager", "DisplayLog", true);
        SetStaticProperty(_game, "Game.SettingsManager", "EnableMod", true);
        InstallHeadlessGraphicsStubs();
        InitializeScKeyTokenStorage();

        InvokeStatic(_game, "Game.ContentManager", "Initialize");
        InstallHeadlessContentCaches();
        InitializeBuiltInModOnly(assetsRoot);
        InvokeStatic(_game, "Game.NetWork.PackageManager", "Initialize");
        InitializeGameData();
        LoadOrCreateWorld();
        DeleteEmptyDefaultWorldsDirectory(dataRoot);
        CacheTickMethods();
    }

    private void InitializeScKeyTokenStorage()
    {
        var tokenStorageType = _game.GetType("Game.ScKeyTokenStorage", throwOnError: false);
        tokenStorageType?.GetMethod("UseCurrentDirectoryConfig", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, [true]);
        tokenStorageType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, []);
    }

    public void Tick()
    {
        if (_engine is null)
        {
            return;
        }
        InvokeInternalStatic(_engine, "Engine.Time", "BeforeFrame");
        InvokeInternalStatic(_engine, "Engine.Dispatcher", "BeforeFrame");
        _gameManagerUpdateProject?.Invoke(null, []);
        _netNodeUpdate?.Invoke(_netNode, []);
        InvokeInternalStatic(_engine, "Engine.Dispatcher", "AfterFrame");
        InvokeInternalStatic(_engine, "Engine.Time", "AfterFrame");
    }

    private void CacheTickMethods()
    {
        var gameManager = _game.GetType("Game.GameManager", throwOnError: true)!;
        _gameManagerUpdateProject = gameManager.GetMethod("UpdateProject", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("Game.GameManager", "UpdateProject");

        var commonLib = _game.GetType("Game.NetWork.CommonLib", throwOnError: true)!;
        _netNode = commonLib.GetField("Net", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        _netNodeUpdate = _netNode?.GetType().GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
    }

    private static void DeleteEmptyDefaultWorldsDirectory(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "Worlds");
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }
}
