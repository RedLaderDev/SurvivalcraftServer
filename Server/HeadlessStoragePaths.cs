namespace SurvivalcraftServer.Server;

internal static class HeadlessStoragePaths
{
    public static string ResolveDataPath(string dataRoot, string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(dataRoot, path);
    }

    public static string ToConfigPath(string dataRoot, string path)
    {
        var fullPath = Path.GetFullPath(ResolveDataPath(dataRoot, path));
        var relativePath = Path.GetRelativePath(Path.GetFullPath(dataRoot), fullPath);
        return "config:/" + relativePath.Trim().TrimStart('.', '/', '\\').Replace('\\', '/');
    }
}
