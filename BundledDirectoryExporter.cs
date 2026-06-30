namespace SurvivalcraftServer;

internal static class BundledDirectoryExporter
{
    public static void ExportIfMissing(string directoryName, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            return;
        }

        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, directoryName);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"内置{directoryName}目录不存在: {sourceDirectory}");
        }

        CopyDirectory(sourceDirectory, targetDirectory);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: false);
        }
    }
}
