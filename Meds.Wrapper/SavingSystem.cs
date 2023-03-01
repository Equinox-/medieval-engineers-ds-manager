using System;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox.Game.World;

namespace Meds.Wrapper
{
    public class SavingSystem : IHostedService
    {
        private readonly ILogger<SavingSystem> _log;
        private readonly ISubscriber<SaveRequest> _subscriber;
        private readonly IPublisher<SaveResponse> _publisher;

        public SavingSystem(ISubscriber<SaveRequest> subscriber, IPublisher<SaveResponse> publisher, ILogger<SavingSystem> log)
        {
            _subscriber = subscriber;
            _publisher = publisher;
            _log = log;
        }

        private async Task HandleRequest(SaveRequest obj)
        {
            void Respond(SaveResult result)
            {
                using var token = _publisher.Publish();
                token.Send(SaveResponse.CreateSaveResponse(token.Builder, result));
            }

            while (MyAsyncSaving.InProgress)
                await Task.Delay(TimeSpan.FromSeconds(1));
            var completion = new TaskCompletionSource<bool>();
            MyAsyncSaving.Start(completion.SetResult);
            await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMinutes(10)));
            if (!completion.Task.IsCompleted)
            {
                Respond(SaveResult.TimedOut);
                return;
            }

            var saveResult = completion.Task.Result;
            if (!saveResult)
            {
                Respond(SaveResult.Failed);
                return;
            }

            if (!string.IsNullOrEmpty(obj.Backup))
            {
                
            }
        }

        private void HandleRequestBackground(SaveRequest obj)
        {
            var task = HandleRequest(obj);
            task.ContinueWith(result =>
            {
                if (result.IsFaulted)
                    _log.LogWarning(result.Exception, "Failed to handle save message");
            });
        }

        private IDisposable _requestSubscription;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _requestSubscription = _subscriber.Subscribe(HandleRequestBackground);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _requestSubscription.Dispose();
            return Task.CompletedTask;
        }
    }
}