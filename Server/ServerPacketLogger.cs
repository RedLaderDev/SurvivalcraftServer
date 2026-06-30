using System.Collections;
using System.Reflection;

namespace SurvivalcraftServer;

internal static class ServerPacketLogger
{
    private static readonly Dictionary<string, int> PendingCounts = [];
    private static readonly HashSet<string> ReceiveKeys = [];
    private static Type? _playerDataPackageType;
    private static Type? _clientPackageType;
    private static Type? _projectPackageType;
    private static Type? _entityPackageType;
    private static Type? _terrainPackageType;
    private static Type? _componentPlayerPackageType;
    private static Type? _bodyPackageType;
    private static Type? _timePackageType;
    private static Type? _skyPackageType;
    private static Type? _projectNetType;
    private static Type? _playerDataType;
    private static readonly HashSet<string> PlayerCreateFallbackKeys = [];

    public static void Install(Assembly gameAssembly, object netNode)
    {
        _playerDataPackageType = gameAssembly.GetType("Game.NetWork.Packages.PlayerDataPackage", throwOnError: false);
        _clientPackageType = gameAssembly.GetType("Game.NetWork.Packages.ClientPackage", throwOnError: false);
        _projectPackageType = gameAssembly.GetType("Game.NetWork.Packages.ProjectPackage", throwOnError: false);
        _entityPackageType = gameAssembly.GetType("Game.NetWork.Packages.EntityPackage", throwOnError: false);
        _terrainPackageType = gameAssembly.GetType("Game.NetWork.Packages.SubsystemTerrainPackage", throwOnError: false);
        _componentPlayerPackageType = gameAssembly.GetType("Game.NetWork.Packages.ComponentPlayerPackage", throwOnError: false);
        _bodyPackageType = gameAssembly.GetType("Game.NetWork.Packages.SubsystemBodyPackage", throwOnError: false);
        _timePackageType = gameAssembly.GetType("Game.NetWork.Packages.SubsystemTimePackage", throwOnError: false);
        _skyPackageType = gameAssembly.GetType("Game.NetWork.Packages.SubsystemSkyPackage", throwOnError: false);
        _projectNetType = gameAssembly.GetType("Game.NetWork.ProjectNet", throwOnError: false);
        _playerDataType = gameAssembly.GetType("Game.PlayerData", throwOnError: false);
        PendingCounts.Clear();
        ReceiveKeys.Clear();
        PlayerCreateFallbackKeys.Clear();
        AttachReceiveLogger(netNode);
        ServerDiagnostics.Debug($"[PKT] 关键包日志 hook 已启用。");
    }

    public static void Tick(object netNode)
    {
        LogPendingPackages(netNode);
    }

    private static void AttachReceiveLogger(object netNode)
    {
        var eventInfo = netNode.GetType().GetEvent("OnRecieve", BindingFlags.Public | BindingFlags.Instance);
        if (eventInfo is null)
        {
            ServerDiagnostics.Debug($"[PKT] 未找到 NetNode.OnRecieve，无法挂接收包日志。");
            return;
        }

        var method = typeof(ServerPacketLogger).GetMethod(nameof(OnReceive), BindingFlags.NonPublic | BindingFlags.Static)!;
        var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType!, method);
        var field = netNode.GetType().GetField("OnRecieve", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null)
        {
            var existing = field.GetValue(netNode) as Delegate;
            field.SetValue(netNode, existing is null ? handler : Delegate.Combine(handler, existing));
            return;
        }

