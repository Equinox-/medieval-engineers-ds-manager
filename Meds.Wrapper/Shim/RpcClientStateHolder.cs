using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Players;
using VRage.Network;

namespace Meds.Wrapper.Shim
{
    public static class RpcClientStateHolder
    {
        private static readonly ConcurrentDictionary<EndpointId, MyClientStateBase> States = new ConcurrentDictionary<EndpointId, MyClientStateBase>();

        public static bool TryGetState(EndpointId endpoint, out MyClientStateBase state) => States.TryGetValue(endpoint, out state);

        [HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.OnClientConnected), typeof(EndpointId), typeof(MyClientStateBase))]
        [AlwaysPatch]
        public static class HandleOnClientConnected
        {
            public static void Postfix(EndpointId endpointId, MyClientStateBase clientState) => States[endpointId] = clientState;
        }

        [HarmonyPatch(typeof(MyReplicationServer), nameof(MyReplicationServer.OnClientLeft), typeof(EndpointId))]
        [AlwaysPatch]
        public static class HandleOnClientLeft
        {
            public static void Postfix(EndpointId endpointId) => States.Remove(endpointId);
        }
    }
}