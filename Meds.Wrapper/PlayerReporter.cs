using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.FlatBuffers;
using Medieval.GameSystems.Factions;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using Sandbox.Game.SessionComponents;
using VRage.Library.Collections;

namespace Meds.Standalone
{
    public class PlayerReporter : IHostedService
    {
        private readonly IPublisher<PlayersResponse> _publisher;
        private readonly ISubscriber<PlayersRequest> _subscriber;
        private readonly IPublisher<PlayerJoinedLeft> _publishJoinedLeft;

        public PlayerReporter(ISubscriber<PlayersRequest> subscriber,
            IPublisher<PlayersResponse> publisher,
            IPublisher<PlayerJoinedLeft> publishJoinedLeft)
        {
            _subscriber = subscriber;
            _publisher = publisher;
            _publishJoinedLeft = publishJoinedLeft;
        }

        private readonly struct PlayerData
        {
            public readonly ulong SteamId;
            public readonly string Name;
            public readonly PromotionLevel Promotion;
            public readonly MyFaction Faction;
            public readonly MyFaction.Rank Rank;

            public PlayerData(MyPlayer player, MyIdentity id)
            {
                SteamId = player.Id.SteamId;
                Name = id.DisplayName;
                Promotion = MyPlayerAdministrationSystem.GetPromotionLevel(player.Id.SteamId);
                Faction = MyFactionManager.GetPlayerFaction(id.Id);
                Rank = Faction?.GetMemberRank(id.Id);
            }

            public PlayerPromotionLevel FlatPromotion()
            {
                switch (Promotion)
                {
                    case PromotionLevel.None:
                    default:
                        return PlayerPromotionLevel.None;
                    case PromotionLevel.Moderator:
                        return PlayerPromotionLevel.Moderator;
                    case PromotionLevel.Admin:
                        return PlayerPromotionLevel.Admin;
                }
            }

            public Offset<PlayerResponse> EncodeTo(FlatBufferBuilder builder) => PlayerResponse.CreatePlayerResponse(
                builder,
                steam_id: SteamId,
                nameOffset: builder.CreateString(Name),
                promotion: FlatPromotion(),
                faction_tagOffset: builder.CreateSharedString(Faction?.FactionTag),
                faction_rankOffset: builder.CreateSharedString(Rank?.Title)
            );
        }

        private readonly Dictionary<ulong, PlayerData> _playerDataCache = new Dictionary<ulong, PlayerData>();
        public void HandlePlayerJoinedLeft(bool joined, ulong id)
        {
            var player = MyPlayers.Static.GetPlayer(new MyPlayer.PlayerId(id));
            var identity = player?.Identity;
            PlayerData playerData;
            if (identity != null)
            {
                playerData = new PlayerData(player, identity);
                _playerDataCache[id] = playerData;
            } else if (!_playerDataCache.TryGetValue(id, out playerData))
                return;
            using var token = _publishJoinedLeft.Publish();
            var playerDataBuf = playerData.EncodeTo(token.Builder);
            token.Send(PlayerJoinedLeft.CreatePlayerJoinedLeft(token.Builder,
                joined, (Sync.Clients?.Count ?? 1) - 1,
                playerDataBuf));
        }

        private static void GatherPlayerData(List<PlayerData> output)
        {
            var players = MyPlayers.Static.GetAllPlayers();
            foreach (var player in players.Values)
            {
                var id = player.Identity;
                if (id == null)
                    continue;
                output.Add(new PlayerData(player, id));
            }

            output.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        private void Report()
        {
            using var token = _publisher.Publish();
            var builder = token.Builder;
            using (PoolManager.Get(out List<Offset<PlayerResponse>> playerOffsets))
            using (PoolManager.Get(out List<PlayerData> players))
            {
                GatherPlayerData(players);
                foreach (var player in players)
                {
                    playerOffsets.Add(player.EncodeTo(builder));
                }

                PlayersResponse.StartPlayersVector(builder, playerOffsets.Count);
                foreach (var offset in playerOffsets)
                    builder.AddOffset(offset.Value);
                token.Send(PlayersResponse.CreatePlayersResponse(builder, builder.EndVector()));
            }
        }

        private IDisposable _subscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _subscription = _subscriber.Subscribe(msg => MySandboxGame.Static?.Invoke(Report));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _subscription.Dispose();
            return Task.CompletedTask;
        }
    }
}