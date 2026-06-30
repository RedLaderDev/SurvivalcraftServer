using System.Text.Json;

namespace SurvivalcraftServer.Server;

internal static class ScKeyServerSettingsWriter
{
    public static void Write(ServerOptions options)
    {
        Directory.CreateDirectory(options.StorageDir);
        Directory.CreateDirectory(options.DataDir);

        var serverSettingPath = Path.Combine(options.StorageDir, "ServerSetting.json");
        var serverSetting = new
        {
            options.ScKeyServerId,
            options.ScKeyServerName,
            CheckLogin = options.CheckLogin
        };
        File.WriteAllText(serverSettingPath, JsonSerializer.Serialize(serverSetting, JsonOptions()));

        var loginConfig = new
        {
            ScKeyToken = options.ScKeyToken,
            LoginContact = string.Empty,
            CurrentRoleId = string.Empty,
            CurrentRoleName = string.Empty
        };
        WriteLoginConfig(Path.Combine(options.DataDir, "Login"), loginConfig);
        WriteLoginConfig(Path.Combine(options.StorageDir, "Login"), loginConfig);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions { WriteIndented = true };
    }

    private static void WriteLoginConfig(string loginDir, object loginConfig)
    {
        Directory.CreateDirectory(loginDir);
        File.WriteAllText(Path.Combine(loginDir, "login_config.json"), JsonSerializer.Serialize(loginConfig, JsonOptions()));
    }
}
