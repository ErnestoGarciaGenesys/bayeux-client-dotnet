using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    // TODO: Check HTTP pipelining
    // https://docs.cometd.org/current/reference/#_two_connection_operation
    // Implementations MUST control HTTP pipelining so that req1 does not get queued behind req0 and thus enforce an ordering of responses.

    // TODO: Check implementation of negotiation
    // https://docs.cometd.org/current/reference/#_connection_negotiation
    // Bayeux connection negotiation may be iterative and several handshake messages may be exchanged before a successful connection is obtained. Servers may also request Bayeux connection renegotiation by sending an unsuccessful connect response with advice to reconnect with a handshake message.

    // TODO: keep alive, and re-subscribe when reconnected through a different session (different clientId?)

    // TODO: Make thread-safe, or thread-contained.

    // TODO: Implement IDisposable

    // TODO: do test to provoke an "Invalid client id" response, by taking too long from the handshake response to a connect or subscribe request.
    public class BayeuxClient
    {
        public string Url { get; }

        public HttpClient HttpClient { get; }

        public string ClientId { get; private set; }

        public BayeuxClient(HttpClient httpClient, string url)
        {
            HttpClient = httpClient;

            // TODO: allow relative URL to HttpClient.BaseAddress
            Url = url;
        }

        public async Task Start()
        {
            await Handshake();
            StartLongPolling();
        }

        class BayeuxResponse
        {
            public bool successful;
            public string error;
            public string clientId;
        }

        readonly JsonSerializer jsonSerializer = JsonSerializer.Create();

        // TODO: avoid several handshake requests
        async Task Handshake()
        {
            var response = await Request(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { "long-polling" },
                });

            ClientId = response.clientId;
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
        {
            var handler = EventReceived; // avoid race condition after checking for null
            handler?.Invoke(this, args);
        }

        async void StartLongPolling()
        {
            await Connect();
            StartLongPolling();
        }

        public Task Subscribe(string channel)
        {
            return Request(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/subscribe",
                    subscription = channel,
                });
        }

        public Task Unsubscribe(string channel)
        {
            return Request(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/unsubscribe",
                    subscription = channel,
                });
        }

        public Task Disconnect()
        {
            return Request(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/disconnect",
                });
        }

        Task Connect()
        {
            Debug.WriteLine("Polling...");
            return Request(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                });
        }

        Task<HttpResponseMessage> Post(object message)
        {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            var messageStr = JsonConvert.SerializeObject(new[] { message });
            Debug.WriteLine("Posting: " + messageStr); // TODO: proper configurable logging

            return HttpClient.PostAsync(Url, new StringContent(messageStr, Encoding.UTF8, "application/json"));
        }

        async Task<BayeuxResponse> Request(object request)
        {
            var httpResponse = await Post(request);

            // As a stream it could have better performance, but logging is easier with strings.
            var responseStr = await httpResponse.Content.ReadAsStringAsync();
            Debug.WriteLine("Received: " + responseStr); // TODO: proper configurable logging

            var responseToken = JToken.Parse(responseStr);
            IEnumerable<JToken> tokens = responseToken is JArray ? (IEnumerable<JToken>) responseToken : new [] { responseToken };

            // https://docs.cometd.org/current/reference/#_delivery
            // Event messages MAY be sent to the client in the same HTTP response as any other message other than a /meta/handshake response.
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

                FollowAdvice(message);
            }

            // TODO: notify always on same thread, to preserve order
            Task.Run(() =>
            {
                foreach (var ev in events)
                {
                    OnEventReceived(new EventReceivedArgs(ev));
                }
            });

            var response = responseObj.ToObject<BayeuxResponse>();

            if (!response.successful)
                throw new BayeuxRequestFailedException(response.error);

            // I have received the following non-compliant error response from the Statistics API:
            // request: [{"clientId":"256fs7hljxavbz317cdt1d7t882v","channel":"/meta/subscribe","subscription":"/pepe"}]
            // response: {"timestamp":1536851691737,"status":500,"error":"Internal Server Error","message":"java.lang.IllegalArgumentException: Invalid channel id: pepe","path":"/statistics/v3/notifications"}
            
            return response;
        }

        class Advice
        {
            public long interval;
            public long timeout;
            public string reconnect;
        }
        // sample advice received: {"interval":0,"timeout":20000,"reconnect":"retry"}

        void FollowAdvice(JObject message)
        {
            var adviceToken = message["advice"];
            if (adviceToken != null)
            {
                var advice = adviceToken.ToObject<Advice>();
                // TODO: follow advice
                // https://docs.cometd.org/current/reference/#_bayeux_advice
                // any Bayeux response message may contain an advice field. Advice received always supersedes any previous received advice.
            }
        }
    }
}
