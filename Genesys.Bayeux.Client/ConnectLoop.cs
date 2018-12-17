using Genesys.Bayeux.Client.Logging;
using Genesys.Bayeux.Client.Util;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.BayeuxClient;

namespace Genesys.Bayeux.Client
{
    internal interface IContext
    {
        Task<JObject> Request(object request, CancellationToken cancellationToken);
        Task<JObject> RequestMany(IEnumerable<object> requests, CancellationToken cancellationToken);
        void SetConnectionState(ConnectionState newState);
        void SetConnection(BayeuxConnection newConnection);
    }

    class ConnectLoop
    {
        static readonly ILog log = BayeuxClient.log;

        readonly string connectionType;
        readonly ReconnectDelays reconnectDelays;
        readonly IContext context;

        readonly CancellationTokenSource pollCancel = new CancellationTokenSource();

        BayeuxConnection currentConnection;
        BayeuxAdvice lastAdvice = new BayeuxAdvice();
        bool rehandshakeOnFailure = false;


        public ConnectLoop(
            string connectionType,
            IEnumerable<TimeSpan> reconnectDelays,
            IContext context)
        {
            this.connectionType = connectionType;
            this.reconnectDelays = new ReconnectDelays(reconnectDelays);
            this.context = context;
        }

        readonly BooleanLatch startLatch = new BooleanLatch();

        public async Task Start(CancellationToken cancellationToken)
        {
            if (startLatch.AlreadyRun())
                throw new Exception("Already started.");

            await Handshake(cancellationToken);

            // A way to test the re-handshake with a real server is to put some delay here, between the first handshake response,
            // and the first try to connect. That will cause an "Invalid client id" response, with an advice of reconnect=handshake.
            // This can also be tested with a fake server in unit tests.

            LoopPolling();
        }

        async void LoopPolling()
        {
            try
            {
                while (!pollCancel.IsCancellationRequested)
                    await Poll();

                context.SetConnectionState(ConnectionState.Disconnected);
            }
            catch (TaskCanceledException)
            {
                context.SetConnectionState(ConnectionState.Disconnected);
                log.Info("Long-polling cancelled.");
            }
            catch (Exception e)
            {
                log.ErrorException("Long-polling stopped on unexpected exception.", e);
                context.SetConnectionState(ConnectionState.DisconnectedOnError);
                throw; // unobserved exception
            }
        }

        public void Stop()
        {
            pollCancel.Cancel();
        }

        async Task Poll()
        {
            reconnectDelays.ResetIfLastSucceeded();

            try
            {
                if (rehandshakeOnFailure)
                {
                    rehandshakeOnFailure = false;
                    log.Debug($"Re-handshaking due to previously failed HTTP request.");
                    await Handshake(pollCancel.Token);
                }
                else switch (lastAdvice.reconnect)
                    {
                        case "none":
                            log.Debug("Long-polling stopped on server request.");
                            Stop();
                            break;

                        // https://docs.cometd.org/current/reference/#_the_code_long_polling_code_response_messages
                        // interval: the number of milliseconds the client SHOULD wait before issuing another long poll request

                        // usual sample advice:
                        // {"interval":0,"timeout":20000,"reconnect":"retry"}
                        // another sample advice, when too much time without polling:
                        // [{"advice":{"interval":0,"reconnect":"handshake"},"channel":"/meta/connect","error":"402::Unknown client","successful":false}]

                        case "handshake":
                            log.Debug($"Re-handshaking after {lastAdvice.interval} ms on server request.");
                            await Task.Delay(lastAdvice.interval);
                            await Handshake(pollCancel.Token);
                            break;

                        case "retry":
                        default:
                            if (lastAdvice.interval > 0)
                                log.Debug($"Re-connecting after {lastAdvice.interval} ms on server request.");

                            await Task.Delay(lastAdvice.interval);
                            await Connect(pollCancel.Token);
                            break;
                    }
            }
            catch (HttpRequestException e)
            {
                context.SetConnectionState(ConnectionState.Connecting);
                rehandshakeOnFailure = true;

                var reconnectDelay = reconnectDelays.GetNext();
                log.ErrorException($"HTTP request failed. Rehandshaking after {reconnectDelay}", e);
                await Task.Delay(reconnectDelay);
            }
            catch (BayeuxRequestException e)
            {
                context.SetConnectionState(ConnectionState.Connecting);
                log.Error($"Bayeux request failed with error: {e.BayeuxError}");
            }
        }

        async Task Handshake(CancellationToken cancellationToken)
        {
            context.SetConnectionState(ConnectionState.Connecting);

            var response = await context.Request(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { connectionType },
                },
                cancellationToken);

            currentConnection = new BayeuxConnection((string)response["clientId"], context);
            context.SetConnection(currentConnection);
            context.SetConnectionState(ConnectionState.Connected);
            ObtainAdvice(response);
        }

        async Task Connect(CancellationToken cancellationToken)
        {
            var connectResponse = await currentConnection.Connect(cancellationToken);
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
