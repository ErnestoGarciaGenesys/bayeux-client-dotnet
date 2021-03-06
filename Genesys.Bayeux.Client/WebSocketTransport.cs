﻿using System;
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
    internal class WebSocketTransport : IBayeuxTransport
    {
        static readonly ILog log = BayeuxClient.log;

        readonly Func<WebSocket> webSocketFactory;
        readonly Uri uri;
        readonly TimeSpan responseTimeout;
        Action<IEnumerable<JObject>> eventPublisher;

        WebSocket webSocket;
        Task receiverLoopTask;
        CancellationTokenSource receiverLoopCancel;

        readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> pendingResponsePromises = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        long nextMessageId = 0;

        public WebSocketTransport(Func<WebSocket> webSocketFactory, Uri uri, TimeSpan responseTimeout)
        {
            this.webSocketFactory = webSocketFactory;
            this.uri = uri;
            this.responseTimeout = responseTimeout;
        }

        public void SetEventPublisher(Action<IEnumerable<JObject>> eventPublisher)
        {
            this.eventPublisher = eventPublisher;
        }

        public void Dispose()
        {
            ClearPendingRequests();

            if (receiverLoopCancel != null)
                receiverLoopCancel.Cancel();

            if (webSocket != null)
            {
                try
                {
                    _ = webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (Exception e)
                {
                    log.WarnException("WebSocket close failed", e);
                    // Nothing else to try.
                }

                webSocket.Dispose();
            }
        }

        public async Task Open(CancellationToken cancellationToken)
        {
            if (receiverLoopCancel != null)
            {
                receiverLoopCancel.Cancel();
                await receiverLoopTask;
            }

            if (webSocket != null)
                webSocket.Dispose();

            webSocket = webSocketFactory();

            try
            {
                await webSocket.ConnectAsync(uri, cancellationToken);
            }
            catch (Exception e)
            {
                throw new BayeuxTransportException("WebSocket connect failed.", e, transportClosed: true);
            }

            receiverLoopCancel = new CancellationTokenSource();
            receiverLoopTask = StartReceiverLoop(receiverLoopCancel.Token);
        }

        async Task StartReceiverLoop(CancellationToken cancelToken)
        {
            Exception fault;

            try
            {
                while (!cancelToken.IsCancellationRequested)
                    HandleReceivedMessage(await ReceiveMessage(cancelToken));

                fault = null;
            }
            catch (OperationCanceledException)
            {
                fault = null;
            }
            catch (WebSocketException e)
            {
                // It is not possible to infer whether the webSocket is closed from webSocket.State,
                // and not clear how to infer it from WebSocketException. So we always assume that it is closed.
                fault = new BayeuxTransportException("WebSocket receive message failed. Connection assumed closed.", e, transportClosed: true);
            }
            catch (Exception e)
            {
                log.ErrorException("Unexpected exception thrown in WebSocket receiving loop", e);
                fault = new BayeuxTransportException("Unexpected exception. Connection assumed closed.", e, transportClosed: true);
            }

            ClearPendingRequests(fault);
        }

        void ClearPendingRequests(Exception fault = null)
        {
            if (fault == null)
            {
                foreach (var r in pendingResponsePromises)
                    r.Value.SetCanceled();
            }
            else
            {
                foreach (var r in pendingResponsePromises)
                    r.Value.SetException(fault);
            }

            pendingResponsePromises.Clear();
        }

        async Task<Stream> ReceiveMessage(CancellationToken cancellationToken)
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            var stream = new MemoryStream();
            WebSocketReceiveResult result = null;
            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                stream.Write(buffer.Array, buffer.Offset, result.Count);
            }
            while (!result.EndOfMessage);

            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        void HandleReceivedMessage(Stream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var received = JToken.ReadFrom(new JsonTextReader(reader));
                log.Debug(() => $"Received: {received.ToString(Formatting.None)}");

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
                        var found = pendingResponsePromises.TryRemove(messageId, out var requestTask);

                        if (found)
                            requestTask.SetResult(response);
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
            var responsePromises = new List<TaskCompletionSource<JObject>>();
            var requestsJArray = JArray.FromObject(requests);
            var messageIds = new List<string>();
            foreach (var request in requestsJArray)
            {
                var messageId = Interlocked.Increment(ref nextMessageId).ToString();
                request["id"] = messageId;
                messageIds.Add(messageId);

                var responsePromise = new TaskCompletionSource<JObject>();
                pendingResponsePromises.TryAdd(messageId, responsePromise);
                responsePromises.Add(responsePromise);
            }
            
            var messageStr = JsonConvert.SerializeObject(requestsJArray);
            log.Debug(() => $"Posting: {messageStr}");
            await SendAsync(messageStr, cancellationToken);

            var timeoutTask = Task.Delay(responseTimeout, cancellationToken);
            Task completedTask = await Task.WhenAny(
                Task.WhenAll(responsePromises.Select(t => t.Task)),
                timeoutTask);

            foreach (var id in messageIds)
                pendingResponsePromises.TryRemove(id, out var _);

            if (completedTask == timeoutTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            else
            {
                return await responsePromises.First().Task;
            }
        }

        public async Task SendAsync(string message, CancellationToken cancellationToken)
        {
            var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
            try
            {
                await webSocket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                throw new BayeuxTransportException("WebSocket send failed.", e, transportClosed: webSocket.State != WebSocketState.Open);
            }
        }
    }
}