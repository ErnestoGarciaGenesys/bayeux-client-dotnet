using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Tests
{
    [TestClass]
    public class RealLocalTest
    {
        const string url = "http://localhost:8080/bayeux/";

        [TestMethod]
        public async Task Run_for_a_while_using_HTTP()
        {
            var httpClient = new HttpClient();
            var bayeuxClient = new BayeuxClient(httpClient, url);

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            bayeuxClient.ConnectionStateChanged += (e, args) =>
                Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            bayeuxClient.AddSubscriptions("/**");

            await bayeuxClient.Start();

            using (bayeuxClient)
            {
                Thread.Sleep(TimeSpan.FromSeconds(60));
            }
        }

        class Context : IBayeuxClientContext
        {
            readonly WebSocketTransport wsTransport;

            public Context(WebSocketTransport wsTransport)
            {
                this.wsTransport = wsTransport;
            }

            public Task Reopen(CancellationToken cancellationToken)
            {
                return wsTransport.Open(cancellationToken);
            }

            public Task<JObject> Request(object request, CancellationToken cancellationToken)
            {
                return wsTransport.Request(new[] { request }, cancellationToken);
            }

            public Task<JObject> RequestMany(IEnumerable<object> requests, CancellationToken cancellationToken)
            {
                return wsTransport.Request(requests, cancellationToken);
            }

            public void SetConnection(BayeuxConnection newConnection)
            {
                Debug.WriteLine("New connection");

                newConnection.DoSubscription(new[] { "/test" }, new string[] { }, CancellationToken.None);
            }

            public void SetConnectionState(BayeuxClient.ConnectionState newState)
            {
                Debug.WriteLine($"New connection state: {newState}");
            }
        }

        [TestMethod]
        public async Task Run_for_a_while_using_WebSocket()
        {
            using (var transport = new WebSocketTransport(
                webSocketFactory: () => SystemClientWebSocket.CreateClientWebSocket(),
                uri: new Uri("ws://localhost:5088/bayeux/"),
                responseTimeout: TimeSpan.FromSeconds(30),
                eventPublisher: events =>
                {
                    foreach (var ev in events)
                        Debug.WriteLine($"Event received: {ev}");
                }))
            {
                await transport.Open(CancellationToken.None);

                using (var connectLoop = new ConnectLoop("websocket", null, new Context(transport)))
                {
                    await connectLoop.Start(CancellationToken.None);

                    await Delay(120);
                    
                    Debug.WriteLine("End");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            //    var bayeuxClient = new BayeuxClient(url);

            //bayeuxClient.EventReceived += (e, args) =>
            //    Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            //bayeuxClient.ConnectionStateChanged += (e, args) =>
            //    Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            //bayeuxClient.AddSubscriptions("/**");

            //await bayeuxClient.Start();

            //using (bayeuxClient)
            //{
            //    Thread.Sleep(TimeSpan.FromSeconds(60));
            //}
        }

        private class BayeuxClientConfiguration
        {
            public BayeuxClientConfiguration()
            {
            }

            public object EventTaskScheduler { get; set; }
        }


        [TestMethod]
        public async Task Open_and_close_WebSocket()
        {
            using (var webSocket = SystemClientWebSocket.CreateClientWebSocket())
            {
                Debug.WriteLine("Connecting");
                await webSocket.ConnectAsync(new Uri("ws://localhost:5088/bayeux/"), CancellationToken.None);
                await Delay(10);
                //Debug.WriteLine("Sending");
                //await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hola")), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: CancellationToken.None);
                //await Delay(10);
                Debug.WriteLine("Closing");
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                await Delay(5);
                Debug.WriteLine("Connecting again");
                await webSocket.ConnectAsync(new Uri("ws://localhost:5088/bayeux/"), CancellationToken.None);
                await Delay(5);
                Debug.WriteLine("End");
            }
        }

        async Task Delay(int seconds)
        {
            Debug.WriteLine($"Waiting {seconds}s...");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
    }
}
