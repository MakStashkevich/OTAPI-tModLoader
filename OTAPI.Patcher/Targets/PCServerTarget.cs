using ModFramework;
using ModFramework.Modules.CSharp;
using Mono.Cecil;
using MonoMod.Utils;
using OTAPI.Patcher.Resolvers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using static ModFramework.ModContext;

namespace OTAPI.Patcher.Targets;

[MonoMod.MonoModIgnore]
public class PCServerTarget : IServerPatchTarget
{
    private static bool IsSafeHookType(TypeReference type)
    {
        if (type.MetadataType is MetadataType.Void or MetadataType.Boolean or MetadataType.Char
            or MetadataType.SByte or MetadataType.Byte or MetadataType.Int16 or MetadataType.UInt16
            or MetadataType.Int32 or MetadataType.UInt32 or MetadataType.Int64 or MetadataType.UInt64
            or MetadataType.Single or MetadataType.Double or MetadataType.String or MetadataType.Object)
            return true;

        if (type is ByReferenceType byReference)
            return IsSafeHookType(byReference.ElementType);
        if (type is ArrayType array)
            return IsSafeHookType(array.ElementType);
        if (type is PointerType or FunctionPointerType or GenericParameter)
            return false;
        if (type is GenericInstanceType generic)
        {
            if (generic.GenericArguments.Any(argument => !IsSafeHookType(argument)))
                return false;
            type = generic.ElementType;
        }

        TypeDefinition definition;
        try { definition = type.Resolve(); }
        catch { return false; }

        if (definition.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute"))
            return false;

        // HookEmitter puts return values and parameters into public event-args fields.
        // A private nested tML type there makes every type in OTAPI unloadable.
        for (var current = definition; current is not null; current = current.DeclaringType)
            if (!(current.IsPublic || current.IsNestedPublic))
                return false;

        return true;
    }

    private static void CreateSafeHooks(TypeDefinition type, ModFwModder modder)
    {
        var eventAccessors = type.Events
            .SelectMany(evt => new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod })
            .Where(method => method is not null)
            .ToHashSet();

        foreach (var method in type.Methods.ToArray())
        {
            if (!method.HasBody || method.IsConstructor || method.HasGenericParameters || eventAccessors.Contains(method))
                continue;
            // SendData already has a dedicated OTAPI wrapper in Terraria.NetMessage.Both.cs.
            // Wrapping that replacement again creates an invalid mfwh_orig_SendData chain in tML 1.4.4.9.
            if (type.FullName == "Terraria.NetMessage" && method.Name == "SendData")
                continue;
            if (!IsSafeHookType(method.ReturnType) || method.Parameters.Any(parameter => !IsSafeHookType(parameter.ParameterType)))
            {
                Console.WriteLine($"[OTAPI] Skipping hook with unsupported signature: {method.FullName}");
                continue;
            }

            method.CreateHook(modder);
        }
    }

    public ModContext ModContext { get; } = new("Terraria");

    public virtual string DisplayText => "OTAPI PC Server";

    public virtual string InstallDestination { get; } = Environment.CurrentDirectory;

    public virtual string ModificationsDirectory => Path.Combine(ModContext.BaseDirectory, "modifications");

    public virtual IFileResolver FileResolver { get; } = new PCFileResolver();

    public virtual NugetPackageBuilder NugetPackager { get; } = new("OTAPI.PC.nupkg", Path.Combine(AppContext.BaseDirectory, "../../../../docs/OTAPI.PC.nuspec"));
    public virtual MarkdownDocumentor MarkdownDocumentor { get; } = new("OTAPI.PC.Server.mfw.md");

    public virtual string ArtifactName { get; } = "artifact-pc";
    public virtual string MainAssemblyFileName => "OTAPI.dll";

    public virtual bool PublicEverything => true;
    public virtual bool GenerateSymbols => false;

    public virtual string GetEmbeddedResourcesDirectory(string fileinput)
    {
        return Path.Combine(ModContext.BaseDirectory, Path.GetDirectoryName(fileinput));
    }

    public virtual async Task<string> DownloadServerAsync()
    {
        var localTargetDirectory = Environment.GetEnvironmentVariable("OTAPI_TML_PATH")?.Trim();
        if (!String.IsNullOrWhiteSpace(localTargetDirectory))
        {
            if (!Directory.Exists(localTargetDirectory))
                throw new DirectoryNotFoundException($"OTAPI_TML_PATH does not exist: {localTargetDirectory}");
            return FileResolver.DetermineInputAssembly(localTargetDirectory);
        }

        var zipUrl = this.GetZipUrl();
        var zipPath = await Common.DownloadZipAsync(zipUrl);
        var extracted = Common.ExtractZip(zipPath);

        return FileResolver.DetermineInputAssembly(extracted);
    }

