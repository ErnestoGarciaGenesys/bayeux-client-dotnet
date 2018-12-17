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

        [TestMethod]
        public async Task Run_for_a_while_using_WebSocket()
        {
            using (var webSocket = SystemClientWebSocket.CreateClientWebSocket())
            {
                var transport = new WebSocketTransport(webSocket, "ws://localhost:8080/bayeux/",
                    events =>
                    {
                        Debug.WriteLine($"Events received: {events}");
                    });

                await transport.InitAsync(CancellationToken.None);

                var response = await transport.Request(new[]
                {
                    new
                    {
                        channel = "/meta/handshake",
                        version = "1.0",
                        supportedConnectionTypes = new[] { "websocket" },
                    },
                }, CancellationToken.None);

                var currentConnection = (string)response["clientId"];

                var subscribeResponse = await transport.Request(new[]
                {
                    new
                    {
                        clientId = currentConnection,
                        channel = "/meta/subscribe",
                        subscription = "/test",
                    },
                }, CancellationToken.None);

                var connectResponse = await transport.Request(new[]
                {
                    new
                    {
                        clientId = currentConnection,
                        channel = "/meta/connect",
                        connectionType = "websocket",
                    },
                }, CancellationToken.None);

                var connectResponse2 = await transport.Request(new[]
                {
                    new
                    {
                        clientId = currentConnection,
                        channel = "/meta/connect",
                        connectionType = "websocket",
                    },
                }, CancellationToken.None);

                await Task.Delay(TimeSpan.FromSeconds(60));
            }

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
    }
}
