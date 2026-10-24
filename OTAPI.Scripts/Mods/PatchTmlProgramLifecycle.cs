using System.Linq;
using ModFramework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;

#if tModLoader_1441_OrAbove
[Modification(ModType.PostPatch, "Inlining OTAPI lifecycle in modern tModLoader")]
[MonoMod.MonoModIgnore]
void PatchTmlProgramLifecycle(MonoModder modder)
{
    var program = modder.Module.GetType("Terraria.Program");
    var launch = program.Methods.Single(x => x.Name == "LaunchGame_" && x.Parameters.Count == 1);
    var launchOtapi = program.Methods.Single(x => x.Name == "LaunchOTAPI" && x.Parameters.Count == 0);
    var shutdownOtapi = program.Methods.Single(x => x.Name == "ShutdownOTAPI" && x.Parameters.Count == 0);
    var processor = launch.Body.GetILProcessor();

    processor.InsertBefore(launch.Body.Instructions[0], Instruction.Create(OpCodes.Call, launchOtapi));
    foreach (var ret in launch.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToArray())
        processor.InsertBefore(ret, Instruction.Create(OpCodes.Call, shutdownOtapi));
}
#endif
