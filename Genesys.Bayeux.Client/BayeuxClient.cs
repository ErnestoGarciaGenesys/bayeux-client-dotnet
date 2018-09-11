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
    // TODO: Implement protocol correctly:

    // https://docs.cometd.org/current/reference/#_messages
    // All Bayeux messages SHOULD be encapsulated in a JSON encoded array so that multiple messages may be transported together

    // https://docs.cometd.org/current/reference/#_code_successful_code
    // https://docs.cometd.org/current/reference/#_code_error_code
    // The boolean successful message field is used to indicate success or failure and MUST be included in responses to the /meta/handshake, /meta/connect, /meta/subscribe, /meta/unsubscribe, /meta/disconnect, and publish channels.

    // https://docs.cometd.org/current/reference/#__bayeux_advice
    // any Bayeux response message may contain an advice field. Advice received always supersedes any previous received advice.

    // https://docs.cometd.org/current/reference/#_two_connection_operation
    // Implementations MUST control HTTP pipelining so that req1 does not get queued behind req0 and thus enforce an ordering of responses.

    // https://docs.cometd.org/current/reference/#_connection_negotiation
    // Bayeux connection negotiation may be iterative and several handshake messages may be exchanged before a successful connection is obtained. Servers may also request Bayeux connection renegotiation by sending an unsuccessful connect response with advice to reconnect with a handshake message.

    // TODO: keep alive, and re-subscribe when reconnected through a different session (different clientId?)

    // TODO: Make thread-safe, or thread-contained.
    public class BayeuxClient
    {
        public string Url { get; }

        public HttpClient HttpClient { get; }

        public string ClientId { get; private set; }

        int nextId = 0;

        public BayeuxClient(HttpClient httpClient, string url)
        {
            HttpClient = httpClient;
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

        class HandshakeResponse
        {
            public string successful;
            public string error;
            public string clientId;
            public Advice advice;
        }

        readonly JsonSerializer jsonSerializer = JsonSerializer.Create();

        // TODO: avoid several handshake requests
        async Task Handshake()
        {
            var httpResponse = await Post(
                new
                {
                    channel = "/meta/handshake",
                    version = "1.0",
                    supportedConnectionTypes = new[] { "long-polling" },
                });

            var responseStream = await httpResponse.Content.ReadAsStreamAsync();
            var responseList = jsonSerializer.Deserialize<List<HandshakeResponse>>(
                new JsonTextReader(new StreamReader(responseStream, Encoding.UTF8)));
            Debug.Assert(responseList.Count == 1);
            var response = responseList[0];

            if (response.successful != "true")
                throw new HandshakeException(response.error);

            ClientId = response.clientId;

            FollowAdvice(response.advice);
        }

        void FollowAdvice(Advice advice)
        {
            if (advice != null)
            {
                // TODO: follow advice
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

        public Task<HttpResponseMessage> Subscribe(string channel)
        {
            return Post(
                new {       
                    clientId = ClientId,
                    channel = "/meta/subscribe",
                    subscription = channel,
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

        public Task<HttpResponseMessage> Disconnect()
        {
            return Post(
                new
                {
                    clientId = ClientId,
                    channel = "/meta/disconnect",
                });
        }

        public Task<HttpResponseMessage> Post(object obj)
        {
            return HttpClient.PostAsync(Url, ToJsonContent(new[] { obj }));
        }

        HttpContent ToJsonContent(object message)
        {
            var result = JsonConvert.SerializeObject(message);
            Debug.WriteLine("HTTP request JSON content:\n" + result);
            return new StringContent(result, Encoding.UTF8, "application/json"); ;
        }
    }
}
