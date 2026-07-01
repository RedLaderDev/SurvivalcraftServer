using System.Reflection;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private void InitializeHeadlessLanguage()
    {
        var contentManager = _game.GetType("Game.ContentManager", throwOnError: true)!;
        var languageControl = _game.GetType("Game.LanguageControl", throwOnError: true)!;
        var modsManager = _game.GetType("Game.ModsManager", throwOnError: true)!;

        var languageTypes = (System.Collections.IList)languageControl
            .GetField("LanguageTypes", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var languageNames = (System.Collections.IDictionary)languageControl
            .GetField("LanguageNames", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        languageTypes.Clear();
        languageNames.Clear();

        var languageContent = EnumerateLanguageContent(contentManager).ToArray();
        foreach (var contentInfo in languageContent)
        {
            var language = Path.GetFileNameWithoutExtension(GetContentFilename(contentInfo));
            if (string.IsNullOrWhiteSpace(language) || languageTypes.Contains(language))
            {
                continue;
            }

            languageTypes.Add(language);
            languageNames[language] = language;
        }

        var configuredLanguage = TryGetConfiguredLanguage(modsManager);
        var selectedLanguage = !string.IsNullOrWhiteSpace(configuredLanguage) && languageTypes.Contains(configuredLanguage)
            ? configuredLanguage
            : languageTypes.Contains("zh-CN")
                ? "zh-CN"
                : languageTypes.Count > 0
                    ? (string)languageTypes[0]!
                    : "zh-CN";

        languageControl.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!
            .Invoke(null, [selectedLanguage]);

        var loadJson = languageControl.GetMethod("loadJson", BindingFlags.Public | BindingFlags.Static, [typeof(Stream), typeof(string)])!;
        var selectedFile = selectedLanguage + ".json";
        foreach (var contentInfo in languageContent)
        {
            if (!string.Equals(GetContentFilename(contentInfo), selectedFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = (Stream)contentInfo.GetType().GetMethod("Duplicate", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(contentInfo, [])!;
            loadJson.Invoke(null, [stream, selectedLanguage]);
            break;
        }

        ServerDiagnostics.Debug($"[LANG] 已初始化语言: {selectedLanguage}, 可用语言数: {languageTypes.Count}, SuicideCause={GetLanguageText(languageControl, "ComponentHealthPackage", "SuicideCause")}");
    }

    private static IEnumerable<object> EnumerateLanguageContent(Type contentManager)
    {
        var list = (System.Collections.IEnumerable)contentManager
            .GetMethod("List", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!
            .Invoke(null, ["Lang"])!;
        foreach (var item in list)
        {
            yield return item;
        }
    }

    private static string GetContentFilename(object contentInfo)
    {
        return (string)contentInfo.GetType()
            .GetField("Filename", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(contentInfo)!;
    }

    private static string? TryGetConfiguredLanguage(Type modsManager)
    {
        var configs = (System.Collections.IDictionary?)modsManager
            .GetField("Configs", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        return configs is not null && configs.Contains("Language") ? configs["Language"]?.ToString() : null;
    }

    private static string GetLanguageText(Type languageControl, params string[] keys)
    {
        var get = languageControl.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "Get"
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(string[]);
            });
        return (string)get.Invoke(null, [keys])!;
    }
}
