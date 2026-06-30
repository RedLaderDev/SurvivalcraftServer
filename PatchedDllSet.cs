using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SurvivalcraftServer;

internal static class PatchedDllSet
{
    private static readonly HashSet<string> FrameworkAssemblyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Microsoft",
        "netstandard",
        "mscorlib",
        "WindowsBase",
        "PresentationCore",
        "PresentationFramework"
    };

    private static readonly string[] RuntimeRootAssemblies =
    [
        "Survivalcraft",
        "Engine",
        "Mono.Android",
        "Mono.Android.Export",
        "Mono.Android.Runtime",
        "Java.Interop"
    ];

    public static string Prepare(ServerOptions options)
    {
        var sourceDir = Path.GetFullPath(options.LibDir);
        var patchedDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "patched-dll"));
        Directory.CreateDirectory(patchedDir);

        foreach (var stalePath in Directory.EnumerateFiles(patchedDir, "*.dll"))
        {
            File.Delete(stalePath);
        }

        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*.dll")
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        var patchSupportDlls = FindRequiredDlls(sourceFiles, includeFrameworkAssemblies: true).ToArray();
        var runtimeDllNames = FindRequiredDlls(sourceFiles, includeFrameworkAssemblies: false)
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in patchSupportDlls)
        {
            File.Copy(sourcePath, Path.Combine(patchedDir, Path.GetFileName(sourcePath)), overwrite: true);
        }

        PatchEngine(Path.Combine(patchedDir, "Engine.dll"), sourceDir);
        PatchSurvivalcraft(Path.Combine(patchedDir, "Survivalcraft.dll"), sourceDir);
        foreach (var copiedPath in Directory.EnumerateFiles(patchedDir, "*.dll"))
        {
            if (!runtimeDllNames.Contains(Path.GetFileNameWithoutExtension(copiedPath)))
            {
                File.Delete(copiedPath);
            }
        }

        ServerDiagnostics.Debug($"patched DLL 最小集合: {runtimeDllNames.Count} 个");
        ServerDiagnostics.Debug($"使用 patched DLL 目录: {patchedDir}");
        return patchedDir;
    }

    private static IEnumerable<string> FindRequiredDlls(Dictionary<string, string> sourceFiles, bool includeFrameworkAssemblies)
    {
        var required = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        foreach (var root in RuntimeRootAssemblies)
        {
            Enqueue(root);
        }

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!sourceFiles.TryGetValue(name, out var path))
            {
                continue;
            }

            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadWrite = false });
            foreach (var reference in module.AssemblyReferences)
            {
                Enqueue(reference.Name);
            }
        }

        return required.Select(name => sourceFiles[name]);

        void Enqueue(string? assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName)
                || (!includeFrameworkAssemblies && IsFrameworkAssembly(assemblyName))
                || !sourceFiles.ContainsKey(assemblyName)
                || !required.Add(assemblyName))
            {
                return;
            }
            pending.Enqueue(assemblyName);
        }
    }

    private static bool IsFrameworkAssembly(string assemblyName)
    {
        return FrameworkAssemblyPrefixes.Any(prefix =>
            assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static void PatchEngine(string enginePath, string sourceDir)
    {
        var resolver = CreateResolver(Path.GetDirectoryName(enginePath)!, sourceDir);
        var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
        var tempPath = enginePath + ".tmp";
        using (var module = ModuleDefinition.ReadModule(enginePath, parameters))
        {
            PatchStorageOpenFile(module);
            PatchShaderCompile(module);

            module.Write(tempPath);
        }
        File.Copy(tempPath, enginePath, overwrite: true);
        File.Delete(tempPath);
    }

    private static void PatchSurvivalcraft(string survivalcraftPath, string sourceDir)
    {
        var resolver = CreateResolver(Path.GetDirectoryName(survivalcraftPath)!, sourceDir);
        var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
        var tempPath = survivalcraftPath + ".tmp";
        using (var module = ModuleDefinition.ReadModule(survivalcraftPath, parameters))
        {
            PatchPlayerDataUpdateSpawnDialog(module);
            PatchPlayerDataOnEntityAdded(module);
            PatchHeadlessUpdateReturn(module, "Game.SubsystemAnimatedTextures", "Update", parameterCount: 1);
            PatchHeadlessUpdateReturn(module, "Game.ComponentClothing", "UpdateRenderTargets", parameterCount: 0);
            PatchHeadlessUpdateReturn(module, "Game.ComponentBlockHighlight", "Update", parameterCount: 1);
            PatchHeadlessUpdateReturn(module, "Game.ComponentLevel", "Update", parameterCount: 1);
            PatchHeadlessUpdateReturn(module, "Game.ComponentHealth", "Update", parameterCount: 1);
            PatchHeadlessUpdateReturn(module, "Game.ComponentVitalStats", "Update", parameterCount: 1);
            PatchHeadlessComponentGui(module);
            PatchHumanModelRendererOptional(module);
            PatchTerrainUpdaterKeepPendingChunks(module);
            PatchScKeyPlatformSupport(module);
            PatchMissingSubtextureFallback(module);
            PatchDuplicateLoginRemoveBroadcast(module);
            PatchNetNodeConnectionStability(module);
            PatchPeerDisconnectedLogging(module);
            PatchEmptyCatchLogging(module, "Game.SubsystemUpdate", "Update", "[HEADLESS] SubsystemUpdate swallowed exception: ");
            PatchEmptyCatchLogging(module, "Game.TerrainUpdater", "ThreadUpdateFunction", "[HEADLESS] TerrainUpdater thread swallowed exception: ");

            module.Write(tempPath);
        }
        File.Copy(tempPath, survivalcraftPath, overwrite: true);
        File.Delete(tempPath);
    }

    private static DefaultAssemblyResolver CreateResolver(params string[] searchDirectories)
    {
        var resolver = new DefaultAssemblyResolver();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in searchDirectories)
        {
            AddSearchDirectory(directory);
        }

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        foreach (var path in (trustedAssemblies ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            AddSearchDirectory(Path.GetDirectoryName(path));
        }
        return resolver;

        void AddSearchDirectory(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && added.Add(Path.GetFullPath(directory)))
            {
                resolver.AddSearchDirectory(directory);
            }
        }
    }

    private static void PatchStorageOpenFile(ModuleDefinition module)
    {
        var storage = RequireType(module, "Engine.Storage");
        var openFile = storage.Methods.Single(method =>
            method.Name == "OpenFile"
            && method.Parameters.Count == 2
            && method.Parameters[0].ParameterType.FullName == "System.String");
        var il = openFile.Body.GetILProcessor();
        var first = openFile.Body.Instructions[0];
        var assetsRoot = new VariableDefinition(module.TypeSystem.String);
        openFile.Body.Variables.Add(assetsRoot);
        openFile.Body.InitLocals = true;

        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "SCNET_ASSETS_DIR"));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(Environment).GetMethod(nameof(Environment.GetEnvironmentVariable), [typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Stloc, assetsRoot));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "app:"));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, assetsRoot));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod(nameof(string.IsNullOrEmpty), [typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, assetsRoot));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_4));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, module.ImportReference(typeof(string).GetMethod(nameof(string.Substring), [typeof(int)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(Path).GetMethod(nameof(Path.Combine), [typeof(string), typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_3));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(File).GetMethod(nameof(File.Open), [typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    private static void PatchShaderCompile(ModuleDefinition module)
    {
        var shader = RequireType(module, "Engine.Graphics.Shader");
        var shaderParameter = RequireType(module, "Engine.Graphics.ShaderParameter");
        var shaderParameterType = RequireType(module, "Engine.Graphics.ShaderParameterType");
        var compileShaders = shader.Methods.Single(method => method.Name == "CompileShaders" && method.Parameters.Count == 0);
        var deleteShaders = shader.Methods.Single(method => method.Name == "DeleteShaders" && method.Parameters.Count == 0);
        var parameterCtor = shaderParameter.Methods.Single(method =>
            method.IsConstructor
            && method.Parameters.Count == 4
            && method.Parameters[0].ParameterType.FullName == shader.FullName);
        var parametersByNameField = shader.Fields.Single(field => field.Name == "m_parametersByName");
        var parametersField = shader.Fields.Single(field => field.Name == "m_parameters");
        var glymulField = shader.Fields.Single(field => field.Name == "m_glymulParameter");

        var dictionaryType = new GenericInstanceType(module.ImportReference(typeof(Dictionary<,>)));
        dictionaryType.GenericArguments.Add(module.TypeSystem.String);
        dictionaryType.GenericArguments.Add(shaderParameter);
        var dictionaryCtor = module.ImportReference(typeof(Dictionary<string, object>).GetConstructor(Type.EmptyTypes)!);
        dictionaryCtor.DeclaringType = dictionaryType;
        var dictionaryAdd = module.ImportReference(typeof(Dictionary<string, object>).GetMethod(nameof(Dictionary<string, object>.Add))!);
        dictionaryAdd.DeclaringType = dictionaryType;

        var il = compileShaders.Body.GetILProcessor();
        var first = compileShaders.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "SCNET_HEADLESS_GRAPHICS"));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(Environment).GetMethod(nameof(Environment.GetEnvironmentVariable), [typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "1"));
        il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!)));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, deleteShaders));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Newobj, dictionaryCtor));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, parametersByNameField));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Newarr, shaderParameter));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, parametersField));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "u_glymul"));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Newobj, parameterCtor));
        il.InsertBefore(first, il.Create(OpCodes.Stfld, glymulField));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, parametersByNameField));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "u_glymul"));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, glymulField));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, dictionaryAdd));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, parametersField));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, glymulField));
        il.InsertBefore(first, il.Create(OpCodes.Stelem_Ref));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    private static void PatchPlayerDataUpdateSpawnDialog(ModuleDefinition module)
    {
        var playerData = RequireType(module, "Game.PlayerData");
        var gameWidget = RequireType(module, "Game.GameWidget");
        var getGameWidget = playerData.Methods.Single(method =>
            method.Name == "get_GameWidget"
            && method.Parameters.Count == 0
            && method.ReturnType.FullName == gameWidget.FullName);
        var updateSpawnDialog = playerData.Methods.Single(method =>
            method.Name == "UpdateSpawnDialog"
            && method.Parameters.Count == 4
            && method.Parameters[0].ParameterType.FullName == module.TypeSystem.String.FullName
            && method.Parameters[1].ParameterType.FullName == module.TypeSystem.String.FullName
            && method.Parameters[2].ParameterType.FullName == module.TypeSystem.Single.FullName
            && method.Parameters[3].ParameterType.FullName == module.TypeSystem.Boolean.FullName);

        var il = updateSpawnDialog.Body.GetILProcessor();
        var first = updateSpawnDialog.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, getGameWidget));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    private static void PatchPlayerDataOnEntityAdded(ModuleDefinition module)
    {
        var playerData = RequireType(module, "Game.PlayerData");
        var componentPlayer = RequireType(module, "Game.ComponentPlayer");
        var onEntityAdded = playerData.Methods.Single(method =>
            method.Name == "OnEntityAdded"
            && method.Parameters.Count == 1
            && method.Parameters[0].ParameterType.FullName == "GameEntitySystem.Entity");
        var entity = onEntityAdded.Parameters[0].ParameterType.Resolve()
            ?? throw new TypeLoadException("类型不存在: GameEntitySystem.Entity");
        var findComponent = entity.Methods.Single(method =>
            method.Name == "FindComponent"
            && method.HasGenericParameters
            && method.Parameters.Count == 0);
        var findComponentPlayer = new GenericInstanceMethod(findComponent);
        findComponentPlayer.GenericArguments.Add(componentPlayer);
        var importedFindComponentPlayer = module.ImportReference(findComponentPlayer);
        var componentPlayerVariable = new VariableDefinition(componentPlayer);
        onEntityAdded.Body.Variables.Add(componentPlayerVariable);
        onEntityAdded.Body.InitLocals = true;

        var playerDataGetter = componentPlayer.Methods.Single(method =>
            method.Name == "get_PlayerData"
            && method.Parameters.Count == 0
            && method.ReturnType.FullName == playerData.FullName);
        var componentPlayerField = playerData.Fields.Single(field => field.Name == "<ComponentPlayer>k__BackingField");
        var objectEquality = module.ImportReference(typeof(object).GetMethod("ReferenceEquals", [typeof(object), typeof(object)])!);

        var il = onEntityAdded.Body.GetILProcessor();
        var first = onEntityAdded.Body.Instructions[0];
        var continueAt = first;
        var returnInstruction = il.Create(OpCodes.Ret);

        il.InsertBefore(continueAt, il.Create(OpCodes.Ldstr, "SCNET_HEADLESS_GRAPHICS"));
        il.InsertBefore(continueAt, il.Create(OpCodes.Call, module.ImportReference(typeof(Environment).GetMethod(nameof(Environment.GetEnvironmentVariable), [typeof(string)])!)));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldstr, "1"));
        il.InsertBefore(continueAt, il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!)));
        il.InsertBefore(continueAt, il.Create(OpCodes.Brfalse, continueAt));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(continueAt, il.Create(OpCodes.Callvirt, importedFindComponentPlayer));
        il.InsertBefore(continueAt, il.Create(OpCodes.Stloc, componentPlayerVariable));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldloc, componentPlayerVariable));
        il.InsertBefore(continueAt, il.Create(OpCodes.Brfalse, returnInstruction));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldloc, componentPlayerVariable));
        il.InsertBefore(continueAt, il.Create(OpCodes.Callvirt, playerDataGetter));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(continueAt, il.Create(OpCodes.Call, objectEquality));
        il.InsertBefore(continueAt, il.Create(OpCodes.Brfalse, returnInstruction));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldloc, componentPlayerVariable));
        il.InsertBefore(continueAt, il.Create(OpCodes.Stfld, componentPlayerField));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ret));
        il.InsertBefore(continueAt, returnInstruction);
    }

    private static void PatchHeadlessComponentGui(ModuleDefinition module)
    {
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "Load", parameterCount: 2);
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "Update", parameterCount: 1);
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "Draw", parameterCount: 2);
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "OnEntityAdded", parameterCount: 0);
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "OnEntityRemoved", parameterCount: 0);
        PatchHeadlessUpdateReturn(module, "Game.ComponentGui", "Dispose", parameterCount: 0);
    }

    private static void PatchHumanModelRendererOptional(ModuleDefinition module)
    {
        var humanModel = RequireType(module, "Game.ComponentHumanModel");
        var modelsRenderer = RequireType(module, "Game.SubsystemModelsRenderer");
        var load = humanModel.Methods.Single(method =>
            method.Name == "Load"
            && method.Parameters.Count == 2);

        var patched = false;
        foreach (var instruction in load.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Call)
            {
                continue;
            }
            if (instruction.Operand is not GenericInstanceMethod genericMethod
                || genericMethod.Name != "FindSubsystem"
                || genericMethod.GenericArguments.All(argument => argument.FullName != modelsRenderer.FullName))
            {
                continue;
            }

            var loadThrowOnError = PreviousMeaningfulInstruction(instruction);
            if (loadThrowOnError?.OpCode == OpCodes.Ldc_I4_1)
            {
                loadThrowOnError.OpCode = OpCodes.Ldc_I4_0;
                loadThrowOnError.Operand = null;
                patched = true;
            }
        }

        if (!patched)
        {
            ServerDiagnostics.Debug("跳过 ComponentHumanModel renderer 可选补丁：未找到匹配调用，可能已经补丁过或 DLL 版本不同。");
        }
    }

    private static void PatchTerrainUpdaterKeepPendingChunks(ModuleDefinition module)
    {
        var terrainUpdater = RequireType(module, "Game.TerrainUpdater");
        var terrainChunk = RequireType(module, "Game.TerrainChunk");
        var update = terrainUpdater.Methods.Single(method => method.Name == "Update" && method.Parameters.Count == 0);
        var threadStateGetter = terrainChunk.Methods.Single(method => method.Name == "get_ThreadState" && method.Parameters.Count == 0);

        for (var i = 0; i < update.Body.Instructions.Count; i++)
        {
            var instruction = update.Body.Instructions[i];
            if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                || instruction.Operand is not MethodReference method
                || method.Name != threadStateGetter.Name
                || method.DeclaringType.FullName != terrainChunk.FullName)
            {
                continue;
            }

            var loadInvalidContents4 = instruction.Next;
            var branchToReplyResult = loadInvalidContents4?.Next;
            var branchAfterReadyAdd = branchToReplyResult?.Next?.Next?.Next?.Next?.Next?.Next?.Next;
            if (loadInvalidContents4?.OpCode != OpCodes.Ldc_I4_4
                || branchToReplyResult?.OpCode != OpCodes.Ble_S
                || branchAfterReadyAdd?.OpCode != OpCodes.Br_S
                || branchAfterReadyAdd.Operand is not Instruction skipToNextRequestedChunk)
            {
                continue;
            }

            branchToReplyResult.Operand = skipToNextRequestedChunk;
            return;
        }

        throw new MissingMethodException("Game.TerrainUpdater", "Update pending chunk branch");
    }

    private static void PatchScKeyPlatformSupport(ModuleDefinition module)
    {
        var authService = RequireType(module, "Game.ScKeyServerAuthService");
        var method = authService.Methods.Single(method =>
            method.Name == "IsPlatformSupportedBySCKey"
            && method.Parameters.Count == 0
            && method.ReturnType.FullName == module.TypeSystem.Boolean.FullName);
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.Instructions.Clear();
        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void PatchMissingSubtextureFallback(ModuleDefinition module)
    {
        var manager = RequireType(module, "Game.TextureAtlasManager");
        var method = manager.Methods.Single(method => method.Name == "GetSubtexture" && method.Parameters.Count == 1);
        var atlasTexture = manager.Fields.Single(field => field.Name == "AtlasTexture");
        var subtexture = RequireType(module, "Game.Subtexture");

        var oldInstructions = method.Body.Instructions.ToArray();
        var tryGetValue = oldInstructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .First(reference => reference.Name == "TryGetValue");
        var add = oldInstructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .First(reference => reference.Name == "Add");
        var subtextureConstructor = oldInstructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .First(reference => reference.Name == ".ctor" && reference.DeclaringType.FullName == subtexture.FullName);
        var vectorZero = oldInstructions.Select(instruction => instruction.Operand).OfType<FieldReference>()
            .First(reference => reference.Name == "Zero" && reference.DeclaringType.FullName == "Engine.Vector2");
        var vectorOne = oldInstructions.Select(instruction => instruction.Operand).OfType<FieldReference>()
            .First(reference => reference.Name == "One" && reference.DeclaringType.FullName == "Engine.Vector2");
        var subtexturesField = manager.Fields.Single(field => field.Name == "m_subtextures");

        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.InitLocals = true;
        var value = new VariableDefinition(subtexture);
        method.Body.Variables.Add(value);
        method.Body.Instructions.Clear();

        var il = method.Body.GetILProcessor();
        var createFallback = il.Create(OpCodes.Ldsfld, atlasTexture);

        il.Append(il.Create(OpCodes.Ldsfld, subtexturesField));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloca, value));
        il.Append(il.Create(OpCodes.Callvirt, tryGetValue));
        il.Append(il.Create(OpCodes.Brfalse, createFallback));
        il.Append(il.Create(OpCodes.Ldloc, value));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(createFallback);
        il.Append(il.Create(OpCodes.Ldsfld, vectorZero));
        il.Append(il.Create(OpCodes.Ldsfld, vectorOne));
        il.Append(il.Create(OpCodes.Newobj, subtextureConstructor));
        il.Append(il.Create(OpCodes.Stloc, value));
        il.Append(il.Create(OpCodes.Ldsfld, subtexturesField));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, value));
        il.Append(il.Create(OpCodes.Callvirt, add));
        il.Append(il.Create(OpCodes.Ldloc, value));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void PatchDuplicateLoginRemoveBroadcast(ModuleDefinition module)
    {
        var netNode = RequireType(module, "Game.NetWork.NetNode");
        var method = netNode.Methods.Single(method => method.Name == "RemoveClientImmediate" && method.Parameters.Count == 2);
        var queuePackage = netNode.Methods.Single(method => method.Name == "QueuePackage" && method.Parameters.Count == 1);
        var queueCall = method.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Call
            && instruction.Operand is MethodReference reference
            && reference.Name == queuePackage.Name
            && reference.DeclaringType.FullName == netNode.FullName);
        if (queueCall is null)
        {
            throw new MissingMethodException("Game.NetWork.NetNode", "RemoveClientImmediate QueuePackage(ClientPackage)");
        }

        var blockStart = queueCall;
        while (blockStart.Previous is not null && blockStart.OpCode != OpCodes.Ldarg_0)
        {
            blockStart = blockStart.Previous;
        }
        if (blockStart.OpCode != OpCodes.Ldarg_0)
        {
            throw new MissingMethodException("Game.NetWork.NetNode", "RemoveClientImmediate ClientPackage block start");
        }

        var skipTarget = queueCall.Next
            ?? throw new MissingMethodException("Game.NetWork.NetNode", "RemoveClientImmediate QueuePackage next instruction");
        var il = method.Body.GetILProcessor();
        var stringEquals = module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.InsertBefore(blockStart, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(blockStart, il.Create(OpCodes.Ldstr, "账号从其它设备登录"));
        il.InsertBefore(blockStart, il.Create(OpCodes.Call, stringEquals));
        il.InsertBefore(blockStart, il.Create(OpCodes.Brtrue, skipTarget));
    }

    private static void PatchNetNodeConnectionStability(ModuleDefinition module)
    {
        var netNode = RequireType(module, "Game.NetWork.NetNode");
        var constructor = netNode.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        var netManagerField = netNode.Fields.Single(field => field.Name == "netManager");
        var netManagerType = netManagerField.FieldType.Resolve()
            ?? throw new TypeLoadException("LiteNetLib.NetManager");
        var liteNetManagerType = netManagerType.BaseType.Resolve()
            ?? throw new TypeLoadException("LiteNetLib.LiteNetManager");
        var allowPeerAddressChange = module.ImportReference(liteNetManagerType.Fields.Single(field => field.Name == "AllowPeerAddressChange"));
        var disconnectTimeout = module.ImportReference(liteNetManagerType.Fields.Single(field => field.Name == "DisconnectTimeout"));
        var ret = constructor.Body.Instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);
        var il = constructor.Body.GetILProcessor();

        il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldfld, netManagerField));
        il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(ret, il.Create(OpCodes.Stfld, allowPeerAddressChange));

        il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(ret, il.Create(OpCodes.Ldfld, netManagerField));
        il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4, 30000));
        il.InsertBefore(ret, il.Create(OpCodes.Stfld, disconnectTimeout));
    }

    private static void PatchPeerDisconnectedLogging(ModuleDefinition module)
    {
        var netNode = RequireType(module, "Game.NetWork.NetNode");
        var method = netNode.Methods.Single(method => method.Name == "PeerDisconnectedEvent" && method.Parameters.Count == 2);
        var disconnectInfo = method.Parameters[1].ParameterType.Resolve()
            ?? throw new TypeLoadException("LiteNetLib.DisconnectInfo");
        var reasonField = module.ImportReference(disconnectInfo.Fields.Single(field => field.Name == "Reason"));
        var socketErrorField = module.ImportReference(disconnectInfo.Fields.Single(field => field.Name == "SocketErrorCode"));
        var reasonType = reasonField.FieldType;
        var socketErrorType = socketErrorField.FieldType;
        var logInformation = method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .First(reference =>
                reference.Name == "Information"
                && reference.DeclaringType.FullName == "Engine.Log"
                && reference.Parameters.Count == 1
                && reference.Parameters[0].ParameterType.FullName == module.TypeSystem.String.FullName);
        var objectToString = module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
        var stringConcat2 = module.ImportReference(typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
        var first = method.Body.Instructions[0];
        var il = method.Body.GetILProcessor();

        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "[断开] PeerDisconnected: reason="));
        il.InsertBefore(first, il.Create(OpCodes.Ldarga, method.Parameters[1]));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, reasonField));
        il.InsertBefore(first, il.Create(OpCodes.Box, reasonType));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, objectToString));
        il.InsertBefore(first, il.Create(OpCodes.Call, stringConcat2));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, ", socket="));
        il.InsertBefore(first, il.Create(OpCodes.Call, stringConcat2));
        il.InsertBefore(first, il.Create(OpCodes.Ldarga, method.Parameters[1]));
        il.InsertBefore(first, il.Create(OpCodes.Ldfld, socketErrorField));
        il.InsertBefore(first, il.Create(OpCodes.Box, socketErrorType));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, objectToString));
        il.InsertBefore(first, il.Create(OpCodes.Call, stringConcat2));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, ", peer="));
        il.InsertBefore(first, il.Create(OpCodes.Call, stringConcat2));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, objectToString));
        il.InsertBefore(first, il.Create(OpCodes.Call, stringConcat2));
        il.InsertBefore(first, il.Create(OpCodes.Call, logInformation));
    }

    private static void PatchHeadlessUpdateReturn(ModuleDefinition module, string typeName, string methodName, int parameterCount)
    {
        var type = RequireType(module, typeName);
        var method = type.Methods.Single(method => method.Name == methodName && method.Parameters.Count == parameterCount);
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        InsertHeadlessReturnGuard(module, il, first);
    }

    private static void PatchEmptyCatchLogging(ModuleDefinition module, string typeName, string methodName, string prefix)
    {
        var type = RequireType(module, typeName);
        var method = type.Methods.Single(method => method.Name == methodName && method.Parameters.Count == 0);
        var consoleWriteLine = module.ImportReference(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!);
        var stringConcat = module.ImportReference(typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
        var objectToString = module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
        var exceptionVariable = new VariableDefinition(module.ImportReference(typeof(Exception)));
        method.Body.Variables.Add(exceptionVariable);
        method.Body.InitLocals = true;

        foreach (var handler in method.Body.ExceptionHandlers.Where(handler =>
            handler.HandlerType == ExceptionHandlerType.Catch
            && handler.HandlerStart.OpCode == OpCodes.Pop))
        {
            var il = method.Body.GetILProcessor();
            var start = handler.HandlerStart;
            start.OpCode = OpCodes.Stloc;
            start.Operand = exceptionVariable;
            var cursor = start;
            foreach (var instruction in new[]
            {
                il.Create(OpCodes.Ldstr, prefix),
                il.Create(OpCodes.Ldloc, exceptionVariable),
                il.Create(OpCodes.Callvirt, objectToString),
                il.Create(OpCodes.Call, stringConcat),
                il.Create(OpCodes.Call, consoleWriteLine)
            })
            {
                il.InsertAfter(cursor, instruction);
                cursor = instruction;
            }
        }
    }

    private static void InsertHeadlessReturnGuard(ModuleDefinition module, ILProcessor il, Instruction continueAt)
    {
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldstr, "SCNET_HEADLESS_GRAPHICS"));
        il.InsertBefore(continueAt, il.Create(OpCodes.Call, module.ImportReference(typeof(Environment).GetMethod(nameof(Environment.GetEnvironmentVariable), [typeof(string)])!)));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ldstr, "1"));
        il.InsertBefore(continueAt, il.Create(OpCodes.Call, module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!)));
        il.InsertBefore(continueAt, il.Create(OpCodes.Brfalse, continueAt));
        il.InsertBefore(continueAt, il.Create(OpCodes.Ret));
    }

    private static Instruction? PreviousMeaningfulInstruction(Instruction instruction)
    {
        var current = instruction.Previous;
        while (current is not null && current.OpCode == OpCodes.Nop)
        {
            current = current.Previous;
        }
        return current;
    }

    private static TypeDefinition RequireType(ModuleDefinition module, string fullName)
    {
        return module.Types.FirstOrDefault(type => type.FullName == fullName)
            ?? throw new TypeLoadException($"类型不存在: {fullName}");
    }

}
