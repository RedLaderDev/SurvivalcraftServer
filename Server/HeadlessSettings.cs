using System.Reflection;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private void InitializeServerSettings()
    {
        SetStaticProperty(_game, "Game.SettingsManager", "LiteNetLibLogLevel", "Error");
        SetStaticProperty(_game, "Game.SettingsManager", "Server_ChunkSendPeriod", 0.5);
        SetStaticProperty(_game, "Game.SettingsManager", "Server_ChunkCountSendPer", 50);
        SetStaticProperty(_game, "Game.SettingsManager", "VisibilityRange", 128);
        SetStaticProperty(_game, "Game.SettingsManager", "MultithreadedTerrainUpdate", false);
        SetStaticProperty(_game, "Game.SettingsManager", "OnlineAccessToken", Guid.NewGuid().ToString());
        SetStaticProperty(_game, "Game.SettingsManager", "UserId", string.Empty);
        SetStaticProperty(_game, "Game.SettingsManager", "LastLaunchedVersion", "x26.06.19");
        SetStaticProperty(_game, "Game.SettingsManager", "BlocksTextureFileName", string.Empty);
        SetStaticProperty(_game, "Game.SettingsManager", "LiteNetLibLogLevel", "Error");
        SetStaticProperty(_game, "Game.SettingsManager", "WillEnterServer", string.Empty);
        SetStaticProperty(_game, "Game.SettingsManager", "WillEnterServerPwd", string.Empty);
        SetEnumStaticProperty("Game.SettingsManager", "CommunityContentMode", "Normal");
        SetEnumStaticProperty("Game.SettingsManager", "ResolutionMode", "High");
        SetEnumStaticProperty("Game.SettingsManager", "SkyRenderingMode", "Full");
        SetEnumStaticProperty("Game.SettingsManager", "ScreenshotSize", "ScreenSize");
        SetEnumStaticProperty("Game.SettingsManager", "MoveControlMode", "Pad");
        SetEnumStaticProperty("Game.SettingsManager", "LookControlMode", "EntireScreen");
        SetEnumStaticProperty("Game.SettingsManager", "ScreenLayout1", "Single");
        SetEnumStaticProperty("Game.SettingsManager", "ScreenLayout2", "DoubleHorizontal");
        SetEnumStaticProperty("Game.SettingsManager", "ScreenLayout3", "TripleHorizontal");
        SetEnumStaticProperty("Game.SettingsManager", "ScreenLayout4", "Quadruple");
    }

    private void SetEnumStaticProperty(string typeName, string propertyName, string enumName)
    {
        var type = _game.GetType(typeName, throwOnError: true)!;
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)!;
        property.SetValue(null, Enum.Parse(property.PropertyType, enumName));
    }

    private static void InitializeEngineFrameHooks(Assembly engine)
    {
        InvokeInternalStatic(engine, "Engine.Dispatcher", "Initialize");
    }

    private static void SetDisplayViewport(Assembly engine)
    {
        var point2 = engine.GetType("Engine.Point2", throwOnError: true)!;
        var viewport = engine.GetType("Engine.Graphics.Viewport", throwOnError: true)!;
        var display = engine.GetType("Engine.Graphics.Display", throwOnError: true)!;
        var backbuffer = Activator.CreateInstance(point2, [1280, 720]);
        display.GetProperty("BackbufferSize", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, backbuffer);
        display.GetProperty("Viewport", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, Activator.CreateInstance(viewport, [0, 0, 1280, 720, 0f, 1f]));
    }
}
