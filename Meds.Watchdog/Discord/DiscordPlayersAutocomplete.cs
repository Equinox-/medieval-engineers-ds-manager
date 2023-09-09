using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Meds.Watchdog.Discord
{
    public class DiscordPlayersAutocomplete : IAutocompleteProvider
    {
        public Task<IEnumerable<DiscordAutoCompleteChoice>> Provider(AutocompleteContext ctx)
        {
            var publisher = ctx.Services.GetRequiredService<IPublisher<PlayersRequest>>();
            var subscriber = ctx.Services.GetRequiredService<ISubscriber<PlayersResponse>>();
            return subscriber.AwaitResponse(response =>
            {
                var players = new List<DiscordAutoCompleteChoice>();
                for (var i = 0; i < response.PlayersLength; i++)
                {
                    var player = response.Players(i);
                    if (!player.HasValue) continue;
                    var id = player.Value.SteamId;
                    var name = player.Value.Name;
                    var level = player.Value.Promotion;
                    players.Add(new DiscordAutoCompleteChoice($"{name} ({id}) {level}", id.ToString()));
                }
                return (IEnumerable<DiscordAutoCompleteChoice>) players;
            }, TimeSpan.FromSeconds(5), () =>
            {
                var token = publisher.Publish();
                PlayersRequest.StartPlayersRequest(token.Builder);
                token.Send(PlayersRequest.EndPlayersRequest(token.Builder));
            });
        }
    }
}