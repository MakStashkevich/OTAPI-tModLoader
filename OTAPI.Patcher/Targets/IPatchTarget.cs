/*
Copyright (C) 2020 DeathCradle

This file is part of Open Terraria API v3 (OTAPI)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

using ModFramework;
using ModFramework.Relinker;
using Mono.Cecil;
using OTAPI.Patcher.Resolvers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static ModFramework.ModContext;

namespace OTAPI.Patcher.Targets;

[MonoMod.MonoModIgnore]
public interface IPatchTarget
{
    string DisplayText { get; }
    string InstallDestination { get; }
    Task PatchAsync();
}

[MonoMod.MonoModIgnore]
public interface IModPatchTarget : IPatchTarget
{
    ModContext ModContext { get; }
    string GetEmbeddedResourcesDirectory(string fileinput);
    void AddSearchDirectories(ModFwModder modder);
    MarkdownDocumentor MarkdownDocumentor { get; }
    bool GenerateSymbols { get; }
}

[MonoMod.MonoModIgnore]
public interface IServerPatchTarget : IModPatchTarget
{
    IFileResolver FileResolver { get; }
}

[MonoMod.MonoModIgnore]
public interface IClientPatchTarget : IModPatchTarget
{
}

[MonoMod.MonoModIgnore]
public static partial class PatchTargetExtensions
{
    private sealed class TargetFrameworkResolver : IFrameworkResolver
    {
        private readonly IFrameworkResolver fallback = new DefaultFrameworkResolver();

        public string FindFramework()
        {
            var referenceDirectory = Environment.GetEnvironmentVariable("OTAPI_FRAMEWORK_REF_DIR");
            if (String.IsNullOrWhiteSpace(referenceDirectory))
                return fallback.FindFramework();

            if (!Directory.Exists(referenceDirectory))
                throw new DirectoryNotFoundException($"OTAPI_FRAMEWORK_REF_DIR does not exist: {referenceDirectory}");

            return referenceDirectory;
        }
    }

    private static string TargetFramework =>
        Environment.GetEnvironmentVariable("OTAPI_TARGET_FRAMEWORK")?.Trim() is { Length: > 0 } target
            ? target
            : "net9.0";

    private static CoreLibRelinker AddCoreLibRelinker(ModFwModder modder)
    {
        var relinker = (CoreLibRelinker)Activator.CreateInstance(
            typeof(CoreLibRelinker),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { modder },
            culture: null)!;
        relinker.FrameworkResolver = new TargetFrameworkResolver();
        modder.AddTask(relinker);
        return relinker;
    }

    private static void PatchTargetFrameworkAttribute(string path)
    {
        var target = TargetFramework;
        if (!target.StartsWith("net", StringComparison.Ordinal) || !target.EndsWith(".0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported OTAPI_TARGET_FRAMEWORK: {target}");

        var version = target[3..^2];
        var targetMajor = Int32.Parse(version, System.Globalization.CultureInfo.InvariantCulture);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        var tmlPath = Environment.GetEnvironmentVariable("OTAPI_TML_PATH")?.Trim();
        if (!String.IsNullOrWhiteSpace(tmlPath) && Directory.Exists(Path.Combine(tmlPath, "Libraries")))
        {
            foreach (var directory in Directory.GetDirectories(Path.Combine(tmlPath, "Libraries"), "*", SearchOption.AllDirectories))
                resolver.AddSearchDirectory(directory);
        }
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            AssemblyResolver = resolver,
        });
        var attribute = assembly.CustomAttributes.FirstOrDefault(x =>
            x.AttributeType.FullName == typeof(System.Runtime.Versioning.TargetFrameworkAttribute).FullName);
        if (attribute is null)
            throw new InvalidOperationException($"TargetFrameworkAttribute missing from {path}");

        attribute.ConstructorArguments[0] = new(attribute.ConstructorArguments[0].Type, $".NETCoreApp,Version=v{version}.0");

        // The hook generator can leave framework references from the patcher's runtime in
        // OTAPI.Runtime. A .NET 8 tModLoader cannot resolve System.Runtime 9, even for
        // common types such as Delegate and MethodBase, so bind those references to the
        // selected target framework's reference pack.
        foreach (var reference in assembly.MainModule.AssemblyReferences)
        {
            if ((reference.Name == "System.Runtime" || reference.Name.StartsWith("System.", StringComparison.Ordinal))
                && reference.Version.Major > targetMajor)
                reference.Version = new Version(targetMajor, 0, 0, 0);
        }

        // Prefer the MonoMod runtime actually shipped with the selected tModLoader copy
        // when HookGen emitted a reference to a newer NuGet build.
        if (!String.IsNullOrWhiteSpace(tmlPath))
        {
            var fnaPath = Directory.GetFiles(Path.Combine(tmlPath, "Libraries"), "FNA.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
            Version? tmlFnaVersion = null;
            if (fnaPath is not null)
            {
                using var fnaAssembly = AssemblyDefinition.ReadAssembly(fnaPath, new ReaderParameters { InMemory = true });
                tmlFnaVersion = fnaAssembly.Name.Version;
            }

            foreach (var reference in assembly.MainModule.AssemblyReferences.Where(x =>
                         x.Name is "MonoMod.RuntimeDetour" or "MonoMod.Utils" && x.Version.Major >= 25))
            {
                var matchingFile = Directory.GetFiles(Path.Combine(tmlPath, "Libraries"), $"{reference.Name}.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (matchingFile is null) continue;
                using var matchingAssembly = AssemblyDefinition.ReadAssembly(matchingFile, new ReaderParameters { InMemory = true });
                reference.Version = matchingAssembly.Name.Version;
            }

            // HookGen can emit references against the FNA version used by the patcher
            // instead of the version bundled with tModLoader. Multiple FNA versions in
            // one output make .NET 8 fail while JIT-compiling otherwise valid tML methods.
            if (tmlFnaVersion is not null)
            {
                foreach (var reference in assembly.MainModule.AssemblyReferences.Where(x => x.Name == "FNA"))
                    reference.Version = tmlFnaVersion;
            }
        }

        assembly.Write(path);
    }

    public static string ExtractResources(this IModPatchTarget target, string fileinput)
    {
        var dir = target.GetEmbeddedResourcesDirectory(fileinput);
        var extractor = new ResourceExtractor();
        var embeddedResourcesDir = extractor.Extract(fileinput, dir);
        return embeddedResourcesDir;
    }

    public static void AddPatchMetadata(this IPatchTarget target, ModFwModder modder,
        string? initialModuleName = null,
        string? inputName = null)
    {
        modder.AddMetadata("OTAPI.Target", target.DisplayText);
        if (initialModuleName != null) modder.AddMetadata("OTAPI.ModuleName", initialModuleName);
        if (inputName != null) modder.AddMetadata("OTAPI.Input", inputName);
    }

    public static void WriteCIArtifacts(this IPatchTarget target, string outputFolder, string mainAssemblyFileName = "OTAPI.dll")
    {
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);

        File.Copy(Path.Combine(AppContext.BaseDirectory, "../../../../COPYING.txt"), Path.Combine(outputFolder, "COPYING.txt"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.dll"), Path.Combine(outputFolder, mainAssemblyFileName));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "OTAPI.Runtime.dll"), Path.Combine(outputFolder, "OTAPI.Runtime.dll"));
    }

    public static void AddEnvMetadata(this ModFwModder modder)
    {
        var commitSha = Common.GetGitCommitSha();
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER")?.Trim();

        if (!String.IsNullOrWhiteSpace(commitSha))
            modder.AddMetadata("GitHub.Commit", commitSha);

        if (!String.IsNullOrWhiteSpace(run))
            modder.AddMetadata("GitHub.Action.RunNo", run);
    }

    public static void CreateRuntimeHooks(this ModFwModder modder, string output)
    {
        modder.Log("[OTAPI] Generating OTAPI.Runtime.dll");
        var gen = new MonoMod.RuntimeDetour.HookGen.HookGenerator(modder, "OTAPI.Runtime.dll");
        using var srm = new MemoryStream();
        using (ModuleDefinition mOut = gen.OutputModule)
        {
            gen.Generate();

            mOut.Write(srm);
        }

        srm.Position = 0;
        var fileName = Path.GetFileName(output);
        using var mm = new ModFwModder(new("OTAPI.Runtime"))
        {
            Input = srm,
            OutputPath = output,
            MissingDependencyThrow = false,
            //LogVerboseEnabled = true,
            // PublicEverything = true, // this is done in setup

            GACPaths = new string[] { } // avoid MonoMod looking up the GAC, which causes an exception on .netcore
        };
        mm.Log($"[OTAPI] Processing corelibs to be net9: {fileName}");

        mm.Read();

        AddCoreLibRelinker(mm);

        mm.MapDependencies();
        mm.AutoPatch();

        mm.Write();
        PatchTargetFrameworkAttribute(output);
    }

    public static void AddConstants(this IModPatchTarget target, string inputName, ModFwModder modder)
    {
        var version = modder.Module.Assembly.Name.Version;
        var const_major = $"{inputName}_V{version.Major}_{version.Minor}";
        var const_fullname = $"{inputName}_{version.ToString().Replace(".", "_")}";
        var const_senddatapatch = $"{inputName}_SendDataNumber{(version == new Version("1.4.3.0") ? "8" : "7")}";
        var const_entitysource = $"{inputName}_EntitySources{(version >= new Version("1.4.3.3") ? "Active" : "Inactive")}";

        List<string> constants = new()
        {
            inputName,
            const_major,
            const_fullname,
            const_senddatapatch,
            const_entitysource,
        };

        if (version >= new Version("1.4.4.1"))
            constants.Add($"{inputName}_1441_OrAbove");
        if (version >= new Version("1.4.4.2"))
            constants.Add($"{inputName}_1442_OrAbove");
        if (version >= new Version("1.4.4.8"))
            constants.Add($"{inputName}_1448_OrAbove");
        if (version >= new Version("1.4.5"))
            constants.Add($"{inputName}_1450_OrAbove");
        if (version >= new Version("1.4.5.5"))
            constants.Add($"{inputName}_1455_OrAbove");
        if (version >= new Version("1.4.5.7"))
            constants.Add($"{inputName}_1457_OrAbove");
        if (version >= new Version("1.4.5.8"))
            constants.Add($"{inputName}_1458_OrAbove");

        target.ModContext.ReferenceConstants.AddRange(constants.Select(x => $"#define {x}"));

        File.WriteAllText("AutoGenerated.target", @$"<!-- DO NOT EDIT THIS FILE! It was auto generated by the setup project  -->
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <DefineConstants>{String.Join(';', constants)}</DefineConstants>
  </PropertyGroup>
</Project>");
    }

    public static string Patch(this IModPatchTarget target, string status, string input, string output, bool publicEverything,
        Func<ModType, ModFwModder?, EApplyResult> onApplying,
        Action<ModFwModder, string>? print = null
    )
    {
        if (print is null) print = (_, str) => Console.WriteLine(str);

        EApplyResult OnApplying(ModType modType, ModFwModder? modder)
        {
            return onApplying(modType, modder);
        };

        target.ModContext.OnApply += OnApplying;
        try
        {
            using ModFwModder mm = new(target.ModContext)
            {
                InputPath = input,
                OutputPath = output,
                MissingDependencyThrow = false,
                PublicEverything = publicEverything,

                LogVerboseEnabled = true,

                GACPaths = new string[] { }, // avoid MonoMod looking up the GAC, which causes an exception on .netcore

                MarkdownDocumentor = target.MarkdownDocumentor,

                //EnableWriteEvents = writeEvents,
            };

            target.AddSearchDirectories(mm);
            AddCoreLibRelinker(mm);

            mm.Read();

            print(mm, $"[OTAPI] Mapping dependencies: {status}");
            mm.MapDependencies();

            print(mm, $"[OTAPI] Patching: {status}");
            mm.AutoPatch();

            print(mm, $"[OTAPI] Writing: {status}, Path={new Uri(Environment.CurrentDirectory).MakeRelativeUri(new(mm.OutputPath))}");

            if (!target.GenerateSymbols)
            {
                mm.WriterParameters.SymbolWriterProvider = null;
                mm.WriterParameters.WriteSymbols = false;
            }

            mm.Write();
            PatchTargetFrameworkAttribute(output);

            return mm.OutputPath;
        }
        finally
        {
            target.ModContext.OnApply -= OnApplying;
        }
    }
}
