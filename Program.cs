using SurvivalcraftServer.Server;

namespace SurvivalcraftServer;

internal static class Program
{
    private static int Main(string[] args)
    {
        ServerOptions options;
        try
        {
            options = ParseOptions(args);
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

        return ServerLauncher.Run(options);
    }

    private static ServerOptions ParseOptions(string[] args)
    {
        if (args.Any(arg => arg is "-h" or "--help" or "help"))
        {
            return CreateDefaultOptions("start", Path.GetFullPath(".")) with { ShowHelp = true };
        }

        var command = "start";
        var startIndex = 0;
        if (args.Length > 0 && args[0] is "probe" or "start" or "selftest")
        {
            command = args[0];
            startIndex = 1;
        }
        else if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"未知命令: {args[0]}");
        }

        var storageDir = Path.GetFullPath(".");
        var debug = false;
        for (var i = startIndex; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--storage":
                    storageDir = Path.GetFullPath(RequireValue(args, ref i, "--storage"));
                    break;
                case "--debug":
                    debug = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}");
            }
        }

        var defaults = CreateDefaultOptions(command, storageDir);
        var config = ServerConfigFile.LoadOrCreate(storageDir, new ServerConfig(
            defaults.Port,
            defaults.WorldName,
            defaults.DataDir,
            defaults.WorldDir,
            defaults.FurniturePacksDir,
            defaults.CharacterSkinsDir,
            defaults.TexturePacksDir,
            defaults.Seed,
            defaults.MaxPlayers,
            defaults.TickMilliseconds,
            defaults.CheckLogin,
            defaults.ScKeyServerId,
            defaults.ScKeyServerName,
            defaults.ScKeyToken,
            defaults.AssetsDir,
            defaults.LibDir));

        var options = defaults with
        {
            Port = config.Port,
            WorldName = config.WorldName,
            DataDir = config.DataDir,
            WorldDir = config.WorldDir,
            FurniturePacksDir = config.FurniturePacksDir,
            CharacterSkinsDir = config.CharacterSkinsDir,
            TexturePacksDir = config.TexturePacksDir,
            Seed = config.Seed,
            MaxPlayers = config.MaxPlayers,
            TickMilliseconds = config.TickMilliseconds,
            CheckLogin = config.CheckLogin,
            ScKeyServerId = config.ScKeyServerId,
            ScKeyServerName = config.ScKeyServerName,
            ScKeyToken = config.ScKeyToken,
            AssetsDir = config.AssetsDir,
            LibDir = config.LibDir,
            Debug = debug
        };

        return options with
        {
            LibDir = Path.GetFullPath(ServerConfigFile.ResolveStoragePath(storageDir, options.LibDir)),
            AssetsDir = Path.GetFullPath(ServerConfigFile.ResolveStoragePath(storageDir, options.AssetsDir)),
            DataDir = Path.GetFullPath(ServerConfigFile.ResolveStoragePath(storageDir, options.DataDir))
        };
    }

    private static ServerOptions CreateDefaultOptions(string command, string storageDir)
    {
        return new ServerOptions(
            command,
            storageDir,
            "lib",
            25565,
            ShowHelp: false,
            Debug: false,
            "assets",
            ".",
            "World",
            "World",
            "FurniturePacks",
            "CharacterSkins",
            "TexturePacks",
            "",
            20,
            50,
            false,
            "",
            "",
            "");
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} 缺少参数值。");
        }
        index++;
        return args[index];
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
