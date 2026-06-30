using System.Collections;
using System.Reflection;

namespace SurvivalcraftServer.Server;

internal static class ServerJoinLogger
{
    private sealed record ClientSnapshot(byte Id, Guid PlayerGuid, string TokenId, string Nickname, string State, string Endpoint);

    private static readonly Dictionary<byte, string> KnownStates = [];
    private static readonly HashSet<string> PendingKeys = [];
    private static readonly HashSet<byte> KnownClients = [];
    private static readonly Dictionary<Guid, string> KnownPlayerRuntimeStates = [];
    private static readonly Dictionary<Guid, DateTime> LastTerrainWaitLogs = [];

    public static void Install(object netNode)
    {
        KnownStates.Clear();
        PendingKeys.Clear();
        KnownClients.Clear();
        KnownPlayerRuntimeStates.Clear();
        LastTerrainWaitLogs.Clear();
        ServerDiagnostics.Debug($"[JOIN] 加入日志 hook 已启用。");
        Tick(netNode);
    }

    public static void Tick(object netNode)
    {
        LogPendingClient(netNode);
        LogClients(netNode);
        LogPlayerRuntimeStates(netNode);
    }

    public static void BeforeNetworkUpdate(object netNode)
    {
        LogClientsToRemove(netNode);
    }

