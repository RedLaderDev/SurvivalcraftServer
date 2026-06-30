# SurvivalcraftServer

SurvivalcraftServer 是一个把 Survivalcraft 联机服务端运行在普通 .NET 进程里的 headless server 包装器。

它会加载 `Survivalcraft.dll` / `Engine.dll`，在启动前对原始 DLL 做最小运行时补丁，初始化资源、世界、网络服务和 SCKey 配置，然后调用游戏内的 `Game.NetWork.NetNode.StartServer(port)`。

## 当前状态

- 可以在无图形界面的 Linux/.NET 环境中启动服务端。
- 支持单世界存档目录，不再使用默认 `Worlds/` 多世界目录。
- 支持 `config.toml` 配置端口、存档、资源、SCKey、材质包/家具包等路径。
- 支持首次启动自动创建 `config.toml`。
- 支持在 storage 下缺少 `lib/` 或 `assets/` 时，从程序输出目录导出内置目录。
- 支持 `--debug` 打开本项目注入的 hook 调试日志。
- 已针对 headless 环境跳过部分 GUI/渲染组件，并补了网络 peer 地址变化兼容。

这个项目仍然依赖原游戏 DLL 的内部实现。遇到客户端版本、DLL 版本或资源包变化时，补丁点可能需要同步调整。

## 目录结构

推荐运行目录：

```text
storage/
  config.toml
  World/
  FurniturePacks/
  CharacterSkins/
  TexturePacks/
  Login/
  ServerSetting.json
  lib/
    Survivalcraft.dll
    Engine.dll
    ...
  assets/
    Content.scpak
    Lit.vsh
    Lit.psh
    ...
```

默认 `--storage` 是当前目录。无参数启动时会在当前目录创建/读取 `config.toml`。

## 准备环境

需要：

- .NET SDK，当前项目目标框架是 `net10.0`。
- `lib/`：游戏 DLL 和运行依赖，至少包含 `Survivalcraft.dll`、`Engine.dll`、`LiteNetLib.dll` 等。公开仓库不包含这些文件，需要本地自行提供。
- `assets/`：游戏资源，至少包含 `Content.scpak` 和 shader 文件。公开仓库不包含这些文件，需要本地自行提供。
- `Mono.Cecil` 会通过 NuGet 自动还原，用于启动前改写游戏 DLL。

## 快速启动

从仓库根目录运行：

```bash
dotnet run --project SurvivalcraftServer
```

使用独立 storage 目录：

```bash
dotnet run --project SurvivalcraftServer -- --storage SurvivalcraftServer/server
```

打开本项目 hook 调试日志：

```bash
dotnet run --project SurvivalcraftServer -- --storage SurvivalcraftServer/server --debug
```

启动后客户端连接 `config.toml` 里的 `port`，默认端口是 `25565`。

## 命令

CLI 只保留少量运行控制参数。服务端配置都写在 `config.toml`。

```bash
dotnet run --project SurvivalcraftServer
dotnet run --project SurvivalcraftServer -- start [--storage PATH] [--debug]
dotnet run --project SurvivalcraftServer -- probe [--storage PATH] [--debug]
dotnet run --project SurvivalcraftServer -- selftest [--storage PATH] [--debug]
```

命令说明：

- `start`：默认命令，加载配置、初始化世界并启动服务端。
- `probe`：只加载 DLL 并检查关键类型是否存在，不启动服务器。
- `selftest`：启动服务端后用同一套 DLL 创建本地客户端做握手测试。
- `--storage PATH`：配置和默认数据根目录，默认当前目录。
- `--debug`：显示本项目自定义 hook 日志；游戏自己的 INFO/WARNING 日志不受它控制。

## 配置文件

首次启动会自动创建 `config.toml`。示例：

```toml
# Survivalcraft headless server 配置文件。
# 服务端 UDP 端口，客户端连接时填写这个端口。
port = 25565

# 世界显示名称，只在创建新世界时写入存档元数据。
world = "World"

# 运行数据根目录。相对路径基于 config.toml 所在目录解析。
data_dir = "."
# 唯一世界存档目录。相对路径基于 data_dir 解析，不会再创建 Worlds/ 多世界目录。
world_dir = "World"
# 家具包目录。相对路径基于 data_dir 解析。
furniture_packs_dir = "FurniturePacks"
# 角色皮肤目录。相对路径基于 data_dir 解析。
character_skins_dir = "CharacterSkins"
# 方块纹理包目录。相对路径基于 data_dir 解析。
texture_packs_dir = "TexturePacks"

# 世界种子。留空表示创建新世界时随机生成；已有世界始终使用存档内保存的种子。
seed = ""
# 最大在线玩家数，只在创建新世界时写入世界设置。
max_players = 20
# 服务端主循环每次 tick 后的休眠毫秒数。
tick_ms = 50

# 是否要求客户端使用 SCKey 登录验证。开启前必须同时填写 sc_key_server_id 和 sc_key_token。
check_login = false
# SCKey 服务端绑定 ID。check_login = true 时必填。
sc_key_server_id = ""
# SCKey 服务端显示名，仅用于写入游戏兼容配置。
sc_key_server_name = ""
# SCKey 服务端令牌。check_login = true 时必填；不要提交到仓库。
sc_key_token = ""

# 游戏资源目录，里面应包含 Content.scpak 和 shader 等资源文件。相对路径基于 config.toml 所在目录解析。
assets_dir = "assets"
# 游戏 DLL 目录，里面应包含 Survivalcraft.dll、Engine.dll 和运行所需依赖。相对路径基于 config.toml 所在目录解析。
lib_dir = "lib"
```

