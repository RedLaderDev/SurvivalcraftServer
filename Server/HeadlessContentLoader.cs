using System.Reflection;
using System.Runtime.Serialization;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private object ManualLoadContentScpak(string assetsRoot)
    {
        var contentPath = Path.Combine(assetsRoot, "Content.scpak");
        if (!File.Exists(contentPath))
        {
            throw new FileNotFoundException("找不到 Content.scpak", contentPath);
        }

        var bytes = File.ReadAllBytes(contentPath);
        var marker = "再乱改就跑路，谁也别想玩！"u8.ToArray();
        if (bytes.AsSpan().StartsWith(marker))
        {
            var source = bytes.AsSpan(marker.Length);
            var decoded = new byte[source.Length];
            var evenCount = (decoded.Length + 1) / 2;
            var even = 0;
            var odd = 0;
            for (var i = 0; i < decoded.Length; i++)
            {
                decoded[i] = i % 2 == 0 ? source[even++] : source[evenCount + odd++];
            }
            bytes = decoded;
        }

        var zipArchiveType = _game.GetType("Game.ZipArchive", throwOnError: true)!;
        var memory = new MemoryStream(bytes);
        var archive = zipArchiveType.GetMethod("Open", BindingFlags.Public | BindingFlags.Static, [typeof(Stream), typeof(bool)])!
            .Invoke(null, [memory, true])!;
        var entries = (System.Collections.IEnumerable)zipArchiveType.GetMethod("ReadCentralDir")!.Invoke(archive, [])!;
        var contentInfoType = _game.GetType("Game.ContentInfo", throwOnError: true)!;
        var contentManagerType = _game.GetType("Game.ContentManager", throwOnError: true)!;
        var add = contentManagerType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static)!;
        var extract = zipArchiveType.GetMethod("ExtractFile", BindingFlags.Public | BindingFlags.Instance)!;

        RegisterContentReaders();
        foreach (var entry in entries)
        {
            var entryType = entry.GetType();
            var filename = (string)entryType.GetField("FilenameInZip")!.GetValue(entry)!;
            var fileSize = Convert.ToUInt32(entryType.GetField("FileSize")!.GetValue(entry)!);
            if (fileSize == 0 || !filename.StartsWith("Assets/", StringComparison.Ordinal))
            {
                continue;
            }
            using var extracted = new MemoryStream();
            extract.Invoke(archive, [entry, extracted]);
            extracted.Position = 0;
            var contentInfo = Activator.CreateInstance(contentInfoType, [filename[7..]])!;
            contentInfoType.GetMethod("SetContentStream")!.Invoke(contentInfo, [new MemoryStream(extracted.ToArray())]);
            add.Invoke(null, [contentInfo]);
        }
        return archive;
    }

    private void RegisterContentReaders()
    {
        var contentManagerType = _game.GetType("Game.ContentManager", throwOnError: true)!;
        var readerList = (System.Collections.IDictionary)contentManagerType
            .GetField("ReaderList", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        string[] readerTypes =
        [
            "Game.IContentReader.BitmapFontReader",
            "Game.IContentReader.DaeModelReader",
            "Game.IContentReader.ImageReader",
            "Game.IContentReader.JsonArrayReader",
            "Game.IContentReader.JsonObjectReader",
            "Game.IContentReader.JsonModelReader",
            "Game.IContentReader.MtllibStructReader",
            "Game.IContentReader.ObjModelReader",
            "Game.IContentReader.ShaderReader",
            "Game.IContentReader.SoundBufferReader",
            "Game.IContentReader.StreamingSourceReader",
            "Game.IContentReader.StringReader",
            "Game.IContentReader.SubtextureReader",
            "Game.IContentReader.Texture2DReader",
            "Game.IContentReader.XmlReader"
        ];
        foreach (var typeName in readerTypes)
        {
            var type = _game.GetType(typeName, throwOnError: true)!;
            var reader = Activator.CreateInstance(type)!;
            var key = type.GetProperty("Type")!.GetValue(reader)!;
            readerList[key] = reader;
        }
    }

    private void InitializeGameData()
    {
        ConfigureStorageDirectories();
        InvokeStatic(_game, "Game.DatabaseManager", "Initialize");
        var contentManager = _game.GetType("Game.ContentManager", throwOnError: true)!;
        var get = contentManager.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, [typeof(Type), typeof(string), typeof(string)])!;
        var xElementType = typeof(System.Xml.Linq.XElement);
        var databaseNode = get.Invoke(null, [xElementType, "Database", null]);
        var databaseManager = _game.GetType("Game.DatabaseManager", throwOnError: true)!;
        databaseManager.GetField("DatabaseNode", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, databaseNode);
        InvokeStatic(_game, "Game.DatabaseManager", "LoadDataBaseFromXml", [databaseNode!]);
        InvokeStatic(_game, "Game.BlocksManager", "Initialize");
        InvokeStatic(_game, "Game.CraftingRecipesManager", "Initialize");
        InvokeOptionalStatic("Game.BlocksTexturesManager", "Initialize");
        InvokeOptionalStatic("Game.CharacterSkinsManager", "Initialize");
        InvokeOptionalStatic("Game.CommunityContentManager", "Initialize");
        InvokeOptionalStatic("Game.ExternalContentManager", "Initialize");
        InvokeOptionalStatic("Game.FurniturePacksManager", "Initialize");
        InvokeOptionalStatic("Game.LightingManager", "Initialize");
        InvokeOptionalStatic("Game.MotdManager", "Initialize");
        InvokeStatic(_game, "Game.VersionsManager", "Initialize");
        InvokeStatic(_game, "Game.WorldsManager", "Initialize");
    }

    private void ConfigureStorageDirectories()
    {
        var modsManager = _game.GetType("Game.ModsManager", throwOnError: true)!;
        var dataRoot = Path.GetFullPath(_options.DataDir);
        SetStaticField(modsManager.Assembly, "Game.ModsManager", "FurniturePacksDirectoryName", HeadlessStoragePaths.ToConfigPath(dataRoot, _options.FurniturePacksDir));
        SetStaticField(modsManager.Assembly, "Game.ModsManager", "CharacterSkinsDirectoryName", HeadlessStoragePaths.ToConfigPath(dataRoot, _options.CharacterSkinsDir));
        SetStaticField(modsManager.Assembly, "Game.ModsManager", "BlockTexturesDirectoryName", HeadlessStoragePaths.ToConfigPath(dataRoot, _options.TexturePacksDir));
    }

    private void InstallHeadlessContentCaches()
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        var shaderType = _engine.GetType("Engine.Graphics.Shader", throwOnError: true)!;
        AddContentCache("Shaders/AlphaTested", FormatterServices.GetUninitializedObject(shaderType));
    }

    private void AddContentCache(string key, object value)
    {
        var contentManagerType = _game.GetType("Game.ContentManager", throwOnError: true)!;
        var caches = (System.Collections.IDictionary)contentManagerType
            .GetField("Caches", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var listType = typeof(List<>).MakeGenericType(typeof(object));
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        list.Add(value);
        caches[key] = list;
    }
}
