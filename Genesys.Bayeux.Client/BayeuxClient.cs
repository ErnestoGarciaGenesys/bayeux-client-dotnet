using Genesys.Bayeux.Client.Logging;
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
using static Genesys.Bayeux.Client.Logging.LogProvider;

namespace Genesys.Bayeux.Client
{
    // TODO: Make thread-safe, or thread-contained.
    public class BayeuxClient : IDisposable
    {
        // Don't use string formatting for logging, as it is not supported by the internal TraceSource implementation.
        static readonly ILog log;

        static BayeuxClient()
        {
            LogProvider.LogProviderResolvers.Add(
                new Tuple<IsLoggerAvailable, CreateLogProvider>(() => true, () => new TraceSourceLogProvider()));

            log = LogProvider.GetLogger(typeof(BayeuxClient).Namespace);
        }

        readonly string url;
        readonly HttpClient httpClient;
        readonly TaskScheduler eventTaskScheduler;

        volatile string currentClientId;

        class Advice
        {
            public string reconnect; // TODO: take care of these warnings.
            public int interval = 0;
        }

        volatile Advice lastAdvice;


        /// <param name="httpClient">
        /// The HttpClient provided should prevent HTTP pipelining for POST requests, because long-polling HTTP 
        /// requests can delay other concurrent HTTP requests. If you are using an HttpClient from a WebRequestHandler, 
        /// then you should set WebRequestHandler.AllowPipelining to false.
        /// See https://docs.cometd.org/current/reference/#_two_connection_operation.
        /// </param>
        /// <param name="eventTaskScheduler">
        /// <para>
        /// TaskScheduler for invoking events. Usually, you will be good by providing null. If you decide to 
        /// your own TaskScheduler, please make sure that it guarantees ordered execution of events.
        /// </para>
        /// <para>
        /// If null is provided, SynchronizationContext.Current will be used. This means that WPF and 
        /// Windows Forms applications will run events appropriately. If SynchronizationContext.Current
        /// is null, then a new TaskScheduler with ordered execution will be created.
        /// </para>
        /// </param>
        /// <param name="url"></param>
        public BayeuxClient(HttpClient httpClient, string url, TaskScheduler eventTaskScheduler = null)
        {
            this.httpClient = httpClient;

            // TODO: allow relative URL to HttpClient.BaseAddress
            this.url = url;

            this.eventTaskScheduler = ChooseTaskScheduler(eventTaskScheduler);
        }

        // TODO: Move this to external factory methods?
        TaskScheduler ChooseTaskScheduler(TaskScheduler eventTaskScheduler)
        {
            if (eventTaskScheduler != null)
                return eventTaskScheduler;
            
            if (SynchronizationContext.Current != null)
            {
                log.Info($"Using current SynchronizationContext for events: {SynchronizationContext.Current}");
                return TaskScheduler.FromCurrentSynchronizationContext();
            }
            else
            {
                log.Info("Using a new TaskScheduler with ordered execution for events.");
                return new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
            }
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
                log.Info("Long-polling cancelled.");
            }
            catch (Exception e)
            {
                log.ErrorException("Long-polling stopped on unexpected exception.", e);
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
                        log.Debug("Stopping long-polling on server request.");
                        StopLongPolling();
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
                        await Connect(cancellationToken);
                        break;
                }
            }
            catch (HttpRequestException e)
            {
                var retryDelay = 5000; // TODO: accept external implementation, with a backoff, for example
                log.ErrorException($"HTTP request failed. Retrying after {retryDelay} ms.", e);
                await Task.Delay(retryDelay);
            }
            catch (BayeuxRequestException e)
            {
                log.Error($"Bayeux request failed with error: {e.BayeuxError}");
            }
        }

        void StopLongPolling()
        {
            longPollingCancel.Cancel();
        }

        #endregion

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopLongPolling();
            }
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

        // TODO: is this thread-safe?
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

            log.Debug($"Posting: {messageStr}");

            return httpClient.PostAsync(
                url,
                new StringContent(messageStr, Encoding.UTF8, "application/json"),
                cancellationToken);
        }

        // TODO: The response is actually not used elsewhere, no need to return it?
        async Task<BayeuxResponse> Request(object request, CancellationToken cancellationToken = default(CancellationToken))
        {
            var httpResponse = await Post(request, cancellationToken);

            // As a stream it could have better performance, but logging is easier with strings.
            var responseStr = await httpResponse.Content.ReadAsStringAsync();

            log.Debug($"Received: {responseStr}");

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

            var _ = Task.Factory.StartNew(() =>
            {
                foreach (var ev in events)
                {
                    OnEventReceived(new EventReceivedArgs(ev));
                    // TODO: handle client exceptions?
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, eventTaskScheduler);

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestException(response.error);

            return response;
        }
    }
}
