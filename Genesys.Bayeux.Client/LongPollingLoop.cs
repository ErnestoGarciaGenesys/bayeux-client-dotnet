using Genesys.Bayeux.Client.Logging;
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
    class LongPollingLoop
    {
        static readonly ILog log = BayeuxClient.log;

        readonly BayeuxClient client;
        readonly CancellationTokenSource pollCancel = new CancellationTokenSource();

        BayeuxConnection currentConnection;
        BayeuxAdvice lastAdvice = new BayeuxAdvice();

        readonly IEnumerable<TimeSpan> reconnectDelays;
        IEnumerator<TimeSpan> reconnectDelaysEnumerator;
        TimeSpan currentReconnectDelay;
        bool rehandshakeOnFailure = false;

        int started = 0;

        public LongPollingLoop(
            BayeuxClient client, // TODO: remove reference to client
            IEnumerable<TimeSpan> reconnectDelays)
        {
            this.client = client;

            this.reconnectDelays = reconnectDelays ??
                new List<TimeSpan> { TimeSpan.Zero, TimeSpan.FromSeconds(5) };

            reconnectDelaysEnumerator = this.reconnectDelays.GetEnumerator();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            var alreadyStarted = Interlocked.Exchange(ref started, 1);
            if (alreadyStarted == 1)
                throw new Exception("Already started.");

            await Handshake(cancellationToken);

            // A way to test the re-handshake with a real server is to put some delay here, between the first handshake response,
            // and the first try to connect. That will cause an "Invalid client id" response, with an advice of reconnect=handshake.
            // This can also be tested with a fake server in unit tests.

            LoopPolling(pollCancel.Token);
        }

        async void LoopPolling(CancellationToken cancellationToken)
        {
            try
            {
                while (!pollCancel.IsCancellationRequested)
                    await Poll(lastAdvice, pollCancel.Token);

                client.OnConnectionStateChanged(ConnectionState.Disconnected);
            }
            catch (TaskCanceledException)
            {
                client.OnConnectionStateChanged(ConnectionState.Disconnected);
                log.Info("Long-polling cancelled.");
            }
            catch (Exception e)
            {
                log.ErrorException("Long-polling stopped on unexpected exception.", e);
                client.OnConnectionStateChanged(ConnectionState.DisconnectedOnError);
                throw; // unobserved exception
            }
        }

        public void Stop()
        {
            pollCancel.Cancel();
        }

        async Task Poll(BayeuxAdvice advice, CancellationToken cancellationToken)
        {
            var resetReconnectDelayProvider = true;

            try
            {
                if (rehandshakeOnFailure)
                {
                    rehandshakeOnFailure = false;
                    log.Debug($"Re-handshaking due to previously failed HTTP request.");
                    await Handshake(cancellationToken);
                }
                else switch (advice.reconnect)
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
                        log.Debug($"Re-handshaking after {advice.interval} ms on server request.");
                        await Task.Delay(advice.interval);
                        await Handshake(cancellationToken);
                        break;

                    case "retry":
                    default:
                        if (advice.interval > 0)
                            log.Debug($"Re-connecting after {advice.interval} ms on server request.");

                        await Task.Delay(advice.interval);
                        var connectResponse = await currentConnection.Connect(cancellationToken);
                        ObtainAdvice(connectResponse);
                        break;
                }
            }
            catch (HttpRequestException e)
            {
                client.OnConnectionStateChanged(ConnectionState.Connecting);

                rehandshakeOnFailure = true;
                resetReconnectDelayProvider = false;
                if (reconnectDelaysEnumerator.MoveNext())
                    currentReconnectDelay = reconnectDelaysEnumerator.Current;
                log.ErrorException($"HTTP request failed. Rehandshaking after {currentReconnectDelay}", e);
                await Task.Delay(currentReconnectDelay);
            }
            catch (BayeuxRequestException e)
            {
                client.OnConnectionStateChanged(ConnectionState.Connecting);
                log.Error($"Bayeux request failed with error: {e.BayeuxError}");
            }

            if (resetReconnectDelayProvider)
                reconnectDelaysEnumerator = reconnectDelays.GetEnumerator();
        }

        async Task Handshake(CancellationToken cancellationToken)
        {
            client.OnConnectionStateChanged(ConnectionState.Connecting);

            var response = await client.Request(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { "long-polling" },
                },
                cancellationToken);

            currentConnection = new BayeuxConnection((string)response["clientId"], client);
            client.SetNewConnection(currentConnection);
            client.OnConnectionStateChanged(ConnectionState.Connected);
            ObtainAdvice(response);
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
}
