using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;

namespace Genesys.Bayeux.Client.Tests
{
    [TestClass]
    public class BayeuxClientTest
    {
        [TestMethod]
        public void Dispose_without_start()
        {
            using (var bayeuxClient = CreateHttpBayeuxClient())
            {
                Debug.WriteLine("Disposing...");
            }
            Debug.WriteLine("Disposed.");
        }

        [TestMethod]
        [ExpectedException(typeof(BayeuxProtocolException))]
        public async Task Server_response_without_channel()
        {
            var mock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mock.Object);

            mock.Protected().As<IHttpMessageHandlerProtected>()
                .Setup(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildBayeuxResponse(new { }));

            using (var bayeuxClient = CreateHttpBayeuxClient(httpClient))
            {
                await bayeuxClient.StartAsync();
            }
        }

        [TestMethod]
        public async Task Server_advices_no_reconnect_on_handshake()
        {
            var mock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mock.Object);

            mock.Protected().As<IHttpMessageHandlerProtected>()
                .SetupSequence(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildBayeuxResponse(
                    new
                    {
                        minimumVersion = "1.0",
                        clientId = "nv8g1psdzxpb9yol3z1l6zvk2p",
                        supportedConnectionTypes = new[] { "long-polling", "callback-polling" },
                        advice = new
                        {
                            interval = 0,
                            timeout = 20000,
                            reconnect = "none" // !!!
                        },
                        channel = "/meta/handshake",
                        version = "1.0",
                        successful = true,
                    }))
                .ReturnsAsync(BuildBayeuxResponse(
                    new
                    {
                        channel = "/meta/disconnect",
                        successful = true,
                    }));

            using (var bayeuxClient = CreateHttpBayeuxClient(httpClient))
            {
                await bayeuxClient.StartAsync();
            }

