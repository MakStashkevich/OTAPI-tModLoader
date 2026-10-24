using System.Linq;
using ModFramework;
using Mono.Cecil.Cil;
using MonoMod;

#if tModLoader_V1_4
[Modification(ModType.PostPatch, "Skipping client DPI setup for dedicated server")]
[MonoMod.MonoModIgnore]
void SkipClientDpiSetup(MonoModder modder)
{
    var method = modder.Module.GetType("Terraria.Program")?.Methods
        .SingleOrDefault(x => x.Name == "AttemptSupportHighDPI" && x.Parameters.Count == 1);
    if (method is null) return;

    method.Body.Instructions.Clear();
    method.Body.Variables.Clear();
    method.Body.ExceptionHandlers.Clear();
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
}
#endif
