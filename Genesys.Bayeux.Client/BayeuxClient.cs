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

        class Advice
        {
            public long interval;
            public long timeout;
            public string reconnect;
        }
        // sample advice received: {"interval":0,"timeout":20000,"reconnect":"retry"}

        class BayeuxResponse
        {
            public bool successful;
            public string error;
            public string clientId;
            public Advice advice;
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

        void FollowAdvice(Advice advice)
        {
            if (advice != null)
            {
                // TODO: follow advice
                // https://docs.cometd.org/current/reference/#_bayeux_advice
                // any Bayeux response message may contain an advice field. Advice received always supersedes any previous received advice.
            }
        }

        // Use an EventArgs subclass?
        // See https://docs.microsoft.com/en-us/dotnet/standard/design-guidelines/event
        // https://stackoverflow.com/questions/3880789/why-should-we-use-eventhandler
        // https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/events/how-to-publish-events-that-conform-to-net-framework-guidelines
        public event EventHandler<string> MessagesReceived;
        
        void StartLongPolling()
        {
            Poll().ContinueWith(task =>
            {
                Debug.WriteLine("Poll ended.");
                if (!task.IsFaulted)
                {
                    task.Result.Content.ReadAsStringAsync().ContinueWith(
                        s => MessagesReceived?.Invoke(this, s.Result));
                }

                StartLongPolling();
            });
        }

        public Task Subscribe(string channel)
        {
            return Request(
                new {       
                    clientId = ClientId,
                    channel = "/meta/subscribe",
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

        public Task<HttpResponseMessage> Poll()
        {
            Debug.WriteLine("Polling...");
            return Post(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                });
        }

        Task<HttpResponseMessage> Post(object obj)
        {
            // https://docs.cometd.org/current/reference/#_messages
            // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together
            return HttpClient.PostAsync(Url, ToJsonContent(new[] { obj }));
        }

        async Task<BayeuxResponse> Request(object request)
        {
            var httpResponse = await Post(request);
            var responseStr = await httpResponse.Content.ReadAsStringAsync();

            Debug.WriteLine("Received: " + responseStr); // TODO: proper configurable logging

            BayeuxResponse response = ExtractResponse(responseStr);

            if (!response.successful)
                throw new BayeuxRequestFailedException(response.error);

            // I have received the following non-compliant error response from the Statistics API:
            // request: [{"clientId":"256fs7hljxavbz317cdt1d7t882v","channel":"/meta/subscribe","subscription":"/pepe"}]
            // response: {"timestamp":1536851691737,"status":500,"error":"Internal Server Error","message":"java.lang.IllegalArgumentException: Invalid channel id: pepe","path":"/statistics/v3/notifications"}

            FollowAdvice(response.advice);

            return response;
        }

        private static BayeuxResponse ExtractResponse(string responseStr)
        {
            // As a stream it could have better performance, but logging is easier with strings:
            //var responseStream = await httpResponse.Content.ReadAsStreamAsync();
            //var responseList = jsonSerializer.Deserialize<List<BayeuxResponse>>(
            //    new JsonTextReader(new StreamReader(responseStream, Encoding.UTF8)));

            var token = JToken.Parse(responseStr); // TODO: pass proper JsonLoadSettings

            BayeuxResponse response;

            if (token is JArray)
            {
                var responses = token.ToObject<List<BayeuxResponse>>();
                Debug.Assert(responses.Count == 1);
                response = responses[0];
            }
            else
            {
                response = token.ToObject<BayeuxResponse>();
            }

            return response;
        }

        HttpContent ToJsonContent(object message)
        {
            var result = JsonConvert.SerializeObject(message);
            Debug.WriteLine("Posting: " + result); // TODO: proper configurable logging
            return new StringContent(result, Encoding.UTF8, "application/json"); ;
        }
    }
}
