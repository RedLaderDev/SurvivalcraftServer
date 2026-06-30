namespace SurvivalcraftServer.Server;

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
    string ScKeyToken);