路径解析规则：

- `storage` 决定 `config.toml` 的位置。
- `data_dir`、`assets_dir`、`lib_dir` 的相对路径基于 `config.toml` 所在目录。
- `world_dir`、`furniture_packs_dir`、`character_skins_dir`、`texture_packs_dir` 的相对路径基于 `data_dir`。
- `seed = ""` 只影响新建世界；已有 `World/` 会继续使用存档里的种子。

## SCKey 登录

如果要开启服务器验证：

```toml
check_login = true
sc_key_server_id = "你的服务器绑定 ID"
sc_key_server_name = "服务器显示名"
sc_key_token = "你的服务器令牌"
```

启动时程序会写入兼容配置：

- `ServerSetting.json`
- `Login/login_config.json`
- `data_dir/Login/login_config.json`

`sc_key_token` 是敏感信息，不要提交，也不要贴到公开日志。

## 构建和发布

构建：

```bash
dotnet build SurvivalcraftServer/SurvivalcraftServer.csproj
```

发布示例：

```bash
dotnet publish SurvivalcraftServer/SurvivalcraftServer.csproj -c Release -o out/SurvivalcraftServer
```

如果本地存在 `SurvivalcraftServer/lib/**` 和 `SurvivalcraftServer/assets/**`，项目会把它们复制到输出/发布目录。运行时如果 storage 下没有 `lib/` 或 `assets/`，会从程序输出目录导出一份。

## Windows 支持

运行目标本身是 .NET，代码尽量使用跨平台路径，`Mono.Cecil` 也已经改为 NuGet 依赖。Windows 上仍需要自行提供 `lib/` 中的游戏 DLL 和 `assets/` 资源，并验证 headless 图形替身与资源导出流程。

## 常见问题

### `config.toml` 在哪里？

默认在运行命令的当前目录。指定 `--storage PATH` 后在 `PATH/config.toml`。

### 每次启动都像新世界？

确认 `world_dir = "World"` 指向同一个目录，并且不要删除 `World/`。`world` 只是世界显示名；真正复用的是 `world_dir` 里的存档文件。

### 留空 seed 会不会每次改变已有世界？

不会。`seed = ""` 只在创建新世界时随机生成。已有世界会读取存档内保存的种子。

### 客户端弹 `PeerNotFound`

当前补丁已开启 LiteNetLib peer 地址变化支持，并把断线超时提高到 30 秒。若仍然出现，服务端日志里应有：

```text
[断开] PeerDisconnected: reason=..., socket=..., peer=...
```

把这段日志和客户端弹窗时间点一起用于继续排查。

### `服务器ScKeyToken为空`

如果 `check_login = true`，必须填写 `sc_key_server_id` 和 `sc_key_token`。如果只是本地测试，可以先设为：

```toml
check_login = false
```

### 启动失败 `BadImageFormatException` 或 `Common Language Runtime detected an invalid program`

这通常说明 DLL 补丁 IL 与当前游戏 DLL 版本不匹配。先执行：

```bash
dotnet run --project SurvivalcraftServer -- probe --storage SurvivalcraftServer/server --debug
```

如果 `probe` 正常但 `start` 失败，需要检查最近改过的 `PatchedDllSet.cs` 补丁点。

## 开发说明

主要入口：

- `Program.cs`：CLI、配置加载、启动流程。
- `ServerConfig.cs` / `ServerOptions.cs`：`config.toml` 和 CLI 参数。
- `HeadlessBootstrap.cs`：headless 初始化主流程。
- `HeadlessProjectLoader.cs`：单世界加载/创建。
- `PatchedDllSet.cs`：启动前 DLL 补丁。
- `ServerPacketLogger.cs`、`ServerJoinLogger.cs`、`TerrainRuntimeLogger.cs`：调试日志 hook。

修改补丁后建议至少执行：

```bash
dotnet build SurvivalcraftServer/SurvivalcraftServer.csproj
timeout 18s dotnet SurvivalcraftServer/bin/Debug/net10.0/SurvivalcraftServer.dll --storage SurvivalcraftServer/server
```