        eventInfo.AddEventHandler(netNode, handler);
    }

    private static void OnReceive(object netNode, IEnumerable packagesWithMetas)
    {
        foreach (var item in packagesWithMetas)
        {
            var package = GetTupleField(item, "package", "Item1");
            if (package is null || !IsImportant(package.GetType()))
            {
                continue;
            }

            var from = GetProperty(package, "From");
            var clientId = GetProperty(from, "ID")?.ToString() ?? "?";
            var state = GetProperty(from, "State")?.ToString() ?? "?";
            var detail = DescribePackage(package);
            var key = $"{DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond}:{clientId}:{detail}";
            if (ReceiveKeys.Add(key))
            {
                ServerDiagnostics.Debug($"[PKT] 收到 ClientId={clientId}, State={state}: {detail}");
            }

            SchedulePlayerCreateRepair(netNode, package);
        }
    }

    private static void SchedulePlayerCreateRepair(object netNode, object package)
    {
        if (_playerDataPackageType is null || _projectNetType is null || _playerDataType is null)
        {
            return;
        }
        if (package.GetType() != _playerDataPackageType || GetField(package, "m_type")?.ToString() != "Create")
        {
            return;
        }

        var from = GetProperty(package, "From");
        var clientId = GetProperty(from, "ID")?.ToString() ?? "?";
        var clientGuid = GetProperty(from, "PlayerGuid") is Guid guid ? guid : Guid.Empty;
        var playerName = TryGetCreatePlayerName(package);
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(Create) 玩家名: ClientId={clientId}, Name={playerName}");
        }
        NormalizeCreatePackageGuid(package, clientGuid, clientId);
        var key = $"{clientId}:{clientGuid}";
        if (!PlayerCreateFallbackKeys.Add(key))
        {
            return;
        }

        var capturedFrom = from;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200).ConfigureAwait(false);
                TryRepairPlayerCreate(netNode, package, capturedFrom, clientGuid, clientId);
            }
            catch (Exception ex)
            {
                ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(Create) 兜底任务异常: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private static void NormalizeCreatePackageGuid(object package, Guid clientGuid, string clientId)
    {
        if (clientGuid == Guid.Empty)
        {
            return;
        }

        var valuesDictionary = GetField(package, "m_vd");
        var indexer = valuesDictionary?.GetType()
            .GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        if (valuesDictionary is null || indexer is null)
        {
            ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(Create) 无法修正 PlayerGUID: ClientId={clientId}");
            return;
        }

        indexer.SetValue(valuesDictionary, clientGuid, ["PlayerGUID"]);
        ServerDiagnostics.Debug($"[PKT] 已修正 Create.PlayerGUID -> ClientId={clientId}, PlayerGuid={clientGuid}");
    }

    private static void TryRepairPlayerCreate(object netNode, object package, object? from, Guid clientGuid, string clientId)
    {
        var projectNetType = _projectNetType;
        var playerDataPackageType = _playerDataPackageType;
        var playerDataType = _playerDataType;
        if (projectNetType is null || playerDataPackageType is null || playerDataType is null)
        {
            return;
        }

        var project = projectNetType.GetField("project", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var subsystemPlayers = projectNetType.GetField("subsystemPlayers", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (project is null || subsystemPlayers is null || from is null)
        {
            ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(Create) 兜底跳过: project/subsystem/client 为空, ClientId={clientId}");
            return;
        }

        var playerData = FindPlayerData(subsystemPlayers, clientGuid);
        if (playerData is null)
        {
            playerData = TryCreatePlayerData(project, subsystemPlayers, package, clientGuid, clientId);
        }

        if (playerData is null)
        {
            ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(Create) 处理后仍找不到玩家数据: ClientId={clientId}, PlayerGuid={clientGuid}");
            return;
        }

        RepairClientNicknameAndBroadcast(netNode, from, playerData, clientId, clientGuid);

        if (HasPendingAddPlayerFor(netNode, from))
        {
            ServerDiagnostics.Debug($"[PKT] PlayerDataPackage(AddPlayer) 已在队列中: ClientId={clientId}");
            return;
        }

        var ctor = playerDataPackageType.GetConstructor([playerDataType, typeof(bool)]);
        if (ctor is null)
        {
            ServerDiagnostics.Debug($"[PKT] 找不到 PlayerDataPackage(PlayerData,bool) 构造函数。");
            return;
        }

        var reply = ctor.Invoke([playerData, false]);
        SetProperty(reply, "To", from);
        netNode.GetType().GetMethod("QueuePackage", BindingFlags.Public | BindingFlags.Instance)?.Invoke(netNode, [reply]);
        ServerDiagnostics.Debug($"[PKT] 兜底补发 PlayerDataPackage(AddPlayer) -> ClientId={clientId}, PlayerGuid={clientGuid}");
    }

    private static void RepairClientNicknameAndBroadcast(object netNode, object client, object playerData, string clientId, Guid clientGuid)
    {
        var playerName = GetProperty(playerData, "Name")?.ToString();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        var oldNickname = GetProperty(client, "Nickname")?.ToString();
        if (string.IsNullOrWhiteSpace(oldNickname))
        {
            SetProperty(client, "Nickname", playerName);
            ServerDiagnostics.Debug($"[PKT] 已回填 Client.Nickname: ClientId={clientId}, Name={playerName}");
        }

        BroadcastClientList(netNode, clientId, playerName);
        BroadcastPlayerModify(netNode, playerData, clientId, clientGuid, playerName);
    }

    private static void BroadcastClientList(object netNode, string clientId, string playerName)
    {
        var clientPackageType = _clientPackageType;
        if (clientPackageType is null)
        {
            return;
        }

        if (GetField(netNode, "Clients") is not IDictionary clients)
        {
            return;
        }

        var ctor = clientPackageType.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType.IsGenericType
                    && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            });

        if (ctor is null)
        {
            ServerDiagnostics.Debug($"[PKT] 找不到 ClientPackage(IEnumerable<Client>) 构造函数。");
            return;
        }

        var values = clients.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)?.GetValue(clients);
        if (values is null)
        {
            return;
        }

        var queuePackage = netNode.GetType().GetMethod("QueuePackage", BindingFlags.Public | BindingFlags.Instance);
        foreach (DictionaryEntry entry in clients)
        {
            var target = entry.Value;
            if (target is null)
            {
                continue;
            }

            var syncList = ctor.Invoke([values]);
            SetProperty(syncList, "To", target);
            queuePackage?.Invoke(netNode, [syncList]);
        }
        ServerDiagnostics.Debug($"[PKT] 已广播 ClientPackage(SyncList) 刷新昵称: ClientId={clientId}, Name={playerName}");
    }

    private static void BroadcastPlayerModify(object netNode, object playerData, string clientId, Guid clientGuid, string playerName)
    {
        var playerDataPackageType = _playerDataPackageType;
        var playerDataType = _playerDataType;
        var dataType = playerDataPackageType is null ? null : ResolveNestedEnum(playerDataPackageType, "DataType");
        if (playerDataPackageType is null || playerDataType is null || dataType is null)
        {
            return;
        }

        var ctor = playerDataPackageType.GetConstructor([playerDataType, dataType]);
        if (ctor is null)
        {
            ServerDiagnostics.Debug($"[PKT] 找不到 PlayerDataPackage(PlayerData,DataType) 构造函数。");
            return;
        }

        var modifyType = Enum.Parse(dataType, "Modify");
        var modify = ctor.Invoke([playerData, modifyType]);
        netNode.GetType().GetMethod("QueuePackage", BindingFlags.Public | BindingFlags.Instance)?.Invoke(netNode, [modify]);
        ServerDiagnostics.Debug($"[PKT] 已广播 PlayerDataPackage(Modify) 刷新玩家名: ClientId={clientId}, PlayerGuid={clientGuid}, Name={playerName}");
    }

    private static object? TryCreatePlayerData(object project, object subsystemPlayers, object package, Guid clientGuid, string clientId)
    {
        try
        {
            var playerData = Activator.CreateInstance(_playerDataType!, [project])!;
            var valuesDictionary = GetField(package, "m_vd")
                ?? throw new InvalidOperationException("Create 包缺少 m_vd。");
            _playerDataType!.GetMethod("Load", BindingFlags.Public | BindingFlags.Instance)?.Invoke(playerData, [valuesDictionary]);
            _playerDataType.GetProperty("PlayerGUID", BindingFlags.Public | BindingFlags.Instance)?.SetValue(playerData, clientGuid);
            subsystemPlayers.GetType().GetMethod("AddPlayerData", BindingFlags.Public | BindingFlags.Instance)?.Invoke(subsystemPlayers, [playerData]);
            ServerDiagnostics.Debug($"[PKT] 兜底创建玩家数据: ClientId={clientId}, PlayerGuid={clientGuid}");
            return playerData;
        }
        catch (TargetInvocationException ex)
        {
            ServerDiagnostics.Debug($"[PKT] 兜底创建玩家数据失败: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            return FindPlayerData(subsystemPlayers, clientGuid);
        }
        catch (Exception ex)
        {
            ServerDiagnostics.Debug($"[PKT] 兜底创建玩家数据失败: {ex.GetType().Name}: {ex.Message}");
            return FindPlayerData(subsystemPlayers, clientGuid);
        }
    }

    private static object? FindPlayerData(object subsystemPlayers, Guid playerGuid)
    {
        if (subsystemPlayers.GetType().GetProperty("PlayersData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(subsystemPlayers) is not IEnumerable players)
        {
            return null;
        }

        foreach (var player in players)
        {
            if (GetProperty(player, "PlayerGUID") is Guid guid && guid == playerGuid)
            {
                return player;
            }
        }
        return null;
    }

    private static string? TryGetCreatePlayerName(object package)
    {
        var valuesDictionary = GetField(package, "m_vd");
        return GetValuesDictionaryValue(valuesDictionary, "Name")?.ToString();
    }

    private static object? GetValuesDictionaryValue(object? valuesDictionary, string key)
    {
        if (valuesDictionary is null)
        {
            return null;
        }

        var getValue = valuesDictionary.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetValue"
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(string));
        if (getValue is not null)
        {
            return getValue.MakeGenericMethod(typeof(string)).Invoke(valuesDictionary, [key, string.Empty]);
        }

        var indexer = valuesDictionary.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        return indexer?.GetValue(valuesDictionary, [key]);
    }

    private static Type? ResolveNestedEnum(Type type, string enumName)
    {
        return type.GetNestedType(enumName, BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static bool HasPendingAddPlayerFor(object netNode, object client)
    {
        if (GetField(netNode, "PendingPackages") is not IEnumerable packages)
        {
            return false;
        }

        foreach (var pending in packages)
        {
            if (pending is null || pending.GetType() != _playerDataPackageType)
            {
                continue;
            }
            if (GetField(pending, "m_type")?.ToString() != "AddPlayer")
            {
                continue;
            }
            if (ReferenceEquals(GetProperty(pending, "To"), client))
            {
                return true;
            }
        }
        return false;
    }

    private static void LogPendingPackages(object netNode)
    {
        if (GetField(netNode, "PendingPackages") is not IEnumerable packages)
        {
            return;
        }

        var counts = new Dictionary<string, int>();
        var syncRoot = packages is ICollection collection ? collection.SyncRoot : packages;
        lock (syncRoot)
        {
            foreach (var package in packages)
            {
                if (package is null || !IsImportant(package.GetType()))
                {
                    continue;
                }

                var key = DescribePackage(package);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var (key, count) in counts)
        {
            if (!PendingCounts.TryGetValue(key, out var previous) || previous != count)
            {
                ServerDiagnostics.Debug($"[PKT] 待发送 {key}: {count}");
                PendingCounts[key] = count;
            }
        }

        foreach (var key in PendingCounts.Keys.ToArray())
        {
            if (!counts.ContainsKey(key))
            {
                PendingCounts.Remove(key);
            }
        }
    }

    private static bool IsImportant(Type type)
    {
        return type == _playerDataPackageType
            || type == _clientPackageType
            || type == _projectPackageType
            || type == _entityPackageType
            || type == _terrainPackageType
            || type == _componentPlayerPackageType
            || type == _bodyPackageType
            || type == _timePackageType
            || type == _skyPackageType;
    }

    private static string DescribePackage(object package)
    {
        var type = package.GetType();
        var name = type.Name;
        if (type == _playerDataPackageType)
        {
            name += $"({GetField(package, "m_type")})";
        }
        else if (type == _clientPackageType)
        {
            name += $"({GetField(package, "eventType")})";
        }
        else if (type == _entityPackageType)
        {
            name += $"({GetField(package, "m_type")})";
        }
        else if (type == _terrainPackageType)
        {
            name += $"({GetField(package, "m_type")})";
            AppendListCount(package, "RelateChunks", ref name);
            AppendListCount(package, "Chunks", ref name);
        }
        else if (type == _componentPlayerPackageType)
        {
            name += $"({GetField(package, "m_type")})";
        }
        else if (type == _bodyPackageType)
        {
            name += $"({GetField(package, "m_type")})";
        }

        var to = GetProperty(package, "To");
        var except = GetProperty(package, "Except");
        var toId = GetProperty(to, "ID")?.ToString();
        var exceptId = GetProperty(except, "ID")?.ToString();
        if (!string.IsNullOrEmpty(toId))
        {
            name += $" -> {toId}";
        }
        if (!string.IsNullOrEmpty(exceptId))
        {
            name += $" except {exceptId}";
        }
        return name;
    }

    private static void AppendListCount(object package, string fieldName, ref string name)
    {
        if (GetField(package, fieldName) is ICollection collection)
        {
            name += $" {fieldName}={collection.Count}";
        }
    }

    private static object? GetTupleField(object instance, params string[] names)
    {
        foreach (var name in names)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is not null)
            {
                return field.GetValue(instance);
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

    private static void SetProperty(object? instance, string name, object? value)
    {
        instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(instance, value);
    }
}
