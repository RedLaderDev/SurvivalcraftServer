using System.Globalization;

namespace SurvivalcraftServer;

internal sealed record ServerConfig(
    int Port,
    string WorldName,
    string DataDir,
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
    string ScKeyToken,
    string AssetsDir,
    string LibDir);

internal static class ServerConfigFile
{
    public const string FileName = "config.toml";

    public static ServerConfig LoadOrCreate(string storageDir, ServerConfig defaults)
    {
        Directory.CreateDirectory(storageDir);
        var path = Path.Combine(storageDir, FileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Render(defaults));
            return defaults;
        }

        var values = Parse(File.ReadAllLines(path));
        return defaults with
        {
            Port = GetInt(values, "port", defaults.Port),
            WorldName = GetString(values, "world", defaults.WorldName),
            DataDir = ResolveStoragePath(storageDir, GetString(values, "data_dir", defaults.DataDir)),
            WorldDir = GetString(values, "world_dir", defaults.WorldDir),
            FurniturePacksDir = GetString(values, "furniture_packs_dir", defaults.FurniturePacksDir),
            CharacterSkinsDir = GetString(values, "character_skins_dir", defaults.CharacterSkinsDir),
            TexturePacksDir = GetString(values, "texture_packs_dir", defaults.TexturePacksDir),
            Seed = NormalizeSeed(GetString(values, "seed", defaults.Seed)),
            MaxPlayers = GetInt(values, "max_players", defaults.MaxPlayers),
            TickMilliseconds = GetInt(values, "tick_ms", defaults.TickMilliseconds),
            CheckLogin = GetBool(values, "check_login", defaults.CheckLogin),
            ScKeyServerId = GetString(values, "sc_key_server_id", defaults.ScKeyServerId),
            ScKeyServerName = GetString(values, "sc_key_server_name", defaults.ScKeyServerName),
            ScKeyToken = GetString(values, "sc_key_token", defaults.ScKeyToken),
            AssetsDir = ResolveStoragePath(storageDir, GetString(values, "assets_dir", defaults.AssetsDir)),
            LibDir = ResolveStoragePath(storageDir, GetString(values, "lib_dir", GetString(values, "dll_dir", defaults.LibDir)))
        };
    }

    public static string ResolveStoragePath(string storageDir, string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(storageDir, path);
    }

    private static Dictionary<string, string> Parse(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0 || line.StartsWith('['))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            result[key] = Unquote(value);
        }
        return result;
    }

    private static string Render(ServerConfig config)
    {
        return string.Join(Environment.NewLine,
        [
            "# Survivalcraft headless server 配置文件。",
            "# 服务端 UDP 端口，客户端连接时填写这个端口。",
            $"port = {config.Port.ToString(CultureInfo.InvariantCulture)}",
            "",
            "# 世界显示名称，只在创建新世界时写入存档元数据。",
            $"world = {Quote(config.WorldName)}",
            "",
            "# 运行数据根目录。相对路径基于 config.toml 所在目录解析。",
            $"data_dir = {Quote(config.DataDir)}",
            "# 唯一世界存档目录。相对路径基于 data_dir 解析，不会再创建 Worlds/ 多世界目录。",
            $"world_dir = {Quote(config.WorldDir)}",
            "# 家具包目录。相对路径基于 data_dir 解析。",
            $"furniture_packs_dir = {Quote(config.FurniturePacksDir)}",
            "# 角色皮肤目录。相对路径基于 data_dir 解析。",
            $"character_skins_dir = {Quote(config.CharacterSkinsDir)}",
            "# 方块纹理包目录。相对路径基于 data_dir 解析。",
            $"texture_packs_dir = {Quote(config.TexturePacksDir)}",
            "",
            "# 世界种子。留空表示创建新世界时随机生成；已有世界始终使用存档内保存的种子。",
            $"seed = {Quote(config.Seed)}",
            "# 最大在线玩家数，只在创建新世界时写入世界设置。",
            $"max_players = {config.MaxPlayers.ToString(CultureInfo.InvariantCulture)}",
            "# 服务端主循环每次 tick 后的休眠毫秒数。",
            $"tick_ms = {config.TickMilliseconds.ToString(CultureInfo.InvariantCulture)}",
            "",
            "# 是否要求客户端使用 SCKey 登录验证。开启前必须同时填写 sc_key_server_id 和 sc_key_token。",
            $"check_login = {FormatBool(config.CheckLogin)}",
            "# SCKey 服务端绑定 ID。check_login = true 时必填。",
            $"sc_key_server_id = {Quote(config.ScKeyServerId)}",
            "# SCKey 服务端显示名，仅用于写入游戏兼容配置。",
            $"sc_key_server_name = {Quote(config.ScKeyServerName)}",
            "# SCKey 服务端令牌。check_login = true 时必填；会写入 Login/login_config.json 供游戏验证接口使用。",
            $"sc_key_token = {Quote(config.ScKeyToken)}",
            "",
            "# 游戏资源目录，里面应包含 Content.scpak 和 shader 等资源文件。相对路径基于 config.toml 所在目录解析。",
            $"assets_dir = {Quote(config.AssetsDir)}",
            "# 游戏 DLL 目录，里面应包含 Survivalcraft.dll、Engine.dll 和运行所需依赖。相对路径基于 config.toml 所在目录解析。",
            $"lib_dir = {Quote(config.LibDir)}",
            ""
        ]);
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '#')
            {
                return line[..i];
            }
        }
        return line;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return value;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback)
    {
        return values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string NormalizeSeed(string seed)
    {
        return string.Equals(seed, "headless", StringComparison.OrdinalIgnoreCase) ? string.Empty : seed;
    }
}
