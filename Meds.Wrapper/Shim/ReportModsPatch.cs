using HarmonyLib;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Engine.Networking;
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyWorkshop), nameof(MyWorkshop.ResolveAndDownloadModSetup))]
    [AlwaysPatch]
    public static class ReportModsPatch
    {
        public static void Postfix(MyWorkshop.ResultData __result)
        {
            var publisher = Entrypoint.Instance.Services.GetRequiredService<IPublisher<ReportModsMessage>>();
            using var tok = publisher.Publish();
            ReportModsMessage.StartModsVector(tok.Builder, __result.Mods.Count);
            foreach (var mod in __result.Mods)
                tok.Builder.AddUlong(mod.Id);
            var modsOffset = tok.Builder.EndVector();
            tok.Send(ReportModsMessage.CreateReportModsMessage(tok.Builder, modsOffset));
        }
    }
}