    public virtual void MergeReLogic(ModFwModder modder, string embeddedResources)
    {
        var path = Directory.GetFiles(embeddedResources)
            .Single(x => Path.GetFileName(x).Equals("ReLogic.dll", StringComparison.CurrentCultureIgnoreCase));

        modder.ReadMod(path);
        modder.RelinkAssembly("ReLogic");
    }

    public virtual void AddVersion(ModFwModder modder)
    {
        modder.AddVersion(Common.GetVersion());
    }

    public virtual void CompileAndReadShims(ModFwModder modder)
    {
        var ldr = new CSharpLoader(ModContext)
            .SetAutoLoadAssemblies(true) // calls .ReadMod
            .SetMarkdownDocumentor(MarkdownDocumentor)
            .SetModder(modder);
        ldr.AssemblyContext = ModContext.PluginLoader.AssemblyLoader;
        var md = ldr.CreateMetaData();
        _ = ldr.LoadModules(md, "shims").ToArray();
    }

    public virtual void LoadModifications()
    {
        ModContext.PluginLoader.AddFromFolder(Path.Combine(ModContext.BaseDirectory, "modifications"));
    }

    public async Task PatchAsync()
    {
        Console.WriteLine($"Open Terraria API v{Common.GetVersion()}");

        this.SetupFilters();
        try
        {
            Common.AddMarkdownFormatter();

            var vanillaDllPath = await DownloadServerAsync();
            var vanillaName = Path.GetFileNameWithoutExtension(vanillaDllPath);

            Console.WriteLine("[OTAPI] Extracting embedded binaries and packing into one binary...");
            var embeddedResources = this.ExtractResources(vanillaDllPath);

            var putputsPath = Path.Combine(ModContext.BaseDirectory, "outputs");
            Directory.CreateDirectory(putputsPath);

            var terrariaServerDllDestination = Path.Combine(putputsPath, "TerrariaServer.dll");
            var otapiDllDestination = Path.Combine(ModContext.BaseDirectory, "OTAPI.dll");
            var otapiRuntimeDllDestination = Path.Combine(ModContext.BaseDirectory, "OTAPI.Runtime.dll");

            ModContext.ReferenceFiles.AddRange(new[]
            {
                "ModFramework.dll",
                "MonoMod.dll",
                "MonoMod.Utils.dll",
                "MonoMod.RuntimeDetour.dll",
                "Mono.Cecil.dll",
                "Mono.Cecil.Rocks.dll",
                "Newtonsoft.Json.dll",
                "Steamworks.NET.dll",
            });

            // expose types to be public, so private IL refs can be ref'd by compiling mods
            this.Patch("Merging and pregenerating files", vanillaDllPath, terrariaServerDllDestination, PublicEverything, (modType, mm) =>
            {
                if (mm is not null)
                {
                    if (modType == ModType.PreRead)
                    {
                        mm.AssemblyResolver.AddSearchDirectory(embeddedResources);
                    }
                    else if (modType == ModType.Read)
                    {
                        // merge in HookResult, HookEvent etc
                        mm.ReadMod(typeof(HookEvent).Assembly.Location);

                        Console.WriteLine($"[OTAPI] Changing to AnyCPU (x64 preferred)");
                        mm.SetAnyCPU();

                        CompileAndReadShims(mm);
                        MergeReLogic(mm, embeddedResources);
                        this.AddPatchMetadata(mm, mm.Module.Name, vanillaDllPath);
                    }
                }
                return EApplyResult.Continue;
            });

            ModContext.ReferenceFiles.Add(terrariaServerDllDestination);

            // load into the current app domain for patch refs
            var asm = Assembly.LoadFile(terrariaServerDllDestination);
            var cache = new Dictionary<string, Assembly>();

            Assembly? ResolvingFile(AssemblyLoadContext arg1, AssemblyName args)
            {
                if (args.Name is null) return null;
                if (cache.TryGetValue(args.Name, out Assembly? asmm))
                    return asmm;

                if (args.Name == asm.GetName().Name) //.IndexOf("TerrariaServer") > -1)
                {
                    cache[args.Name] = asm;
                    return asm;
                }

                var dll = $"{args.Name}.dll";

                // try tml first, its binaries override other builds
                var tmlPath = Environment.GetEnvironmentVariable("OTAPI_TML_PATH")?.Trim();
                var tmp = Path.Combine(String.IsNullOrWhiteSpace(tmlPath) ? "tModLoader" : tmlPath, "Libraries", args.Name, "1.0.0", dll);
                if (File.Exists(tmp))
                {
                    asmm = Assembly.Load(File.ReadAllBytes(tmp));
                    cache[args.Name] = asmm;
                    return asmm;
                }

                if (File.Exists(dll))
                {
                    asmm = Assembly.Load(File.ReadAllBytes(dll));
                    cache[args.Name] = asmm;
                    return asmm;
                }

                return null;
            }

            AssemblyLoadContext.Default.Resolving += ResolvingFile;
            try
            {
                this.Patch("Applying modifications", terrariaServerDllDestination, otapiDllDestination, false, (modType, mm) =>
                {
                    if (mm is not null)
                    {
                        if (modType == ModType.PreRead)
                        {
                            mm.AssemblyResolver.AddSearchDirectory(embeddedResources);
                        }
                        else if (modType == ModType.Read)
                        {
                            // assembly is loaded, can use it to add in constants
                            // before the plugins are invoked
                            this.AddConstants(vanillaName, mm);

                            LoadModifications();
                        }
                        else if (modType == ModType.PreWrite)
                        {
                            mm.ModContext.TargetAssemblyName = "OTAPI"; // change the target assembly since otapi is now valid for write events
                            AddVersion(mm);
                            this.AddPatchMetadata(mm);
                            mm.AddEnvMetadata();

                            // generate static hooks
                            Console.Write("[OTAPI] Generating static hooks...");
                            CreateSafeHooks(mm.Module.GetType("Terraria.Main"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Item"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.NetMessage"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Netplay"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.NPC"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.WorldGen"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Chat.ChatHelper"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.GameContent.ItemDropRules.CommonCode"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.IO.WorldFile"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Net.NetManager"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Projectile"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.RemoteClient"), mm);
                            // mm.Module.GetType("Terraria.Liquid").CreateHooks(mm); TODO add value type hooks
                            if (this is not TMLPCServerTarget)
                                CreateSafeHooks(mm.Module.GetType("Terraria.Program"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.Wiring"), mm);
                            CreateSafeHooks(mm.Module.GetType("Terraria.GameContent.PressurePlateHelper"), mm);
                            var craftingRequests = mm.Module.GetType("Terraria.GameContent.CraftingRequests");
                            if (craftingRequests is not null)
                                CreateSafeHooks(craftingRequests, mm);
                            Console.WriteLine("Done");
                        }
                        else if (modType == ModType.Write)
                        {
                            mm.ModContext = new("OTAPI.Runtime"); // done with the context. they will be triggered again by runtime hooks if not for this
                            mm.CreateRuntimeHooks(otapiRuntimeDllDestination);

                            Console.WriteLine("[OTAPI] Building NuGet package...");
                            NugetPackager.Build(mm);
                        }
                    }
                    return EApplyResult.Continue;
                });
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= ResolvingFile;
            }
        }
        finally
        {
            this.RemoveFilters();
        }

        Console.WriteLine("[OTAPI] Building markdown documentation...");
        MarkdownDocumentor.Write();

        Console.WriteLine("[OTAPI] Building artifacts...");
        this.WriteCIArtifacts(ArtifactName, MainAssemblyFileName);

        Console.WriteLine("Patching has completed.");
    }

    public virtual void AddSearchDirectories(ModFwModder modder) { }

    public virtual void SetupFilters()
    {
        ModContext.PluginLoader.OnModFileLoading += OnModFileLoading;
    }

    public virtual void RemoveFilters()
    {
        ModContext.PluginLoader.OnModFileLoading += OnModFileLoading;
    }

    public virtual bool OnModFileLoading(string filepath)
    {
        if (filepath.Contains("OTAPI.Runtime")) return true;
        // only load "server" or "both" variants
        var filename = Path.GetFileNameWithoutExtension(filepath);
        return !filename.Replace("-", ".").EndsWith(".Client", StringComparison.CurrentCultureIgnoreCase);
    }

    public string GetZipUrl()
    {
        const string CliKey = "latest";
        var cli = this.GetCliValue(CliKey);

        if (cli != "n")
        {
            if (Console.IsInputRedirected)
                return FileResolver.AquireLatestBinaryUrl();

            int attempts = 5;
            do
            {
                Console.Write("Download the latest binary? y/[N]: ");

                //var input = Console.ReadLine().ToLower();
                var input = Console.ReadKey(true).Key;
                Console.WriteLine(input);

                if (input == ConsoleKey.Y)
                    return FileResolver.AquireLatestBinaryUrl();
                else if (input == ConsoleKey.N)
                    break;
                else if (input == ConsoleKey.Enter)
                    break;
            } while (attempts-- > 0);
        }

        return FileResolver.SupportedDownloadUrl;
    }
}