    private static void LogClientsToRemove(object netNode)
    {
        if (GetField(netNode, "ClientsToRemove") is not IDictionary clientsToRemove)
        {
            return;
        }

        foreach (DictionaryEntry entry in clientsToRemove)
        {
            if (entry.Key is null)
            {
                continue;
            }

            var snapshot = Snapshot(entry.Key);
            var reason = entry.Value?.ToString();
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "<无原因>";
            }
            ServerDiagnostics.Debug($"[JOIN] 准备移除: {Format(snapshot)}, Reason={reason}");
        }
    }

    private static void LogPlayerRuntimeStates(object netNode)
    {
        var projectNetType = netNode.GetType().Assembly.GetType("Game.NetWork.ProjectNet", throwOnError: false);
        var subsystemPlayers = projectNetType?.GetField("subsystemPlayers", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (subsystemPlayers?.GetType().GetProperty("PlayersData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(subsystemPlayers) is not IEnumerable players)
        {
            return;
        }

        foreach (var player in players)
        {
            var guid = GetProperty(player, "PlayerGUID") is Guid playerGuid ? playerGuid : Guid.Empty;
            if (guid == Guid.Empty)
            {
                continue;
            }

            var stateMachine = GetField(player, "m_stateMachine");
            var state = stateMachine is null ? "<unknown>" : GetProperty(stateMachine, "CurrentState")?.ToString() ?? "<unknown>";
            var hasComponent = GetProperty(player, "ComponentPlayer") is not null;
            var hasClient = GetProperty(player, "Client") is not null;
            var clientId = GetProperty(player, "ClientId")?.ToString() ?? "<none>";
            var summary = $"ClientId={clientId}, PlayerState={state}, HasClient={hasClient}, HasComponentPlayer={hasComponent}";
            if (!KnownPlayerRuntimeStates.TryGetValue(guid, out var previous) || previous != summary)
            {
                KnownPlayerRuntimeStates[guid] = summary;
                ServerDiagnostics.Debug($"[JOIN] 玩家运行状态: PlayerGuid={guid}, {summary}");
            }
            if (state == "WaitForTerrain")
            {
                LogTerrainWaitProgress(netNode, player, guid, clientId);
            }
        }
    }

    private static void LogTerrainWaitProgress(object netNode, object player, Guid guid, string clientId)
    {
        var now = DateTime.UtcNow;
        if (LastTerrainWaitLogs.TryGetValue(guid, out var previous) && (now - previous).TotalSeconds < 1.0)
        {
            return;
        }
        LastTerrainWaitLogs[guid] = now;

        try
        {
            var projectNetType = netNode.GetType().Assembly.GetType("Game.NetWork.ProjectNet", throwOnError: false);
            var subsystemTerrain = projectNetType?.GetField("subsystemTerrain", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var terrainUpdater = GetProperty(subsystemTerrain, "TerrainUpdater");
            var terrain = GetProperty(subsystemTerrain, "Terrain");
            var playerIndex = Convert.ToInt32(GetProperty(player, "PlayerIndex"));
            var getUpdateProgress = terrainUpdater?.GetType().GetMethod("GetUpdateProgress", BindingFlags.Public | BindingFlags.Instance);
            float? progress = getUpdateProgress is null ? null : Convert.ToSingle(getUpdateProgress.Invoke(terrainUpdater, [playerIndex, 64f, 0f]));
            var allocatedChunks = GetProperty(terrain, "AllocatedChunks") as ICollection;
            var waitChunkList = GetField(terrainUpdater, "waitChunkList") as IDictionary;
            var updateLocations = GetProperty(terrainUpdater, "UpdateLocations") as IDictionary;
            var location = updateLocations?.Contains(playerIndex) == true ? updateLocations[playerIndex] : null;
            var locationText = location is null ? "<none>" : FormatUpdateLocation(location);

            ServerDiagnostics.Debug(
                $"[JOIN] 等待地形: PlayerGuid={guid}, ClientId={clientId}, PlayerIndex={playerIndex}, " +
                $"Progress={progress?.ToString("0.000") ?? "?"}, AllocatedChunks={allocatedChunks?.Count.ToString() ?? "?"}, " +
                $"WaitingClients={waitChunkList?.Count.ToString() ?? "?"}, Location={locationText}");
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Debug($"[JOIN] 等待地形日志失败: PlayerGuid={guid}, {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatUpdateLocation(object location)
    {
        var center = GetField(location, "Center")?.ToString() ?? "?";
        var visibilityDistance = GetField(location, "VisibilityDistance")?.ToString() ?? "?";
        var contentDistance = GetField(location, "ContentDistance")?.ToString() ?? "?";
        var lastCenter = GetField(location, "LastChunksUpdateCenter")?.ToString() ?? "?";
        return $"Center={center}, Visibility={visibilityDistance}, Content={contentDistance}, LastCenter={lastCenter}";
    }

    private static void LogPendingClient(object netNode)
    {
        var pendingPeer = GetField(netNode, "pendingPeer");
        if (pendingPeer is null)
        {
            return;
        }

        var client = GetProperty(pendingPeer, "Tag");
        if (client is null)
        {
            return;
        }

        var snapshot = Snapshot(client);
        var key = $"{snapshot.Endpoint}|{snapshot.PlayerGuid}|{snapshot.TokenId}";
        if (PendingKeys.Add(key))
        {
            ServerDiagnostics.Debug($"[JOIN] 正在加入: {Format(snapshot)}");
        }
    }

    private static void LogClients(object netNode)
    {
        if (GetField(netNode, "Clients") is not IDictionary clients)
        {
            return;
        }

        var currentIds = new HashSet<byte>();
        foreach (DictionaryEntry entry in clients)
        {
            if (entry.Value is null)
            {
                continue;
            }

            var snapshot = Snapshot(entry.Value);
            currentIds.Add(snapshot.Id);
            if (KnownClients.Add(snapshot.Id) && snapshot.Id != 0)
            {
                ServerDiagnostics.Debug($"[JOIN] 正在加入: {Format(snapshot)}");
                LogStateMilestone(snapshot, previousState: null);
            }

            if (!KnownStates.TryGetValue(snapshot.Id, out var previousState))
            {
                KnownStates[snapshot.Id] = snapshot.State;
                continue;
            }

            if (previousState != snapshot.State)
            {
                KnownStates[snapshot.Id] = snapshot.State;
                ServerDiagnostics.Debug($"[JOIN] 状态变化: {Format(snapshot)} {previousState} -> {snapshot.State}");
                LogStateMilestone(snapshot, previousState);
            }
        }

        foreach (var id in KnownClients.ToArray())
        {
            if (id == 0 || currentIds.Contains(id))
            {
                continue;
            }
            KnownClients.Remove(id);
            KnownStates.Remove(id);
            ServerDiagnostics.Debug($"[JOIN] 玩家离开: ClientId={id}");
        }
    }

    private static void LogStateMilestone(ClientSnapshot snapshot, string? previousState)
    {
        if (snapshot.Id == 0 || snapshot.State == previousState)
        {
            return;
        }

        switch (snapshot.State)
        {
            case "Connected":
                ServerDiagnostics.Debug($"[JOIN] 已通过验证，正在发送世界: {Format(snapshot)}");
                break;
            case "ProjectLoaded":
                ServerDiagnostics.Debug($"[JOIN] 已加载世界项目，正在同步实体: {Format(snapshot)}");
                break;
            case "LoadTerrain":
                ServerDiagnostics.Debug($"[JOIN] 正在加载地形: {Format(snapshot)}");
                break;
            case "Playing":
                ServerDiagnostics.Debug($"[JOIN] 加入好了: {Format(snapshot)}");
                break;
            case "NotConnected" when previousState is not null:
                ServerDiagnostics.Debug($"[JOIN] 加入中断或已离开: {Format(snapshot)}");
                break;
        }
    }

    private static ClientSnapshot Snapshot(object client)
    {
        var id = Convert.ToByte(GetProperty(client, "ID"));
        var playerGuid = GetProperty(client, "PlayerGuid") is Guid guid ? guid : Guid.Empty;
        var tokenId = GetProperty(client, "TokenId")?.ToString() ?? "";
        var nickname = GetProperty(client, "Nickname")?.ToString() ?? "";
        var state = GetProperty(client, "State")?.ToString() ?? "Unknown";
        var endpoint = GetProperty(client, "IPPoint")?.ToString()
            ?? GetPeerEndpoint(GetProperty(client, "Peer"))
            ?? "";
        return new ClientSnapshot(id, playerGuid, tokenId, nickname, state, endpoint);
    }

    private static string Format(ClientSnapshot snapshot)
    {
        var name = string.IsNullOrWhiteSpace(snapshot.Nickname) ? "<未命名>" : snapshot.Nickname;
        var endpoint = string.IsNullOrWhiteSpace(snapshot.Endpoint) ? "<unknown>" : snapshot.Endpoint;
        return $"ClientId={snapshot.Id}, Name={name}, PlayerGuid={snapshot.PlayerGuid}, State={snapshot.State}, EndPoint={endpoint}";
    }

    private static string? GetPeerEndpoint(object? peer)
    {
        if (peer is null)
        {
            return null;
        }

        var address = GetProperty(peer, "Address")?.ToString();
        var port = GetProperty(peer, "Port")?.ToString();
        return string.IsNullOrWhiteSpace(address) ? null : $"{address}:{port}";
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
