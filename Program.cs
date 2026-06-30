using System.Reflection;

namespace SurvivalcraftServer;

internal static class Program
{
    private static int Main(string[] args)
    {
        ServerOptions options;
        try
        {
            options = ServerOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }
        ServerDiagnostics.DebugEnabled = options.Debug;

        if (options.CheckLogin && (string.IsNullOrWhiteSpace(options.ScKeyServerId) || string.IsNullOrWhiteSpace(options.ScKeyToken)))
        {
            Console.Error.WriteLine("check_login = true 时必须配置 sc_key_server_id 和 sc_key_token。");
            return 2;
        }

        Directory.CreateDirectory(options.StorageDir);
        Environment.CurrentDirectory = options.StorageDir;
        ScKeyServerSettingsWriter.Write(options);

        var libDir = Path.GetFullPath(options.LibDir);
        var assetsDir = Path.GetFullPath(options.AssetsDir);
        try
        {
            BundledDirectoryExporter.ExportIfMissing("lib", libDir);
            BundledDirectoryExporter.ExportIfMissing("assets", assetsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"导出内置目录失败: {ex.Message}");
            return 2;
        }

        if (!Directory.Exists(libDir))
        {
            Console.Error.WriteLine($"lib目录不存在: {libDir}");
            return 2;
        }
        if (!Directory.Exists(assetsDir))
        {
            Console.Error.WriteLine($"assets目录不存在: {assetsDir}");
            return 2;
        }

        if (options.Command is "start" or "selftest")
        {
            libDir = PatchedDllSet.Prepare(options);
            options = options with { LibDir = libDir };
            Environment.SetEnvironmentVariable("SCNET_ASSETS_DIR", assetsDir);
            Environment.SetEnvironmentVariable("SCNET_HEADLESS_GRAPHICS", "1");
        }

        var context = new SurvivalcraftLoadContext(libDir);
        try
        {
            var survivalcraft = context.LoadFromAssemblyPath(Path.Combine(libDir, "Survivalcraft.dll"));
            if (options.Command == "probe" || options.Debug)
            {
                PrintAssemblyProbe(survivalcraft, libDir);
            }

            if (options.Command == "probe")
            {
                return 0;
            }

            return options.Command == "selftest"
                ? RunSelfTest(context, survivalcraft, options)
                : TryStartServer(context, survivalcraft, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败:");
            PrintException(ex);
            return 1;
        }
        finally
        {
            context.Unload();
        }
    }

    private static int TryStartServer(SurvivalcraftLoadContext context, Assembly survivalcraft, ServerOptions options)
    {
        Console.WriteLine($"启动 Survivalcraft headless server，端口: {options.Port}");
        Console.WriteLine($"Storage: {options.StorageDir}");
        Console.WriteLine($"World: {options.WorldName}, WorldDir: {options.WorldDir}, DataDir: {options.DataDir}");

        var bootstrap = new HeadlessBootstrap(context, survivalcraft, options);
        bootstrap.Initialize();

        var commonLibType = RequireType(survivalcraft, "Game.NetWork.CommonLib");
        var instance = commonLibType.GetField("Net", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            ?? throw new InvalidOperationException("Game.NetWork.CommonLib.Net 为空。");
        var netNodeType = instance.GetType();

        var startServer = netNodeType.GetMethod("StartServer", BindingFlags.Public | BindingFlags.Instance, [typeof(int)])
            ?? throw new MissingMethodException("Game.NetWork.NetNode", "StartServer(int)");

        try
        {
            var result = startServer.Invoke(instance, [options.Port]);
            Console.WriteLine($"StartServer 返回: {result}");
            if (result is true)
            {
                ServerProjectSyncHook.EnsureInstalled(survivalcraft, instance);
                ServerPacketLogger.Install(survivalcraft, instance);
                TerrainRuntimeLogger.Install();
                ServerJoinLogger.Install(instance);
            }
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine("StartServer 抛出异常，说明还缺 headless 初始化或 Android/Window 替身:");
            PrintException(ex.InnerException ?? ex);
            return 1;
        }

        Console.WriteLine("服务器已启动。按 Ctrl+C 停止。");
        using var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };
        while (!quit.Wait(TimeSpan.FromMilliseconds(options.TickMilliseconds)))
        {
            ServerJoinLogger.BeforeNetworkUpdate(instance);
            bootstrap.Tick();
            ServerJoinLogger.BeforeNetworkUpdate(instance);
            ServerPacketLogger.Tick(instance);
            TerrainRuntimeLogger.Tick(instance);
            ServerJoinLogger.Tick(instance);
        }
        return 0;
    }

    private static int RunSelfTest(SurvivalcraftLoadContext context, Assembly survivalcraft, ServerOptions options)
    {
        Console.WriteLine($"启动 selftest，端口: {options.Port}");
        var bootstrap = new HeadlessBootstrap(context, survivalcraft, options);
        bootstrap.Initialize();

        var commonLibType = RequireType(survivalcraft, "Game.NetWork.CommonLib");
        var server = commonLibType.GetField("Net", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            ?? throw new InvalidOperationException("Game.NetWork.CommonLib.Net 为空。");
        var netNodeType = server.GetType();
        var startServer = netNodeType.GetMethod("StartServer", BindingFlags.Public | BindingFlags.Instance, [typeof(int)])
            ?? throw new MissingMethodException("Game.NetWork.NetNode", "StartServer(int)");
        var startResult = (bool)startServer.Invoke(server, [options.Port])!;
        Console.WriteLine($"selftest StartServer 返回: {startResult}");
        if (!startResult)
        {
            return 1;
        }
        ServerProjectSyncHook.EnsureInstalled(survivalcraft, server);
        ServerPacketLogger.Install(survivalcraft, server);
        TerrainRuntimeLogger.Install();
        ServerJoinLogger.Install(server);

        var client = Activator.CreateInstance(netNodeType)
            ?? throw new InvalidOperationException("无法创建自测客户端 NetNode。");
        var connectServer = netNodeType.GetMethod("ConnectServer", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("Game.NetWork.NetNode", "ConnectServer");
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, options.Port);
        connectServer.Invoke(client, [endpoint, Guid.NewGuid(), Guid.Empty, string.Empty, string.Empty]);

        var update = netNodeType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("Game.NetWork.NetNode", "Update");
        var clientCountProperty = netNodeType.GetProperty("ClientCount", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("Game.NetWork.NetNode", "ClientCount");
        var clientStageProperty = netNodeType.GetProperty("CurrentStage", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("Game.NetWork.NetNode", "CurrentStage");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        Exception? lastTickError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                ServerJoinLogger.BeforeNetworkUpdate(server);
                bootstrap.Tick();
                ServerJoinLogger.BeforeNetworkUpdate(server);
                update.Invoke(server, []);
                update.Invoke(client, []);
                ServerPacketLogger.Tick(server);
                TerrainRuntimeLogger.Tick(server);
                ServerJoinLogger.Tick(server);
            }
            catch (TargetInvocationException ex)
            {
                lastTickError = ex.InnerException ?? ex;
                Console.Error.WriteLine($"selftest tick 异常: {lastTickError.GetType().FullName}: {lastTickError.Message}");
            }

            var clientCount = Convert.ToInt32(clientCountProperty.GetValue(server));
            var clientStage = clientStageProperty.GetValue(client)?.ToString();
            if (clientCount >= 2)
            {
                Console.WriteLine($"selftest 通过: 服务端 ClientCount={clientCount}, 客户端状态={clientStage}");
                return 0;
            }
            Thread.Sleep(options.TickMilliseconds);
        }

        Console.Error.WriteLine($"selftest 失败: 15秒内客户端未加入。服务端 ClientCount={clientCountProperty.GetValue(server)}, 客户端状态={clientStageProperty.GetValue(client)}");
        if (lastTickError is not null)
        {
            PrintException(lastTickError);
        }
        return 1;
    }

    private static void PrintAssemblyProbe(Assembly survivalcraft, string dllDir)
    {
        Console.WriteLine($"DLL目录: {dllDir}");
        Console.WriteLine($"Survivalcraft: {survivalcraft.FullName}");

        string[] requiredTypes =
        [
            "Game.NetWork.NetNode",
            "Game.NetWork.CommonLib",
            "Game.NetWork.ProjectNet",
            "Game.NetWork.PackageManager",
            "Game.GameManager",
            "Game.DatabaseManager",
            "Game.ContentManager",
            "Game.WorldsManager",
            "Game.SettingsManager"
        ];

        foreach (var typeName in requiredTypes)
        {
            var type = survivalcraft.GetType(typeName, throwOnError: false);
            Console.WriteLine($"{(type is null ? "MISS" : " OK ")} {typeName}");
        }
    }

    private static Type RequireType(Assembly assembly, string fullName)
    {
        return assembly.GetType(fullName, throwOnError: true)
            ?? throw new TypeLoadException($"类型不存在: {fullName}");
    }

    private static void PrintException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            Console.Error.WriteLine($"{current.GetType().FullName}: {current.Message}");
            Console.Error.WriteLine(current.StackTrace);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法:
              dotnet run --project SurvivalcraftServer
              dotnet run --project SurvivalcraftServer -- --storage PATH [--debug]
              dotnet run --project SurvivalcraftServer -- probe [--storage PATH] [--debug]
              dotnet run --project SurvivalcraftServer -- selftest [--storage PATH] [--debug]

            说明:
              无参数    读取/创建 ./config.toml，然后启动服务端。
              --storage 配置与默认存档根目录，默认当前目录。
              --debug   显示自定义 hook 调试日志。
              probe     只加载DLL并检查服务端相关类型。
              start     读取 config.toml 初始化资源/世界，并调用 Game.NetWork.CommonLib.Net.StartServer(port)。
              selftest  启动服务端后，用同一DLL创建本地客户端做真实握手验证。
              端口、世界、资源、lib 和存档路径都在 config.toml 中配置。
            """);
    }
}
