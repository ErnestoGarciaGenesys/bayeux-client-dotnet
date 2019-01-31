using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Genesys.Bayeux.Client.Tests
{
    [TestClass]
    public class RealLocalTest
    {
        [TestMethod]
        public async Task Run_for_a_while_using_HTTP()
        {
            // Begin: README example
            var httpClient = new HttpClient();
            var bayeuxClient = new BayeuxClient(
                new HttpLongPollingTransportOptions()
                {
                    HttpClient = httpClient,
                    Uri = "http://localhost:8080/bayeux/",
                });

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            bayeuxClient.ConnectionStateChanged += (e, args) =>
                Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            await bayeuxClient.AddSubscriptionsAsync("/**");

            await bayeuxClient.StartAsync();
            // End: README example

            using (bayeuxClient)
            {
                await Delay(60);
            }
        }

        [TestMethod]
        public async Task Run_for_a_while_using_WebSocket()
        {
            // Begin: README example
            var bayeuxClient = new BayeuxClient(
                new WebSocketTransportOptions()
                {
                    Uri = new Uri("ws://localhost:8080/bayeux/"),
                });
            // End: README example

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            bayeuxClient.ConnectionStateChanged += (e, args) =>
                Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            await bayeuxClient.AddSubscriptionsAsync("/**");

            await bayeuxClient.StartAsync();

            using (bayeuxClient)
            {
                await Delay(60);
            }
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
