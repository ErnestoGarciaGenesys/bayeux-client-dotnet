using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesys.Bayeux.Client
{
    // TODO: Make internal
    public class WebSocketTransport
    {
        static readonly ILog log = BayeuxClient.log;

        readonly string url;
        readonly WebSocket webSocket;
        readonly Action<IEnumerable<JObject>> eventPublisher;

        readonly ConcurrentDictionary<string, Action<JObject>> pendingRequests = new ConcurrentDictionary<string, Action<JObject>>();

        long nextMessageId = 0;

        public WebSocketTransport(WebSocket webSocket, string url, Action<IEnumerable<JObject>> eventPublisher)
        {
            this.url = url;
            this.webSocket = webSocket;
            this.eventPublisher = eventPublisher;
        }

        public async Task InitAsync(CancellationToken cancellationToken)
        {
            await webSocket.ConnectAsync(new Uri(url), cancellationToken);
            StartReceivingLoop(webSocket, cancellationToken);
        }

        async void StartReceivingLoop(WebSocket webSocket, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var responses = await ReceiveJsonMessage(webSocket, cancellationToken);

                    foreach (var response in responses)
                    {
                        var messageId = (string)response["id"];
                        if (messageId == null)
                        {
                            eventPublisher(new[] { response });
                        }
                        else
                        {
                            pendingRequests.TryRemove(messageId, out var action);
                            action?.Invoke(response);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log.FatalException("Exception thrown in WebSocket receiving loop", e);
            }
        }

        public async Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            var responseTasks = new List<Task<JObject>>();
            var requestsJArray = JArray.FromObject(requests);
            foreach (var request in requestsJArray)
            {
                var messageId = Interlocked.Increment(ref nextMessageId).ToString();
                request["id"] = messageId;

                var responseReceived = new TaskCompletionSource<JObject>();
                pendingRequests.TryAdd(messageId, response => responseReceived.SetResult(response));
                responseTasks.Add(responseReceived.Task);
            }
            
            var messageStr = JsonConvert.SerializeObject(requestsJArray);
            log.Debug($"Posting: {messageStr}");
            await webSocket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(messageStr)),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            //foreach (var response in responseTasks)
            //{

            //}

            return await responseTasks.First();
        }

        public static async Task<IEnumerable<JObject>> ReceiveJsonMessage(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);

            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult result = null;
                do
                {
                    result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                    stream.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                stream.Seek(0, SeekOrigin.Begin);

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var received = JToken.ReadFrom(new JsonTextReader(reader));

                    log.Debug($"Received: {received}");

                    if (received is JObject)
                        return new[] { (JObject)received };
                    else
                        return ((JArray)received).Children().Cast<JObject>();
                }
            }
        }
    }
}