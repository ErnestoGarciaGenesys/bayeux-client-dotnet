using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.Logging.LogProvider;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]

namespace Genesys.Bayeux.Client
{
    public class BayeuxClient : IDisposable, IContext
    {
        // Don't use string formatting for logging, as it is not supported by the internal TraceSource implementation.
        internal static readonly ILog log;

        static BayeuxClient()
        {
            LogProvider.LogProviderResolvers.Add(
                new Tuple<IsLoggerAvailable, CreateLogProvider>(() => true, () => new TraceSourceLogProvider()));

            log = LogProvider.GetLogger(typeof(BayeuxClient).Namespace);
        }


        readonly HttpTransport transport;
        readonly TaskScheduler eventTaskScheduler;
        readonly Subscriber subscriber;
        readonly ConnectLoop connectLoop;

        volatile BayeuxConnection currentConnection;


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
            IHttpPoster httpPoster,
            string url,
            IEnumerable<TimeSpan> reconnectDelays = null,
            TaskScheduler eventTaskScheduler = null)
        {
            this.transport = new HttpTransport(httpPoster, url, PublishEvents);
            this.eventTaskScheduler = ChooseEventTaskScheduler(eventTaskScheduler);
            this.connectLoop = new ConnectLoop(
                "long-polling",
                reconnectDelays,
                this);
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
        
        // TODO: Modify all ctors for clear construction of a BayeuxClient. Possibly through static factory methods.
        //public BayeuxClient(string url, TaskScheduler eventTaskScheduler = null, IEnumerable<TimeSpan> reconnectDelays = null)
        //{
        //    this.transport = new WebSocketTransport(url);
        //    this.eventTaskScheduler = ChooseEventTaskScheduler(eventTaskScheduler);
        //    this.longPollingLoop = new LongPollingLoop(this, reconnectDelays);
        //    this.subscriber = new Subscriber(this);
        //}

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

        // TODO: add a new method to Start without failing when first connection has failed.

        /// <summary>
        /// Does the Bayeux handshake, and starts long-polling.
        /// Handshake does not support re-negotiation; it fails at first unsuccessful response.
        /// </summary>
        public Task Start(CancellationToken cancellationToken = default(CancellationToken)) =>
            connectLoop.Start(cancellationToken);

        void StopLongPolling() =>
            connectLoop.Stop();

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

        internal void SetNewConnection(BayeuxConnection newConnection)
        {
            currentConnection = newConnection;
            subscriber.OnConnected();
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

        #region Public subscription methods

        /// <summary>
        /// Adds a subscription. Subscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="Subscribe(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public void AddSubscriptions(params string[] channels) =>
            SubscribeImpl(channels, CancellationToken.None, throwIfNotConnected: false);

        /// <summary>
        /// Removes subscriptions. Unsubscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="Unsubscribe(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public void RemoveSubscriptions(params string[] channels) =>
            UnsubscribeImpl(channels, CancellationToken.None, throwIfNotConnected: false);            

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Subscribe(string channel, CancellationToken cancellationToken = default(CancellationToken)) =>
            Subscribe(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Unsubscribe(string channel, CancellationToken cancellationToken = default(CancellationToken)) =>
            Unsubscribe(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Subscribe(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken)) =>
            SubscribeImpl(channels, cancellationToken, throwIfNotConnected: true);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task Unsubscribe(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken)) =>
            UnsubscribeImpl(channels, cancellationToken, throwIfNotConnected: true);

        #endregion

        Task SubscribeImpl(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
        {
            subscriber.AddSubscription(channels);
            return RequestSubscribe(channels, cancellationToken, throwIfNotConnected);
        }

        Task UnsubscribeImpl(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
        {
            subscriber.RemoveSubscription(channels);
            return RequestUnsubscribe(channels, cancellationToken, throwIfNotConnected);
        }

        internal Task RequestSubscribe(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected) =>
            RequestSubscription(channels, Enumerable.Empty<string>(), cancellationToken, throwIfNotConnected);

        Task RequestUnsubscribe(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected) =>
            RequestSubscription(Enumerable.Empty<string>(), channels, cancellationToken, throwIfNotConnected);

        async Task RequestSubscription(
            IEnumerable<string> channelsToSubscribe,
            IEnumerable<string> channelsToUnsubscribe,
            CancellationToken cancellationToken,
            bool throwIfNotConnected)
        {
            var connection = currentConnection;
            if (connection == null)
            {
                if (throwIfNotConnected)
                    throw new InvalidOperationException("Not connected. Operation will be effective on next connection.");
            }
            else
            {
                await connection.DoSubscription(channelsToSubscribe, channelsToUnsubscribe, cancellationToken);
            }
        }

        #pragma warning disable 0649 // "Field is never assigned to". These fields will be assigned by JSON deserialization
        class BayeuxResponse
        {
            public bool successful;
            public string error;
        }
        #pragma warning restore 0649

        internal Task<JObject> Request(object request, CancellationToken cancellationToken)
        {
            Trace.Assert(!(request is System.Collections.IEnumerable), "Use method RequestMany");
            return RequestMany(new[] { request }, cancellationToken);
        }

        internal async Task<JObject> RequestMany(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            var responseObj = await transport.Request(requests, cancellationToken);

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestException(response.error);

            return responseObj;
        }

        void PublishEvents(IEnumerable<JObject> events) =>
            RunInEventTaskScheduler(() =>
            {
                foreach (var ev in events)
                    OnEventReceived(new EventReceivedArgs(ev));
            });

        void RunInEventTaskScheduler(Action action) =>
            Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.DenyChildAttach, eventTaskScheduler);

        Task<JObject> IContext.Request(object request, CancellationToken cancellationToken)
            => Request(request, cancellationToken);

        Task<JObject> IContext.RequestMany(IEnumerable<object> requests, CancellationToken cancellationToken)
            => RequestMany(requests, cancellationToken);

        void IContext.SetConnectionState(ConnectionState newState)
            => OnConnectionStateChanged(newState);

        void IContext.SetConnection(BayeuxConnection newConnection)
            => SetNewConnection(newConnection);
    }
}
