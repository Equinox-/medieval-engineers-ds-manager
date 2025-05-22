using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace Meds.Watchdog.Steam
{
    public class CallbackPump
    {
        private readonly SteamClient _steamClient;

        public event Action<ICallbackMsg> CallbackReceived;

        private readonly List<Waiter> _callbackWaiters = new List<Waiter>();
        private bool _cancel;
        
        public CallbackPump(SteamClient client)
        {
            _steamClient = client;
        }

        /// <summary>
        /// Returns a task that completes when a callback matching the given filter is received.
        /// </summary>
        /// <param name="filter">The callback filter.</param>
        /// <returns>A task returning the callback object on completion.</returns>
        public async Task<ICallbackMsg> WaitForAsync(Func<ICallbackMsg, bool> filter = null, CancellationToken ct = default)
        {
            var waiter = new Waiter(filter, ct);
            lock(_callbackWaiters)
                _callbackWaiters.Add(waiter);
            return await waiter.Task;
        }
        
        /// <summary>
        /// Returns a task that completes when a callback of the specified type is received.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>A task returning the callback object on completion.</returns>
        public async Task<T> WaitForAsync<T>(CancellationToken ct = default) where T : ICallbackMsg
        {
            return (T)await WaitForAsync(x => x is T, ct);
        }

        /// <summary>
        /// Starts the callback pump.
        /// </summary>
        public void Start()
        {
            Task.Run(() =>
            {
                while (!_cancel)
                {
                    var callback = _steamClient.GetCallback();

                    if (callback == null)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    
                    _steamClient.FreeLastCallback();

                    lock (_callbackWaiters)
                    {
                        for (var i = _callbackWaiters.Count - 1; i >= 0; i--)
                        {
                            if (_callbackWaiters[i].TryComplete(callback))
                                _callbackWaiters.RemoveAt(i);
                        }   
                    }
                    
                    CallbackReceived?.Invoke(callback);
                }

                _cancel = false;
            });
        }

        /// <summary>
        /// Signals the callback pump to stop.
        /// </summary>
        public void Stop()
        {
            _cancel = true;
        }

        /// <summary>
        /// Provides an awaitable callback source.
        /// </summary>
        private class Waiter
        {
            public Task<ICallbackMsg> Task => _tcs.Task;
            
            private readonly TaskCompletionSource<ICallbackMsg> _tcs = new TaskCompletionSource<ICallbackMsg>();
            private readonly Func<ICallbackMsg, bool> _condition;
            private CancellationTokenRegistration _cancelRegistered;

            public Waiter(Func<ICallbackMsg, bool> condition, CancellationToken ct)
            {
                _condition = condition;
                _cancelRegistered = ct.Register(() => _tcs.TrySetCanceled());
            }

            public bool TryComplete(ICallbackMsg msg)
            {
                if (_condition.Invoke(msg))
                {
                    _cancelRegistered.Dispose();
                    _tcs.SetResult(msg);
                    return true;
                }

                return false;
            }
        }
    }
}