using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.BayeuxClient;

namespace Genesys.Bayeux.Client
{
    public interface IBayeuxClientContext
    {
        Task OpenAsync(CancellationToken cancellationToken);
        Task<JObject> RequestAsync(object request, CancellationToken cancellationToken);
        Task<JObject> RequestManyAsync(IEnumerable<object> requests, CancellationToken cancellationToken);
        Task SetConnectionStateAsync(ConnectionState newState, CancellationToken cancellationToken);
        Task SetConnectionAsync(BayeuxConnection newConnection, CancellationToken cancellationToken);
    }

    class ConnectLoop : IDisposable
    {
        private static readonly ILog log = BayeuxClient.log;

        private readonly string connectionType;
        private readonly ReconnectDelays reconnectDelays;
        private readonly IBayeuxClientContext context;

        private readonly CancellationTokenSource pollCancel = new CancellationTokenSource();

        private BayeuxConnection currentConnection;
        private BayeuxAdvice lastAdvice = new BayeuxAdvice();
        private bool transportFailed = false;
        private bool transportClosed = false;
        private bool startInBackground = false;
        private readonly object isStartedLock = new object();
        private bool isStarted = false;

        public ConnectLoop(
            string connectionType,
            IEnumerable<TimeSpan> reconnectDelays,
            IBayeuxClientContext context)
        {
            this.connectionType = connectionType;
            this.reconnectDelays = new ReconnectDelays(reconnectDelays);
            this.context = context;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                lock (isStartedLock)
                {
                    if (isStarted)
                        throw new InvalidOperationException("Already started.");
                    isStarted = true;
                }

                await context.OpenAsync(cancellationToken);
                await HandshakeAsync(cancellationToken);

                // A way to test the re-handshake with a real server is to put some delay here, between the first handshake response,
                // and the first try to connect. That will cause an "Invalid client id" response, with an advice of reconnect=handshake.
                // This can also be tested with a fake server in unit tests.

                await StartLoopPollingAsync(cancellationToken);
            }
            catch
            {
                lock (isStartedLock)
                {
                    isStarted = false;
                }
                throw;
            }
        }

        public async Task StartInBackgroundAsync(CancellationToken cancellationToken)
        {
            try
            {
                lock (isStartedLock)
                {
                    if (isStarted)
                        throw new InvalidOperationException("Already started.");
                    isStarted = true;
                }

                startInBackground = true;

                await StartLoopPollingAsync(cancellationToken);
            }
            catch
            {
                lock (isStartedLock)
                {
                    isStarted = false;
                }
                throw;
            }
        }

