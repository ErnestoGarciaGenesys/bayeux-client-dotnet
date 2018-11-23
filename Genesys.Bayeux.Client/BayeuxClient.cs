using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.Logging.LogProvider;

namespace Genesys.Bayeux.Client
{
    /// <summary>
    /// Abstraction for any HTTP client implementation.
    /// Also allows implementation of retry policies, useful for servers that may need a session refresh, for example. This is in general not supported by HttpClient, as for some versions SendAsync disposes the content of HttpRequestMessage. This means that, for a failed SendAsync call, it can't be retried, as the HttpRequestMessage can't be reused.
    /// </summary>
    public interface HttpPoster
    {
        Task<HttpResponseMessage> PostAsync(string requestUri, string jsonContent, CancellationToken cancellationToken);
    }

    public class HttpClientHttpPoster : HttpPoster
    {
        readonly HttpClient httpClient;

        public HttpClientHttpPoster(HttpClient httpClient)
        {
            this.httpClient = httpClient;
        }

        public Task<HttpResponseMessage> PostAsync(string requestUri, string jsonContent, CancellationToken cancellationToken)
        {
            return httpClient.PostAsync(
                requestUri,
                new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                cancellationToken);
        }
    }

    public class BayeuxClient : IDisposable
    {
        // Don't use string formatting for logging, as it is not supported by the internal TraceSource implementation.
        internal static readonly ILog log;

        static BayeuxClient()
        {
            LogProvider.LogProviderResolvers.Add(
                new Tuple<IsLoggerAvailable, CreateLogProvider>(() => true, () => new TraceSourceLogProvider()));

            log = LogProvider.GetLogger(typeof(BayeuxClient).Namespace);
        }


        readonly string url;
        readonly HttpPoster httpPoster;
        readonly TaskScheduler eventTaskScheduler;
        readonly Subscriber subscriber;
        readonly LongPollingLoop longPollingLoop;

        internal volatile BayeuxConnection currentConnection;


        /// <param name="httpPoster">
        /// An HTTP POST implementation. It should not do HTTP pipelining (rarely done for POSTs anyway).
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
        /// <param name="reconnectDelays">
        /// When a request results in network errors, reconnection trials will be delayed based on the 
        /// values passed here. The last element of the collection will be re-used indefinitely.
        /// </param>
        public BayeuxClient(
            HttpPoster httpPoster,
            string url,
            IEnumerable<TimeSpan> reconnectDelays = null,
            TaskScheduler eventTaskScheduler = null)
        {
            this.httpPoster = httpPoster;
            this.url = url;
            this.eventTaskScheduler = ChooseEventTaskScheduler(eventTaskScheduler);
            this.longPollingLoop = new LongPollingLoop(this, reconnectDelays);
            this.subscriber = new Subscriber(this);
        }

        public BayeuxClient(
            HttpClient httpClient, 
            string url, 
            IEnumerable<TimeSpan> reconnectDelays = null, 
            TaskScheduler eventTaskScheduler = null)
            : this(new HttpClientHttpPoster(httpClient), url, reconnectDelays, eventTaskScheduler)
        {
        }

        static TaskScheduler ChooseEventTaskScheduler(TaskScheduler eventTaskScheduler)
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
        public Task Start(CancellationToken cancellationToken = default(CancellationToken)) =>
            longPollingLoop.Start(cancellationToken);

        void StopLongPolling() =>
            longPollingLoop.Stop();

        public async Task Stop(CancellationToken cancellationToken = default(CancellationToken))
        {
            StopLongPolling();

            var connection = Interlocked.Exchange(ref currentConnection, null);
            if (connection != null)
                await connection.Disconnect(cancellationToken);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                _ = Stop();
            else
                StopLongPolling();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BayeuxClient()
        {
            Dispose(false);
        }

        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            DisconnectedOnError,
        }

        public class ConnectionStateChangedArgs : EventArgs
        {
            public ConnectionState ConnectionState { get; private set; }

            public ConnectionStateChangedArgs(ConnectionState state)
            {
                ConnectionState = state;
            }

            public override string ToString() => ConnectionState.ToString();
        }

        protected volatile int currentConnectionState = -1;

        public event EventHandler<ConnectionStateChangedArgs> ConnectionStateChanged;

        protected internal virtual void OnConnectionStateChanged(ConnectionState state)
        {
            var oldConnectionState = Interlocked.Exchange(ref currentConnectionState, (int) state);

            if (oldConnectionState != (int)state)
                RunInEventTaskScheduler(() =>
                    ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedArgs(state)));
        }   
        