            mock.Protected().As<IHttpMessageHandlerProtected>()
                .Verify(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()),
                times: Times.Exactly(2));
        }

        [TestMethod]
        public async Task Reconnections()
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected().As<IHttpMessageHandlerProtected>()
                .SetupSequence(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildBayeuxResponse(successfulHandshakeResponse))
                .ReturnsAsync(BuildBayeuxResponse(successfulConnectResponse))
                .ReturnsAsync(BuildBayeuxResponse(rehandshakeConnectResponse))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ReturnsAsync(BuildBayeuxResponse(successfulHandshakeResponse))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ReturnsIndefinitely(() =>
                  Task.Delay(TimeSpan.FromSeconds(5))
                      .ContinueWith(t => BuildBayeuxResponse(successfulHandshakeResponse)))
                ;

            var bayeuxClient = new BayeuxClient(
                new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = Url },
                reconnectDelays: new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) });

            using (bayeuxClient)
            {
                bayeuxClient.StartAsync();
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }

        [TestMethod]
        public async Task Reconnection_when_started_in_background()
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected().As<IHttpMessageHandlerProtected>()
                .SetupSequence(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("mock raising exception"))
                .ReturnsAsync(BuildBayeuxResponse(successfulHandshakeResponse))
                .ReturnsAsync(BuildBayeuxResponse(successfulConnectResponse))
                .ReturnsIndefinitely(() =>
                  Task.Delay(TimeSpan.FromSeconds(5))
                      .ContinueWith(t => BuildBayeuxResponse(successfulConnectResponse)))
                ;

            var bayeuxClient = new BayeuxClient(
                new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = Url },
                reconnectDelays: new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) });

            using (bayeuxClient)
            {
                bayeuxClient.StartInBackgroundAsync();
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }

        // TODO: test ConnectionStateChangedEvents

        [TestMethod]
        public async Task Automatic_subscription()
        {
            var mock = new Mock<HttpMessageHandler>();
            var mockProtected = mock.Protected().As<IHttpMessageHandlerProtected>();

            int subscriptionCount = 0;

            mockProtected
                .Setup(h => h.SendAsync(MatchSubscriptionRequest(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                    Task.Run(() => subscriptionCount++)
                        .ContinueWith(t => BuildBayeuxResponse(successfulSubscriptionResponse)));

            mockProtected
                .Setup(h => h.SendAsync(MatchHandshakeRequest(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildBayeuxResponse(successfulHandshakeResponse));

            mockProtected
                .Setup(h => h.SendAsync(MatchConnectRequest(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                    Task.Delay(TimeSpan.FromSeconds(5))
                        .ContinueWith(t => BuildBayeuxResponse(successfulConnectResponse)));

            var bayeuxClient = new BayeuxClient(
                new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = Url },
                reconnectDelays: new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) });

            using (bayeuxClient)
            {
                await bayeuxClient.AddSubscriptionsAsync("/mychannel");
                bayeuxClient.StartAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            Assert.AreEqual(1, subscriptionCount);
        }

        HttpRequestMessage MatchSubscriptionRequest() => MatchRequestContains("/meta/subscribe");
        HttpRequestMessage MatchHandshakeRequest() => MatchRequestContains("/meta/handshake");
        HttpRequestMessage MatchConnectRequest() => MatchRequestContains("/meta/connect");

        HttpRequestMessage MatchRequestContains(string s) =>
            Match.Create((HttpRequestMessage request) =>
                request.Content.ReadAsStringAsync().Result.Contains(s));



        const string Url = "http://testing.net/";

        static readonly object successfulHandshakeResponse =
            new
            {
                minimumVersion = "1.0",
                clientId = "nv8g1psdzxpb9yol3z1l6zvk2p",
                supportedConnectionTypes = new[] { "long-polling", "callback-polling" },
                advice = new { interval = 0, timeout = 20000, reconnect = "retry" },
                channel = "/meta/handshake",
                version = "1.0",
                successful = true,
            };

        static readonly object successfulConnectResponse =
            new
            {
                channel = "/meta/connect",
                successful = true,
            };

        static readonly object successfulSubscriptionResponse =
            new
            {
                channel = "/meta/subscribe",
                successful = true,
            };

        // real re-handshake advice, when too much time has passed without polling:
        // [{"advice":{"interval":0,"reconnect":"handshake"},"channel":"/meta/connect","error":"402::Unknown client","successful":false}]
        static readonly object rehandshakeConnectResponse =
            new
            {
                channel = "/meta/connect",
                successful = false,
                error = "402::Unknown client",
                advice = new { interval = 0, reconnect = "handshake" },
            };

        //static readonly object eventMessage =
        //    new
        //    {
        //        channel = "/test",
        //        data = new { key = "event data" },
        //    };

        static HttpResponseMessage BuildBayeuxResponse(params object[] messages)
         => new HttpResponseMessage()
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonConvert.SerializeObject(messages)),
            };

        interface IHttpMessageHandlerProtected
        {
            Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public async Task Subscribe_throws_exception_when_not_connected()
        {
            var mock = new Mock<HttpMessageHandler>();
            var bayeuxClient = new BayeuxClient(new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = "none" });
            await bayeuxClient.SubscribeAsync("dummy");
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidOperationException))]
        public async Task Unsubscribe_throws_exception_when_not_connected()
        {
            var mock = new Mock<HttpMessageHandler>();
            var bayeuxClient = new BayeuxClient(new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = "none" });
            await bayeuxClient.UnsubscribeAsync("dummy");
        }

        [TestMethod]
        public async Task AddSubscriptions_succeeds_when_not_connected()
        {
            var mock = new Mock<HttpMessageHandler>();
            var bayeuxClient = new BayeuxClient(new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = "none" });
            await bayeuxClient.AddSubscriptionsAsync("dummy");
        }

        [TestMethod]
        public async Task RemoveSubscriptions_succeeds_when_not_connected()
        {
            var mock = new Mock<HttpMessageHandler>();
            var bayeuxClient = new BayeuxClient(new HttpLongPollingTransportOptions() { HttpClient = new HttpClient(mock.Object), Uri = "none" });
            await bayeuxClient.RemoveSubscriptionsAsync("dummy");
        }

        BayeuxClient CreateHttpBayeuxClient(HttpClient httpClient = null, string uri = Url)
        {
            return new BayeuxClient(new HttpLongPollingTransportOptions()
            {
                HttpClient = httpClient,
                Uri = uri,
            });
        }
    }
}
