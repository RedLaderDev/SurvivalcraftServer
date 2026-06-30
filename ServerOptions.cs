namespace SurvivalcraftServer;

internal sealed record ServerOptions(
    string Command,
    string StorageDir,
    string LibDir,
    int Port,
    bool ShowHelp,
    bool Debug,
    string AssetsDir,
    string DataDir,
    string WorldName,
    string WorldDir,
    string FurniturePacksDir,
    string CharacterSkinsDir,
    string TexturePacksDir,
    string Seed,
    int MaxPlayers,
    int TickMilliseconds,
    bool CheckLogin,
    string ScKeyServerId,
    string ScKeyServerName,
    string ScKeyToken)
{
    public static ServerOptions Parse(string[] args)
    {
        if (args.Any(arg => arg is "-h" or "--help" or "help"))
        {
            return Defaults("start", Path.GetFullPath(".")) with { ShowHelp = true };
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

        var defaults = Defaults(command, storageDir);
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

    private static ServerOptions Defaults(string command, string storageDir)
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
}
