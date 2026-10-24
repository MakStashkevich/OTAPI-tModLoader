using System.Linq;
using ModFramework;
using Mono.Cecil.Cil;
using MonoMod;

#if tModLoader_1441_OrAbove
[Modification(ModType.PostPatch, "Disabling tModLoader LAN UDP discovery")]
[MonoMod.MonoModIgnore]
void DisableTmlLanBroadcast(MonoModder modder)
{
    // tModLoader's broadcast thread advertises LAN servers over UDP/8888. In a
    // reverse-tunnel setup this is not used, and on hosts without a broadcast
    // route the send produces a noisy SocketException on a background thread.
    // StopBroadCasting already handles the no-thread case.
    var netplay = modder.Module.GetType("Terraria.Netplay");
    var broadcastThread = netplay?.Methods
        .SingleOrDefault(method => method.Name == "BroadcastThread" && method.Parameters.Count == 0);
    if (broadcastThread?.HasBody != true)
        return;

    broadcastThread.Body.Instructions.Clear();
    broadcastThread.Body.Variables.Clear();
    broadcastThread.Body.ExceptionHandlers.Clear();
    broadcastThread.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
}
#endif
