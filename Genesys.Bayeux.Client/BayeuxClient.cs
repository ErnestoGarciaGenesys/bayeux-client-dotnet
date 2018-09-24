using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    // TODO: Make thread-safe, or thread-contained.
    public class BayeuxClient : IDisposable
    {
        public string Url { get; }

        public HttpClient HttpClient { get; }

        volatile string currentClientId;

        class Advice
        {
            public string reconnect;
            public int interval = 0;
        }

        volatile Advice lastAdvice;

        /// <summary>
        /// </summary>
        /// <param name="httpClient">
        ///   The HttpClient provided should prevent HTTP pipelining, because long-polling HTTP requests can delay 
        ///   other concurrent HTTP requests. If you are using an HttpClient from a WebRequestHandler, then you
        ///   should set WebRequestHandler.AllowPipelining to false.
        ///   See https://docs.cometd.org/current/reference/#_two_connection_operation.
        /// </param>
        /// <param name="url"></param>
        public BayeuxClient(HttpClient httpClient, string url)
        {
            HttpClient = httpClient;

            // TODO: allow relative URL to HttpClient.BaseAddress
            Url = url;
        }

        /// <summary>
        /// Does the Bayeux handshake, and starts long-polling.
        /// Handshake does not support re-negotiation; it fails at first unsuccessful response.
        /// </summary>
        /// <returns></returns>
        public async Task Start(CancellationToken cancellationToken = default(CancellationToken))
        {
            // TODO: avoid several handshake requests

            // TODO: how much timeout?
            await Handshake(cancellationToken);

            // TODO: A way to test the re-handshake with a real server is to put some delay here, between the first handshake response,
            // and the first try to connect. That will cause an "Invalid client id" response, with an advice of reconnect=handshake.
            // This can also be tested with a fake server in unit tests.
            StartLongPolling();
        }

        #region Long polling

        readonly CancellationTokenSource longPollingCancel = new CancellationTokenSource();

        void StartLongPolling()
        {
            LoopLongPolling(longPollingCancel.Token);
        }

        async void LoopLongPolling(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Poll(lastAdvice, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("Long-polling cancelled.");
            }
            catch (Exception e)
            {
                Debug.WriteLine("Long-polling stopped on unexpected exception.", e);
                throw; // TODO: Handle exceptions on this unobserved task? Who would receive them? Just possible to log them?
            }
        }

        async Task Poll(Advice advice, CancellationToken cancellationToken)
        {
            try
            {
                switch (advice.reconnect)
                {
                    case "none":
                        Debug.WriteLine($"{DateTime.Now} Stopping long-polling on server request.");
                        StopLongPolling();
                        break;

                    // https://docs.cometd.org/current/reference/#_the_code_long_polling_code_response_messages
                    // interval: the number of milliseconds the client SHOULD wait before issuing another long poll request

                    // usual sample advice:
                    // {"interval":0,"timeout":20000,"reconnect":"retry"}
                    // another sample advice, when too much time without polling:
                    // [{"advice":{"interval":0,"reconnect":"handshake"},"channel":"/meta/connect","error":"402::Unknown client","successful":false}]

                    case "handshake":
                        await Task.Delay(advice.interval);
                        Debug.WriteLine($"{DateTime.Now} Re-handshaking...");
                        await Handshake(cancellationToken);
                        Debug.WriteLine($"{DateTime.Now} Re-handshaken.");
                        break;

                    case "retry":
                    default:
                        await Task.Delay(advice.interval);
                        Debug.WriteLine($"{DateTime.Now} Polling...");
                        await Connect(cancellationToken);
                        Debug.WriteLine($"{DateTime.Now} Poll ended.");
                        break;
                }
            }
            catch (HttpRequestException e)
            {
                var retryDelay = 5000; // TODO: accept external implementation, with a backoff, for example
                Debug.WriteLine($"HTTP request failed. Retrying after {retryDelay} ms.", e);
                await Task.Delay(retryDelay);
            }
            catch (BayeuxRequestException e)
            {
                Debug.WriteLine($"Bayeux request failed with error: {e.BayeuxError}");
            }
        }

        void StopLongPolling()
        {
            longPollingCancel.Cancel();
        }

        #endregion

        protected virtual void Dispose(bool disposing)
        {
            StopLongPolling();
        }

        #region Dispose pattern boilerplate

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BayeuxClient()
        {
            Dispose(false);
        }

        #endregion

        class BayeuxResponse
        {
            public bool successful;
            public string error;
            public string clientId;
        }

        // TODO: choose best JSON methods to use, and best configuration of JsonSerializer
        readonly JsonSerializer jsonSerializer = JsonSerializer.Create();

        async Task Handshake(CancellationToken cancellationToken)
        {
            var response = await Request(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { "long-polling" },
                },
                cancellationToken);

            currentClientId = response.clientId;
        }

        // On defining .NET events
        // https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/event
        // https://stackoverflow.com/questions/3880789/why-should-we-use-eventhandler
        // https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/events/how-to-publish-events-that-conform-to-net-framework-guidelines
        public class EventReceivedArgs : EventArgs
        {
            readonly JObject ev;

            public EventReceivedArgs(JObject ev)
            {
                this.ev = ev;
            }

            // https://docs.cometd.org/current/reference/#_code_data_code
            // The data message field is an arbitrary JSON encoded *object*
            public JObject Data { get => (JObject) ev["data"]; }

            public string Channel { get => (string) ev["channel"]; }

            public JObject Message { get => ev; }

            public override string ToString() => ev.ToString();
        }

        public event EventHandler<EventReceivedArgs> EventReceived;

        protected virtual void OnEventReceived(EventReceivedArgs args)
            => EventReceived?.Invoke(this, args);

        Task Connect(CancellationToken cancellationToken)
        {
            return Request(
                new
                {
                    clientId = currentClientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                },
                cancellationToken);
        }

        public Task Subscribe(string channel, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Request(
                new
                {
                    clientId = currentClientId,
                    channel = "/meta/subscribe",
                    subscription = channel,
                },
                cancellationToken);
        }

        public Task Unsubscribe(string channel, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Request(
                new
                {
                    clientId = currentClientId,
                    channel = "/meta/unsubscribe",
                    subscription = channel,
                },
                cancellationToken);
        }

        public Task Disconnect(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Request(
                new
                {
                    clientId = currentClientId,
                    channel = "/meta/disconnect",
                },
                cancellationToken);
        }

        Task<HttpResponseMessage> Post(object message, CancellationToken cancellationToken)
        {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            var messageStr = JsonConvert.SerializeObject(new[] { message });
            Debug.WriteLine("Posting: " + messageStr); // TODO: proper configurable logging
                                                       // see https://docs.microsoft.com/en-us/dotnet/framework/debug-trace-profile/tracing-and-instrumenting-applications

            return HttpClient.PostAsync(
                Url,
                new StringContent(messageStr, Encoding.UTF8, "application/json"),
                cancellationToken);
        }

        // TODO: The response is actually not used elsewhere, no need to return it?
        async Task<BayeuxResponse> Request(object request, CancellationToken cancellationToken = default(CancellationToken))
        {
            var httpResponse = await Post(request, cancellationToken);

            // As a stream it could have better performance, but logging is easier with strings.
            var responseStr = await httpResponse.Content.ReadAsStringAsync();
            Debug.WriteLine("Received: " + responseStr); // TODO: proper configurable logging

            var responseToken = JToken.Parse(responseStr);
            IEnumerable<JToken> tokens = responseToken is JArray ?
                (IEnumerable<JToken>) responseToken :
                new [] { responseToken };

            // https://docs.cometd.org/current/reference/#_delivery
            // Event messages MAY be sent to the client in the same HTTP response 
            // as any other message other than a /meta/handshake response.
            JObject responseObj = null;
            var events = new List<JObject>();

            foreach (var token in tokens)
            {
                JObject message = (JObject) token;
                var channel = (string) message["channel"];

                // TODO: throw BayeuxProtocol Exception if no channel?

                if (channel.StartsWith("/meta/"))
                {
                    responseObj = message;
                }
                else
                {
                    events.Add(message);
                }

                // https://docs.cometd.org/current/reference/#_bayeux_advice
                // any Bayeux response message may contain an advice field.
                // Advice received always supersedes any previous received advice.
                var adviceToken = message["advice"];
                if (adviceToken != null)
                    lastAdvice = adviceToken.ToObject<Advice>();
            }

            // TODO: Do we need to accept a SyncContext, or TaskScheduler, for customizing how events are notified?
            // TODO: notify always on same thread, to preserve order
            Task.Run(() =>
            {
                foreach (var ev in events)
                {
                    OnEventReceived(new EventReceivedArgs(ev));
                    // TODO: handle client exceptions?
                }
            });

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestException(response.error);

            // I have received the following non-compliant error response from the Statistics API:
            // request: [{"clientId":"256fs7hljxavbz317cdt1d7t882v","channel":"/meta/subscribe","subscription":"/pepe"}]
            // response: {"timestamp":1536851691737,"status":500,"error":"Internal Server Error","message":"java.lang.IllegalArgumentException: Invalid channel id: pepe","path":"/statistics/v3/notifications"}
            
            return response;
        }
    }
}
