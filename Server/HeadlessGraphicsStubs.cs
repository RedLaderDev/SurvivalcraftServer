using System.Reflection;
using System.Runtime.CompilerServices;

namespace SurvivalcraftServer.Server;

internal sealed partial class HeadlessBootstrap
{
    private void InstallHeadlessGraphicsStubs()
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        SetStaticField(_game, "Game.SubsystemTerrain", "TerrainRenderingEnabled", false);

        var shaderType = _engine.GetType("Engine.Graphics.Shader", throwOnError: true)!;
        var emptyShader = RuntimeHelpers.GetUninitializedObject(shaderType);
        var terrainRendererType = _game.GetType("Game.TerrainRenderer", throwOnError: true)!;
        terrainRendererType.GetField("m_opaqueShader", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, emptyShader);
        terrainRendererType.GetField("m_alphaTestedShader", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, emptyShader);
        terrainRendererType.GetField("m_transparentShader", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, emptyShader);

        var labelWidgetType = _game.GetType("Game.LabelWidget", throwOnError: true)!;
        labelWidgetType.GetField("BitmapFont", BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, CreateHeadlessBitmapFont());

        var modelShaderType = _game.GetType("Game.ModelShader", throwOnError: true)!;
        var emptyModelShader = RuntimeHelpers.GetUninitializedObject(modelShaderType);
        var modelsRendererType = _game.GetType("Game.SubsystemModelsRenderer", throwOnError: true)!;
        modelsRendererType.GetField("ShaderOpaque", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, emptyModelShader);
        modelsRendererType.GetField("ShaderAlphaTested", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, emptyModelShader);
    }

    private object CreateHeadlessBitmapFont()
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("Engine assembly has not been loaded.");
        }

        var fontType = _engine.GetType("Engine.Media.BitmapFont", throwOnError: true)!;
        var glyphType = _engine.GetType("Engine.Media.BitmapFont+Glyph", throwOnError: true)!;
        var vector2Type = _engine.GetType("Engine.Vector2", throwOnError: true)!;
        var zero = Activator.CreateInstance(vector2Type, [0f, 0f])!;
        var spacing = Activator.CreateInstance(vector2Type, [0f, 0f])!;
        var glyphs = Array.CreateInstance(glyphType, 95);
        for (var i = 0; i < 95; i++)
        {
            var code = (char)(32 + i);
            var glyph = Activator.CreateInstance(glyphType, [code, zero, zero, zero, 8f])!;
            glyphs.SetValue(glyph, i);
        }

        var font = Activator.CreateInstance(fontType, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [], culture: null)!;
        var initialize = fontType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Engine.Media.BitmapFont", "Initialize");
        initialize.Invoke(font, [null, null, glyphs, ' ', 16f, spacing, 1f]);
        return font;
    }
}
