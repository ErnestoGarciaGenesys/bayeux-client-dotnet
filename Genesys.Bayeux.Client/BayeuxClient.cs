using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.Logging.LogProvider;

namespace Genesys.Bayeux.Client
{
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
        public JObject Data { get => (JObject)ev["data"]; }
        public string Channel { get => (string)ev["channel"]; }
        public JObject Message { get => ev; }
        public override string ToString() => ev.ToString();
    }

    public class BayeuxClient : IDisposable, IBayeuxClientContext
    {
        internal static readonly ILog log;

        static BayeuxClient()
        {
            LogProvider.LogProviderResolvers.Add(
                new Tuple<IsLoggerAvailable, CreateLogProvider>(() => true, () => new TraceSourceLogProvider()));

            log = LogProvider.GetLogger(typeof(BayeuxClient).Namespace);
        }

        readonly IBayeuxTransport transport;
        readonly TaskScheduler eventTaskScheduler;
        readonly ConnectLoop connectLoop;

        volatile BayeuxConnection currentConnection;

        private readonly HashSet<string> subscribedChannels = new HashSet<string>();
        private readonly object subscribedChannelsLock = new object();


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
        /// <param name="reconnectDelays">
        /// When a request results in network errors, reconnection trials will be delayed based on the
        /// values passed here. The last element of the collection will be re-used indefinitely.
        /// </param>
        public BayeuxClient(
            HttpLongPollingTransportOptions options,
            IEnumerable<TimeSpan> reconnectDelays = null,
            TaskScheduler eventTaskScheduler = null)
        {
            this.transport = options.Build(PublishEventsAsync);
            this.eventTaskScheduler = ChooseEventTaskScheduler(eventTaskScheduler);
            this.connectLoop = new ConnectLoop("long-polling", reconnectDelays, this);
        }

        public BayeuxClient(
            WebSocketTransportOptions options,
            IEnumerable<TimeSpan> reconnectDelays = null,
            TaskScheduler eventTaskScheduler = null)
        {
            this.transport = options.Build(PublishEventsAsync);
            this.eventTaskScheduler = ChooseEventTaskScheduler(eventTaskScheduler);
            this.connectLoop = new ConnectLoop("websocket", reconnectDelays, this);
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

        // TODO: add a new method to Start without failing when first connection has failed.

        /// <summary>
        /// Does the Bayeux handshake, and starts long-polling.
        /// Handshake does not support re-negotiation; it fails at first unsuccessful response.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
            connectLoop.StartAsync(cancellationToken);

        /// <summary>
        /// Does the Bayeux handshake, and starts long-polling in the background with reconnects as needed.
        /// This method does not fail.
        /// </summary>
        public Task StartInBackgroundAsync(CancellationToken cancellationToken = default(CancellationToken))
         => connectLoop.StartInBackgroundAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            connectLoop.Dispose();

            var connection = Interlocked.Exchange(ref currentConnection, null);
            await connection?.DisconnectAsync(cancellationToken);
            transport.Dispose();
        }

        public void Dispose()
        {
            _ = StopAsync();
        }

        protected volatile int currentConnectionState = -1;

        public event EventHandler<ConnectionStateChangedArgs> ConnectionStateChanged;

        protected internal virtual async Task OnConnectionStateChangedAsync(ConnectionState state, CancellationToken cancellationToken)
        {
            var oldConnectionState = Interlocked.Exchange(ref currentConnectionState, (int)state);

            if (oldConnectionState != (int)state)
                await RunInEventTaskSchedulerAsync(() =>
                    ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedArgs(state)), cancellationToken);
        }

        public Task SetConnectionAsync(BayeuxConnection newConnection, CancellationToken cancellationToken)
        {
            currentConnection = newConnection;
            return RequestSubscribeAsync(subscribedChannels.ToImmutableArray(), cancellationToken, throwIfNotConnected: false);
        }


        public event EventHandler<EventReceivedArgs> EventReceived;

        protected virtual void OnEventReceived(EventReceivedArgs args)
            => EventReceived?.Invoke(this, args);

        #region Public subscription methods

        /// <summary>
        /// Adds a subscription. Subscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="SubscribeAsync(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public Task AddSubscriptionsAsync(params string[] channels)
         => SubscribeImplAsync(channels, CancellationToken.None, throwIfNotConnected: false);

        /// <summary>
        /// Adds a subscription. Subscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="SubscribeAsync(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public Task AddSubscriptionsAsync(CancellationToken cancellationToken, params string[] channels)
         => SubscribeImplAsync(channels, cancellationToken, throwIfNotConnected: false);

        /// <summary>
        /// Removes subscriptions. Unsubscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="UnsubscribeAsync(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public Task RemoveSubscriptionsAsync(params string[] channels)
         => UnsubscribeImplAsync(channels, CancellationToken.None, throwIfNotConnected: false);

        /// <summary>
        /// Removes subscriptions. Unsubscribes immediately if this BayeuxClient is connected.
        /// This is equivalent to <see cref="UnsubscribeAsync(IEnumerable{string}, CancellationToken)"/>, but does not await result and does not throw an exception if disconnected.
        /// </summary>
        /// <param name="channels"></param>
        public Task RemoveSubscriptionsAsync(CancellationToken cancellationToken, params string[] channels)
         => UnsubscribeImplAsync(channels, cancellationToken, throwIfNotConnected: false);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task SubscribeAsync(string channel, CancellationToken cancellationToken = default(CancellationToken))
         => SubscribeAsync(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task UnsubscribeAsync(string channel, CancellationToken cancellationToken = default(CancellationToken))
         => UnsubscribeAsync(new[] { channel }, cancellationToken);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task SubscribeAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken))
         => SubscribeImplAsync(channels, cancellationToken, throwIfNotConnected: true);

        /// <exception cref="InvalidOperationException">If the Bayeux connection is not currently connected.</exception>
        public Task UnsubscribeAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default(CancellationToken))
         => UnsubscribeImplAsync(channels, cancellationToken, throwIfNotConnected: true);

        #endregion

        Task SubscribeImplAsync(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
        {
            lock (subscribedChannelsLock)
            {
                foreach (var channel in channels)
                    subscribedChannels.Add(channel);
            }
            return RequestSubscribeAsync(channels, cancellationToken, throwIfNotConnected);
        }

        Task UnsubscribeImplAsync(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
        {
            lock (subscribedChannelsLock)
            {
                foreach (var channel in channels)
                    subscribedChannels.Remove(channel);
            }
            return RequestUnsubscribeAsync(channels, cancellationToken, throwIfNotConnected);
        }

        internal Task RequestSubscribeAsync(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
         => RequestSubscriptionAsync(channels, Enumerable.Empty<string>(), cancellationToken, throwIfNotConnected);

        internal Task RequestUnsubscribeAsync(IEnumerable<string> channels, CancellationToken cancellationToken, bool throwIfNotConnected)
         => RequestSubscriptionAsync(Enumerable.Empty<string>(), channels, cancellationToken, throwIfNotConnected);

        internal async Task RequestSubscriptionAsync(
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
            else if (channelsToSubscribe.Any() || channelsToUnsubscribe.Any())
            {
                await connection.DoSubscriptionAsync(channelsToSubscribe, channelsToUnsubscribe, cancellationToken);
            }
        }

#pragma warning disable 0649 // "Field is never assigned to". These fields will be assigned by JSON deserialization
        class BayeuxResponse
        {
            public bool successful;
            public string error;
        }
#pragma warning restore 0649

        internal Task<JObject> RequestAsync(object request, CancellationToken cancellationToken)
        {
            Trace.Assert(!(request is System.Collections.IEnumerable), "Use method RequestMany");
            return RequestManyAsync(new[] { request }, cancellationToken);
        }

        internal async Task<JObject> RequestManyAsync(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            var responseObj = await transport.RequestAsync(requests, cancellationToken);

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestException(response.error);

            return responseObj;
        }

        internal Task PublishEventsAsync(IEnumerable<JObject> events, CancellationToken cancellationToken)
         => RunInEventTaskSchedulerAsync(() =>
            {
                foreach (var ev in events)
                    OnEventReceived(new EventReceivedArgs(ev));
            }, cancellationToken);

        internal Task RunInEventTaskSchedulerAsync(Action action, CancellationToken cancellationToken)
         => Task.Factory.StartNew(action, cancellationToken, TaskCreationOptions.DenyChildAttach, eventTaskScheduler);

        Task IBayeuxClientContext.OpenAsync(CancellationToken cancellationToken)
         => transport.OpenAsync(cancellationToken);

        Task<JObject> IBayeuxClientContext.RequestAsync(object request, CancellationToken cancellationToken)
         => RequestAsync(request, cancellationToken);

        Task<JObject> IBayeuxClientContext.RequestManyAsync(IEnumerable<object> requests, CancellationToken cancellationToken)
         => RequestManyAsync(requests, cancellationToken);

        Task IBayeuxClientContext.SetConnectionStateAsync(ConnectionState newState, CancellationToken cancellationToken)
         => OnConnectionStateChangedAsync(newState, cancellationToken);
    }
}
