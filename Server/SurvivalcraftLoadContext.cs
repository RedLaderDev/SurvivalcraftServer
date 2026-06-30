using System.Reflection;
using System.Runtime.Loader;

namespace SurvivalcraftServer;

internal sealed class SurvivalcraftLoadContext : AssemblyLoadContext
{
    private readonly string _dllDir;
    private readonly Dictionary<string, string> _assemblies;

    public SurvivalcraftLoadContext(string dllDir)
        : base(isCollectible: true)
    {
        _dllDir = dllDir;
        _assemblies = Directory.EnumerateFiles(dllDir, "*.dll")
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        if (assemblyName.Name.StartsWith("System.", StringComparison.Ordinal)
            || assemblyName.Name is "System" or "mscorlib" or "netstandard")
        {
            return null;
        }

        if (_assemblies.TryGetValue(assemblyName.Name, out var path))
        {
            return LoadFromAssemblyPath(path);
        }

        var candidate = Path.Combine(_dllDir, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