        async Task StartLoopPollingAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!pollCancel.IsCancellationRequested)
                    await PollAsync(cancellationToken);

                await context.SetConnectionStateAsync(ConnectionState.Disconnected, cancellationToken);
                log.Info("Long-polling stopped.");
            }
            catch (OperationCanceledException)
            {
                await context.SetConnectionStateAsync(ConnectionState.Disconnected, cancellationToken);
                log.Info("Long-polling stopped.");
            }
            catch (Exception e)
            {
                log.ErrorException("Long-polling stopped on unexpected exception.", e);
                await context.SetConnectionStateAsync(ConnectionState.DisconnectedOnError, cancellationToken);
                throw; // unobserved exception
            }
        }

        public void Dispose()
        {
            pollCancel.Cancel();
            pollCancel.Dispose();
        }

        async Task PollAsync(CancellationToken cancellationToken)
        {
            reconnectDelays.ResetIfLastSucceeded();

            try
            {
                if (startInBackground)
                {
                    startInBackground = false;
                    await context.OpenAsync(pollCancel.Token);
                    await HandshakeAsync(pollCancel.Token);
                }
                else if (transportFailed)
                {
                    transportFailed = false;

                    if (transportClosed)
                    {
                        transportClosed = false;
                        log.Info($"Re-opening transport due to previously failed request.");
                        await context.OpenAsync(pollCancel.Token);
                    }

                    log.Info($"Re-handshaking due to previously failed request.");
                    await HandshakeAsync(pollCancel.Token);
                }
                else switch (lastAdvice.reconnect)
                    {
                        case "none":
                            log.Info("Long-polling stopped on server request.");
                            Dispose();
                            break;

                        // https://docs.cometd.org/current/reference/#_the_code_long_polling_code_response_messages
                        // interval: the number of milliseconds the client SHOULD wait before issuing another long poll request

                        // usual sample advice:
                        // {"interval":0,"timeout":20000,"reconnect":"retry"}
                        // another sample advice, when too much time without polling:
                        // [{"advice":{"interval":0,"reconnect":"handshake"},"channel":"/meta/connect","error":"402::Unknown client","successful":false}]

                        case "handshake":
                            log.Info($"Re-handshaking after {lastAdvice.interval} ms on server request.");
                            await Task.Delay(lastAdvice.interval);
                            await HandshakeAsync(pollCancel.Token);
                            break;

                        case "retry":
                        default:
                            if (lastAdvice.interval > 0)
                                log.Info($"Re-connecting after {lastAdvice.interval} ms on server request.");

                            await Task.Delay(lastAdvice.interval);
                            await ConnectAsync(pollCancel.Token);
                            break;
                    }
            }
            catch (HttpRequestException e)
            {
                await context.SetConnectionStateAsync(ConnectionState.Connecting, cancellationToken);
                transportFailed = true;

                var reconnectDelay = reconnectDelays.GetNext();
                log.WarnException($"HTTP request failed. Rehandshaking after {reconnectDelay}", e);
                await Task.Delay(reconnectDelay);
            }
            catch (BayeuxTransportException e)
            {
                transportFailed = true;
                transportClosed = e.TransportClosed;

                await context.SetConnectionStateAsync(ConnectionState.Connecting, cancellationToken);

                var reconnectDelay = reconnectDelays.GetNext();
                log.WarnException($"Request transport failed. Retrying after {reconnectDelay}", e);
                await Task.Delay(reconnectDelay);
            }
            catch (BayeuxRequestException e)
            {
                await context.SetConnectionStateAsync(ConnectionState.Connecting, cancellationToken);
                log.Error($"Bayeux request failed with error: {e.BayeuxError}");
            }
        }

        async Task HandshakeAsync(CancellationToken cancellationToken)
        {
            await context.SetConnectionStateAsync(ConnectionState.Connecting, cancellationToken);

            var response = await context.RequestAsync(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { connectionType },
                },
                cancellationToken);

            currentConnection = new BayeuxConnection((string)response["clientId"], context);
            await context.SetConnectionAsync(currentConnection, cancellationToken);
            await context.SetConnectionStateAsync(ConnectionState.Connected, cancellationToken);
            ObtainAdvice(response);
        }

        async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var connectResponse = await currentConnection.ConnectAsync(cancellationToken);
            ObtainAdvice(connectResponse);
        }


        void ObtainAdvice(JObject response)
        {
            var adviceToken = response["advice"];
            if (adviceToken != null)
                lastAdvice = adviceToken.ToObject<BayeuxAdvice>();
        }

#pragma warning disable 0649 // "Field is never assigned to". These fields will be assigned by JSON deserialization
        class BayeuxAdvice
        {
            public string reconnect;
            public int interval = 0;
        }
#pragma warning restore 0649
    }

    class ReconnectDelays
    {
        readonly IEnumerable<TimeSpan> delays;

        IEnumerator<TimeSpan> currentDelaysEnumerator;
        TimeSpan currentDelay;
        bool lastSucceeded = true;


        public ReconnectDelays(IEnumerable<TimeSpan> delays)
        {
            this.delays = delays ??
                new List<TimeSpan> { TimeSpan.Zero, TimeSpan.FromSeconds(5) };
        }

        public void ResetIfLastSucceeded()
        {
            if (lastSucceeded)
                currentDelaysEnumerator = delays.GetEnumerator();

            lastSucceeded = true;
        }

        public TimeSpan GetNext()
        {
            lastSucceeded = false;

            if (currentDelaysEnumerator.MoveNext())
                currentDelay = currentDelaysEnumerator.Current;

            return currentDelay;
        }
    }
}
