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
    internal class WebSocketTransport : IDisposable
    {
        static readonly ILog log = BayeuxClient.log;

        readonly WebSocketChannel channel;
        readonly TimeSpan responseTimeout;
        readonly Action<IEnumerable<JObject>> eventPublisher;

        readonly ConcurrentDictionary<string, Action<JObject>> pendingRequests = new ConcurrentDictionary<string, Action<JObject>>();
        long nextMessageId = 0;

        public WebSocketTransport(WebSocket webSocket, string url, TimeSpan responseTimeout, Action<IEnumerable<JObject>> eventPublisher)
        {
            channel = new WebSocketChannel(webSocket, url, OnMessageReceived);
            this.responseTimeout = responseTimeout;
            this.eventPublisher = eventPublisher;
        }

        public void Dispose()
        {
            channel.Dispose();
        }

        public async Task InitAsync(CancellationToken cancellationToken)
        {
            await channel.Start(cancellationToken);
        }

        void OnMessageReceived(Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var received = JToken.ReadFrom(new JsonTextReader(reader));

                log.Debug($"Received: {received}");

                var responses = received is JObject ?
                    new[] { (JObject)received } :
                    ((JArray)received).Children().Cast<JObject>();

                var events = new List<JObject>();
                foreach (var response in responses)
                {
                    var messageId = (string)response["id"];
                    if (messageId == null)
                    {
                        events.Add(response);
                    }
                    else
                    {
                        var found = pendingRequests.TryRemove(messageId, out var action);

                        if (found)
                            action(response);
                        else
                            log.Error($"Request not found for received response with id '{messageId}'");
                    }
                }

                if (events.Count > 0)
                    eventPublisher(events);
            }
        }

        public async Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            var responseTasks = new List<TaskCompletionSource<JObject>>();
            var requestsJArray = JArray.FromObject(requests);
            var messageIds = new List<string>();
            foreach (var request in requestsJArray)
            {
                var messageId = Interlocked.Increment(ref nextMessageId).ToString();
                request["id"] = messageId;
                messageIds.Add(messageId);

                var responseReceived = new TaskCompletionSource<JObject>();
                pendingRequests.TryAdd(messageId, response => responseReceived.SetResult(response));
                responseTasks.Add(responseReceived);
            }
            
            var messageStr = JsonConvert.SerializeObject(requestsJArray);
            log.Debug($"Posting: {messageStr}");
            await channel.SendAsync(messageStr, cancellationToken);

            var timeoutTask = Task.Delay(responseTimeout, cancellationToken);
            Task completedTask = await Task.WhenAny(
                Task.WhenAll(responseTasks.Select(t => t.Task)),
                timeoutTask);

            foreach (var id in messageIds)
                pendingRequests.TryRemove(id, out var _);

            if (completedTask == timeoutTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            else
            {
                return responseTasks.First().Task.Result;
            }
        }
    }
}