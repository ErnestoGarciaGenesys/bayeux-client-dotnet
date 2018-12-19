using Genesys.Bayeux.Client.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    class WebSocketChannel : IDisposable
    {
        static readonly ILog log = BayeuxClient.log;

        readonly WebSocket webSocket;
        readonly string url;
        readonly Action<Stream> messageReceived;
        readonly CancellationTokenSource receivingLoopCancellation = new CancellationTokenSource();

        public WebSocketChannel(WebSocket webSocket, string url, Action<Stream> messageReceived)
        {
            this.webSocket = webSocket;
            this.url = url;
            this.messageReceived = messageReceived;
        }

        public void Dispose()
        {
            receivingLoopCancellation.Cancel();
            webSocket.Dispose();
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            await webSocket.ConnectAsync(new Uri(url), cancellationToken);
            StartReceivingLoop(webSocket, receivingLoopCancellation.Token);
        }

        async void StartReceivingLoop(WebSocket webSocket, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                    messageReceived(await ReceiveMessage(webSocket, cancellationToken));
            }
            catch (Exception e)
            {
                log.FatalException("Exception thrown in WebSocket receiving loop", e);
            }
        }

        static async Task<Stream> ReceiveMessage(WebSocket webSocket, CancellationToken cancellationToken)
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

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            return webSocket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
    }
}
