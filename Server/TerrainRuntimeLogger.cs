using System.Collections;
using System.Reflection;

namespace SurvivalcraftServer.Server;

internal static class TerrainRuntimeLogger
{
    private const float HeadlessVisibilityDistance = 64f;
    private const float HeadlessContentDistance = 96f;

    private static readonly Dictionary<byte, DateTime> LastLogs = [];
    private static readonly HashSet<string> RangeExpansionLogs = [];

    public static void Install()
    {
        LastLogs.Clear();
        RangeExpansionLogs.Clear();
        ServerDiagnostics.Debug($"[TERRAIN] 地形同步日志 hook 已启用。");
    }

    public static void Tick(object netNode)
    {
        try
        {
            var assembly = netNode.GetType().Assembly;
            var projectNetType = assembly.GetType("Game.NetWork.ProjectNet", throwOnError: false);
            var subsystemTerrain = projectNetType?.GetField("subsystemTerrain", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var subsystemPlayers = projectNetType?.GetField("subsystemPlayers", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var terrainUpdater = GetProperty(subsystemTerrain, "TerrainUpdater");
            var terrain = GetProperty(subsystemTerrain, "Terrain");
            if (terrainUpdater is null || terrain is null)
            {
                return;
            }

            var waitChunkList = GetField(terrainUpdater, "waitChunkList") as IDictionary;
            if (waitChunkList is null || waitChunkList.Count == 0)
            {
                return;
            }

            foreach (DictionaryEntry entry in waitChunkList)
            {
                if (entry.Key is null || entry.Value is not IEnumerable requestedChunks)
                {
                    continue;
                }

                var client = entry.Key;
                var clientId = Convert.ToByte(GetProperty(client, "ID") ?? (byte)0);
                var player = FindPlayerData(subsystemPlayers, client);
                ExpandHeadlessTerrainRange(terrainUpdater, player, clientId);
                LogRequestedChunkStates(terrain, clientId, requestedChunks);
            }
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Debug($"[TERRAIN] 地形同步日志失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ExpandHeadlessTerrainRange(object terrainUpdater, object? player, byte clientId)
    {
        if (player is null)
        {
            return;
        }

        var playerIndex = Convert.ToInt32(GetProperty(player, "PlayerIndex"));
        var updateLocations = GetProperty(terrainUpdater, "UpdateLocations") as IDictionary;
        if (updateLocations is null || !updateLocations.Contains(playerIndex))
        {
            return;
        }

        var location = updateLocations[playerIndex];
        var center = GetField(location, "Center");
        if (center is null)
        {
            return;
        }

        var visibility = Convert.ToSingle(GetField(location, "VisibilityDistance") ?? 0f);
        var content = Convert.ToSingle(GetField(location, "ContentDistance") ?? 0f);
        if (visibility >= HeadlessVisibilityDistance && content >= HeadlessContentDistance)
        {
            return;
        }

        var method = terrainUpdater.GetType().GetMethod("SetUpdateLocation", BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(terrainUpdater, [playerIndex, center, HeadlessVisibilityDistance, HeadlessContentDistance]);

        var key = $"{clientId}:{playerIndex}";
        if (RangeExpansionLogs.Add(key))
        {
            ServerDiagnostics.Debug(
                $"[TERRAIN] 已扩大服务端地形范围: ClientId={clientId}, PlayerIndex={playerIndex}, " +
                $"Visibility {visibility:0.#}->{HeadlessVisibilityDistance:0.#}, Content {content:0.#}->{HeadlessContentDistance:0.#}");
        }
    }

    private static void LogRequestedChunkStates(object terrain, byte clientId, IEnumerable requestedChunks)
    {
        var now = DateTime.UtcNow;
        if (LastLogs.TryGetValue(clientId, out var previous) && (now - previous).TotalSeconds < 1.0)
        {
            return;
        }
        LastLogs[clientId] = now;

        var getChunkAtCoords = terrain.GetType().GetMethod("GetChunkAtCoords", BindingFlags.Public | BindingFlags.Instance);
        var total = 0;
        var missing = 0;
        var loaded = 0;
        var readyToSend = 0;
        var stateCounts = new Dictionary<string, int>();
        var samples = new List<string>();

        foreach (var point in requestedChunks)
        {
            total++;
            var x = Convert.ToInt32(GetField(point, "X") ?? 0);
            var y = Convert.ToInt32(GetField(point, "Y") ?? 0);
            var chunk = getChunkAtCoords?.Invoke(terrain, [x, y]);
            if (chunk is null)
            {
                missing++;
                AddSample(samples, $"{x},{y}:missing");
                continue;
            }

            var state = GetProperty(chunk, "State")?.ToString() ?? "?";
            var threadState = GetProperty(chunk, "ThreadState")?.ToString() ?? "?";
            var isLoaded = GetField(chunk, "IsLoaded") as bool? == true;
            if (isLoaded)
            {
                loaded++;
            }
            if (IsReadyToSend(threadState))
            {
                readyToSend++;
            }

            var key = $"{state}/{threadState}";
            stateCounts[key] = stateCounts.GetValueOrDefault(key) + 1;
            AddSample(samples, $"{x},{y}:{key},Loaded={isLoaded}");
        }

        var statesText = string.Join(", ", stateCounts.Select(pair => $"{pair.Key}={pair.Value}"));
        var sampleText = string.Join(" | ", samples);
        ServerDiagnostics.Debug(
            $"[TERRAIN] 请求块状态: ClientId={clientId}, Total={total}, Missing={missing}, " +
            $"Loaded={loaded}, ReadyToSend={readyToSend}, States=[{statesText}], Samples=[{sampleText}]");
    }

    private static bool IsReadyToSend(string threadState)
    {
        return threadState is "InvalidLight" or "InvalidPropagatedLight" or "InvalidVertices1" or "InvalidVertices2" or "Valid";
    }

    private static void AddSample(List<string> samples, string value)
    {
        if (samples.Count < 8)
        {
            samples.Add(value);
        }
    }

    private static object? FindPlayerData(object? subsystemPlayers, object client)
    {
        if (subsystemPlayers?.GetType().GetProperty("PlayersData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(subsystemPlayers) is not IEnumerable players)
        {
            return null;
        }

        foreach (var player in players)
        {
            if (ReferenceEquals(GetProperty(player, "Client"), client))
            {
                return player;
            }
        }
        return null;
    }

    private static object? GetField(object? instance, string name)
    {
        return instance?.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
    }

    private static object? GetProperty(object? instance, string name)
    {
        return instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
    }
}
