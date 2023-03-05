using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Shared
{
    public sealed class HostBuilder
    {
        private static readonly IServiceProviderFactory<IServiceCollection> ServiceProviderFactory = new DefaultServiceProviderFactory();

        private readonly IServiceCollection _services = new ServiceCollection();

        public HostBuilder ConfigureServices(Action<IServiceCollection> config)
        {
            config(_services);
            return this;
        }

        public IHost Build(string logDir = null)
        {
            _services.AddSingleton<IHostApplicationLifetime, ApplicationLifetime>();
            _services.AddSingleton<IHost, HostImpl>();

            _services.AddSingleton<ILoggerFactory>(new ExtraContextLoggerFactory(LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddZLoggerConsole(opts =>
                {
                    opts.PrefixFormatter = (writer, info) => ZString.Utf8Format(writer, "{0} {1}> ", info.LogLevel, info.CategoryName);
                });
                if (logDir == null) return;
                builder.AddZLoggerRollingFile(
                    (dt, x) => $"{logDir}/{dt.ToLocalTime():yyyy-MM-dd}_{x:000}.log",
                    x => x.ToLocalTime().Date, 1024,
                    opts =>
                    {
                        opts.EnableStructuredLogging = true;
                        opts.FlushRate = TimeSpan.FromSeconds(15);
                    });
            })));
            _services.Add(new ServiceDescriptor(typeof(ILogger<>), typeof (Logger<>), ServiceLifetime.Singleton));

            var services = ServiceProviderFactory.CreateServiceProvider(ServiceProviderFactory.CreateBuilder(_services));
            return services.GetRequiredService<IHost>();
        }

        private sealed class HostImpl : IHost
        {
            private readonly ILogger<IHost> _logger;
            private readonly ApplicationLifetime _applicationLifetime;
            private IEnumerable<IHostedService> _hostedServices;

            public HostImpl(IServiceProvider services,
                IHostApplicationLifetime applicationLifetime,
                ILogger<IHost> logger)
            {
                Services = services;
                _applicationLifetime = (ApplicationLifetime)applicationLifetime;
                _logger = logger;
            }

            public IServiceProvider Services { get; }

            public async Task StartAsync(CancellationToken cancellationToken = default)
            {
                _logger.ZLogInformation("Starting");

                using var combinedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    _applicationLifetime.ApplicationStopping);
                var combinedCancellationToken = combinedCancellationTokenSource.Token;

                combinedCancellationToken.ThrowIfCancellationRequested();
                _hostedServices = Services.GetRequiredService<IEnumerable<IHostedService>>();

                foreach (var hostedService in _hostedServices!)
                {
                    await hostedService.StartAsync(combinedCancellationToken).ConfigureAwait(false);

                    if (hostedService is BackgroundService backgroundService)
                        _ = StopOnBackgroundTaskFailure(backgroundService);
                }

                _applicationLifetime.NotifyStarted();

                _logger.ZLogInformation("Started");
            }

            private async Task StopOnBackgroundTaskFailure(BackgroundService backgroundService)
            {
                var backgroundTask = backgroundService.ExecuteTask;
                if (backgroundTask == null)
                    return;

                try
                {
                    await backgroundTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (backgroundTask.IsCanceled && ex is OperationCanceledException)
                        return;

                    _logger.ZLogError(ex, "Background service faulted, stopping host");
                    _applicationLifetime.StopApplication();
                }
            }

            public async Task StopAsync(CancellationToken cancellationToken = default)
            {
                _logger.ZLogInformation("Stopping");

                _applicationLifetime.StopApplication();

                var exceptions = new List<Exception>();
                if (_hostedServices != null)
                {
                    foreach (var hostedService in _hostedServices.Reverse())
                    {
                        try
                        {
                            await hostedService.StopAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                }

                _applicationLifetime.NotifyStopped();

                if (exceptions.Count > 0)
                {
                    var ex = new AggregateException("One or more hosted services failed to stop.", exceptions);
                    _logger.ZLogError(ex, "Stopped with errors");
                    throw ex;
                }

                _logger.ZLogInformation("Stopped");
            }

            public void Dispose()
            {
                if (Services is IDisposable disposable)
                    disposable.Dispose();
            }
        }
    }

    internal class ApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedSource = new CancellationTokenSource();
        private readonly CancellationTokenSource _stoppingSource = new CancellationTokenSource();
        private readonly CancellationTokenSource _stoppedSource = new CancellationTokenSource();
        private readonly ILogger<ApplicationLifetime> _logger;

        public ApplicationLifetime (ILogger<ApplicationLifetime> logger)
        {
            _logger = logger;

            // ReSharper disable ConvertToLocalFunction
            EventHandler processExitHandler = (o, e) => StopApplication();
            ConsoleCancelEventHandler consoleCancelEventHandler = (o, e) => StopApplication();
            // ReSharper restore ConvertToLocalFunction

            AppDomain.CurrentDomain.ProcessExit += processExitHandler;
            Console.CancelKeyPress += consoleCancelEventHandler;

            ApplicationStopped.Register(() =>
            {
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                Console.CancelKeyPress -= consoleCancelEventHandler;
            });
        }

        /// <summary>
        /// Triggered when the application host has fully started and is about to wait
        /// for a graceful shutdown.
        /// </summary>
        public CancellationToken ApplicationStarted => _startedSource.Token;

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// Request may still be in flight. Shutdown will block until this event completes.
        /// </summary>
        public CancellationToken ApplicationStopping => _stoppingSource.Token;

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// All requests should be complete at this point. Shutdown will block
        /// until this event completes.
        /// </summary>
        public CancellationToken ApplicationStopped => _stoppedSource.Token;

        /// <summary>
        /// Signals the ApplicationStopping event and blocks until it completes.
        /// </summary>
        public void StopApplication()
        {
            // Lock on CTS to synchronize multiple calls to StopApplication. This guarantees that the first call
            // to StopApplication and its callbacks run to completion before subsequent calls to StopApplication,
            // which will no-op since the first call already requested cancellation, get a chance to execute.
            lock (_stoppingSource)
            {
                try
                {
                    if (!_stoppingSource.IsCancellationRequested)
                        _logger.ZLogInformation("Shutting down here:\n{0}", new StackTrace());
                    ExecuteHandlers(_stoppingSource);
                }
                catch (Exception ex)
                {
                    _logger.ZLogError(ex, "An error occurred stopping the application");
                }
            }
        }

        /// <summary>
        /// Signals the ApplicationStarted event and blocks until it completes.
        /// </summary>
        public void NotifyStarted()
        {
            try
            {
                ExecuteHandlers(_startedSource);
            }
            catch (Exception ex)
            {
                _logger.ZLogError(ex, "An error occurred starting the application");
            }
        }

        /// <summary>
        /// Signals the ApplicationStopped event and blocks until it completes.
        /// </summary>
        public void NotifyStopped()
        {
            try
            {
                ExecuteHandlers(_stoppedSource);
            }
            catch (Exception ex)
            {
                _logger.ZLogError(ex, "An error occurred stopping the application");
            }
        }

        private void ExecuteHandlers(CancellationTokenSource cancel)
        {
            // Noop if this is already cancelled
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            // Run the cancellation token callbacks
            cancel.Cancel(throwOnFirstException: false);
        }
    }
}