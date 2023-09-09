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

namespace Meds.Wrapper
{
    public class PlayerSystem : IHostedService
    {
        private readonly IPublisher<PlayersResponse> _listPlayersPublisher;
        private readonly ISubscriber<PlayersRequest> _listPlayersSubscriber;
        private readonly IPublisher<PlayerJoinedLeft> _publishJoinedLeft;
        private readonly ISubscriber<PromotePlayerRequest> _promotePlayerSubscriber;
        private readonly IPublisher<PromotePlayerResponse> _promotePlayerPublisher;

        public PlayerSystem(ISubscriber<PlayersRequest> listPlayersSubscriber,
            IPublisher<PlayersResponse> listPlayersPublisher,
            IPublisher<PlayerJoinedLeft> publishJoinedLeft,
            ISubscriber<PromotePlayerRequest> promotePlayerSubscriber,
            IPublisher<PromotePlayerResponse> promotePlayerPublisher)
        {
            _listPlayersSubscriber = listPlayersSubscriber;
            _listPlayersPublisher = listPlayersPublisher;
            _publishJoinedLeft = publishJoinedLeft;
            _promotePlayerSubscriber = promotePlayerSubscriber;
            _promotePlayerPublisher = promotePlayerPublisher;
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
            }
            else if (!_playerDataCache.TryGetValue(id, out playerData))
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
            using var token = _listPlayersPublisher.Publish();
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

        private void DoPromote(ulong steamId, PlayerPromotionLevel level)
        {
            var mapped = level switch
            {
                PlayerPromotionLevel.None => PromotionLevel.None,
                PlayerPromotionLevel.Moderator => PromotionLevel.Moderator,
                PlayerPromotionLevel.Admin => PromotionLevel.Admin,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
            var originalRank = MyPlayerAdministrationSystem.GetPromotionLevel(steamId) switch
            {
                PromotionLevel.None => PlayerPromotionLevel.None,
                PromotionLevel.Moderator => PlayerPromotionLevel.Moderator,
                PromotionLevel.Admin => PlayerPromotionLevel.Admin,
                _ => throw new ArgumentOutOfRangeException()
            };
            MyPlayerAdministrationSystem.Static.SetPlayerRank(steamId, mapped);
            var identity = MyPlayers.Static.GetPlayer(new MyPlayer.PlayerId(steamId));
            var actual = MyPlayerAdministrationSystem.GetPromotionLevel(steamId);
            using var tok = _promotePlayerPublisher.Publish();
            tok.Send(PromotePlayerResponse.CreatePromotePlayerResponse(tok.Builder,
                steamId,
                tok.Builder.CreateString(identity?.Identity?.DisplayName ?? "Unknown"),
                originalRank,
                level,
                actual == mapped));
        }

        private IDisposable _listPlayersSubscription;
        private IDisposable _promotePlayerSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listPlayersSubscription = _listPlayersSubscriber.Subscribe(msg => MySandboxGame.Static?.Invoke(Report));
            _promotePlayerSubscription = _promotePlayerSubscriber.Subscribe(msg =>
            {
                var id = msg.SteamId;
                var level = msg.Promotion;
                MySandboxGame.Static?.Invoke(() => DoPromote(id, level));
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listPlayersSubscription.Dispose();
            _promotePlayerSubscription.Dispose();
            return Task.CompletedTask;
        }
    }
}