        internal async Task<JObject> Handshake(CancellationToken cancellationToken)
        {
            OnConnectionStateChanged(ConnectionState.Connecting);

            var response = await Request(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { "long-polling" },
                },
                cancellationToken);

            currentConnection = new BayeuxConnection((string)response["clientId"], this);
            subscriber.OnConnected();
            OnConnectionStateChanged(ConnectionState.Connected);
            return response;
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

        /// <summary>
        /// Adds a subscription. Subscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="Subscribe(IEnumerable{string}, CancellationToken)"/>, but does not throw an exception when not currently connected.
        /// </summary>
        /// <param name="channels"></param>
        public void AddSubscriptions(params string[] channels) =>
            Subscribe(channels);

        /// <summary>
        /// Removes subscriptions. Unsubscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="Unsubscribe(IEnumerable{string}, CancellationToken)"/>, but does not throw an exception when not currently connected.
        /// </summary>
        /// <param name="channels"></param>
        public void RemoveSubscriptions(params string[] channels) =>
            Unsubscribe(channels);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Subscribe(string channel, CancellationToken cancellationToken = default(CancellationToken)) =>
            Subscribe(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Subscribe(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken))
        {
            subscriber.AddSubscription(channels);
            return TrySubscribe(channels, cancellationToken);
        }

        internal Task TrySubscribe(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken)) =>
            TrySubscriptionOperation("/meta/subscribe", channels, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Unsubscribe(string channel, CancellationToken cancellationToken = default(CancellationToken)) =>
            Unsubscribe(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Unsubscribe(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken))
        {
            subscriber.RemoveSubscription(channels);
            return TrySubscriptionOperation("/meta/unsubscribe", channels, cancellationToken);
        }

        async Task TrySubscriptionOperation(string metaChannel, IEnumerable<string> channels, CancellationToken cancellationToken)
        {
            var connection = currentConnection;

            if (connection == null)
                throw new InvalidOperationException("Not connected. Operation will be effective for next connections.");

            await connection.DoSubscriptionOperation(metaChannel, channels, cancellationToken);
        }

        Task<HttpResponseMessage> Post(IEnumerable<object> message, CancellationToken cancellationToken)
        {
            var messageStr = JsonConvert.SerializeObject(message);
            log.Debug($"Posting: {messageStr}");
            return httpPoster.PostAsync(url, messageStr, cancellationToken);
        }

#pragma warning disable 0649 // "Field is never assigned to". These fields will be assigned by JSON deserialization
        class BayeuxResponse
        {
            public bool successful;
            public string error;
        }
#pragma warning restore 0649

        internal Task<JObject> Request(object request, CancellationToken cancellationToken = default(CancellationToken)) =>
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            Request(new[] { request }, cancellationToken);

        async Task<JObject> Request(IEnumerable<object> request, CancellationToken cancellationToken = default(CancellationToken))
        {
            var httpResponse = await Post(request, cancellationToken);
            // As a stream it could have better performance, but logging is easier with strings.
            var responseStr = await httpResponse.Content.ReadAsStringAsync();

            log.Debug($"Received: {responseStr}");

            httpResponse.EnsureSuccessStatusCode();

            var responseToken = JToken.Parse(responseStr);
            IEnumerable<JToken> tokens = responseToken is JArray ?
                (IEnumerable<JToken>)responseToken :
                new[] { responseToken };

            // https://docs.cometd.org/current/reference/#_delivery
            // Event messages MAY be sent to the client in the same HTTP response 
            // as any other message other than a /meta/handshake response.
            JObject responseObj = null;
            var events = new List<JObject>();

            foreach (var token in tokens)
            {
                JObject message = (JObject)token;
                var channel = (string)message["channel"];

                if (channel == null)
                    throw new BayeuxProtocolException("No 'channel' field in message.");

                if (channel.StartsWith("/meta/"))
                {
                    responseObj = message;
                }
                else
                {
                    events.Add(message);
                }
            }

            RunInEventTaskScheduler(() =>
            {
                foreach (var ev in events)
                    OnEventReceived(new EventReceivedArgs(ev));
            });

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestException(response.error);

            return responseObj;
        }

        void RunInEventTaskScheduler(Action action) =>
            Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.DenyChildAttach, eventTaskScheduler);
    }
}
