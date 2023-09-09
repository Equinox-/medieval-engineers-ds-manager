using System;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdPlayers : ApplicationCommandModule
    {
        private readonly ISubscriber<PlayersResponse> _playersSubscriber;
        private readonly IPublisher<PlayersRequest> _playersRequest;
        private readonly ISubscriber<PromotePlayerResponse> _promoteSubscriber;
        private readonly IPublisher<PromotePlayerRequest> _promoteRequest;

        public DiscordCmdPlayers(ISubscriber<PlayersResponse> playersSubscriber, IPublisher<PlayersRequest> playersRequest,
            ISubscriber<PromotePlayerResponse> promoteSubscriber, IPublisher<PromotePlayerRequest> promoteRequest)
        {
            _playersSubscriber = playersSubscriber;
            _playersRequest = playersRequest;
            _promoteSubscriber = promoteSubscriber;
            _promoteRequest = promoteRequest;
        }

        [SlashCommand("players", "Lists online players")]
        [SlashCommandPermissions(Permissions.UseApplicationCommands)]
        public async Task PlayersCommand(InteractionContext context)
        {
            await context.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
            try
            {
                var response = await _playersSubscriber.AwaitResponse(msg =>
                {
                    var response = new StringBuilder();
                    response.Append($"{msg.PlayersLength} Online Players");
                    for (var i = 0; i < msg.PlayersLength; i++)
                    {
                        response.Append("\n");
                        // ReSharper disable once PossibleInvalidOperationException
                        var player = msg.Players(i).Value;
                        if (player.FactionTag != null)
                            response.Append("[").Append(player.FactionTag).Append("] ");
                        response.Append(player.Name);
                        switch (player.Promotion)
                        {
                            case PlayerPromotionLevel.Moderator:
                                response.Append(" (Moderator)");
                                break;
                            case PlayerPromotionLevel.Admin:
                                response.Append(" (Admin)");
                                break;
                            case PlayerPromotionLevel.None:
                            default:
                                break;
                        }
                    }

                    return response.ToString();
                }, sendRequest: () =>
                {
                    using var token = _playersRequest.Publish();
                    PlayersRequest.StartPlayersRequest(token.Builder);
                    token.Send(PlayersRequest.EndPlayersRequest(token.Builder));
                });
                await context.EditResponseAsync(response);
            }
            catch (TimeoutException)
            {
                await context.EditResponseAsync("Server did not respond to players request.  Is it offline?");
            }
        }

        public enum DiscordPlayerPromotionLevel
        {
            None,
            Moderator,
            Administrator
        }

        [SlashCommand("player-promote", "Promotes or demotes a player")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task PromotePlayer(InteractionContext context,
            [Option("player", "Player steam ID")] [Autocomplete(typeof(DiscordPlayersAutocomplete))]
            string steamIdString,
            [Option("promotion", "What level to promote / demote the player to")]
            DiscordPlayerPromotionLevel promotionLevel)
        {
            var steamId = ulong.Parse(steamIdString);
            await context.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
            try
            {
                var response = await _promoteSubscriber.AwaitResponse(
                    msg =>
                        $"{(msg.Successful ? "Promoted" : "Failed to promote")} {msg.Name} ({msg.SteamId}) from {msg.OldPromotion} to {msg.RequestedPromotion}",
                    sendRequest: () =>
                    {
                        using var token = _promoteRequest.Publish();
                        token.Send(PromotePlayerRequest.CreatePromotePlayerRequest(token.Builder, steamId, promotionLevel switch
                        {
                            DiscordPlayerPromotionLevel.None => PlayerPromotionLevel.None,
                            DiscordPlayerPromotionLevel.Moderator => PlayerPromotionLevel.Moderator,
                            DiscordPlayerPromotionLevel.Administrator => PlayerPromotionLevel.Admin,
                            _ => throw new ArgumentOutOfRangeException(nameof(promotionLevel), promotionLevel, null)
                        }));
                    });
                await context.EditResponseAsync(response);
            }
            catch (TimeoutException)
            {
                await context.EditResponseAsync("Server did not respond to promotion request.  Is it offline?");
            }
        }
    }
}