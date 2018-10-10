using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client;
using HttpMock;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Tests
{
    [TestClass]
    public class BayeuxClientTest
    {
        public TestContext TestContext { get; set; }

        string GetTestParam(string key)
        {
            var result = TestContext.Properties[key];
            if (result == null)
                throw new Exception($"TestRunParameter [{key}] not found. (See file test.template.runsettings)");
            return (string)result;
        }

        string BaseURL;
        string APIKey;
        Auth.PasswordGrantTypeCredentials credentials;
        object statistics;

        [TestInitialize]
        public void Init()
        {
            BaseURL = GetTestParam("BaseURL");
            APIKey = GetTestParam("APIKey");

            credentials = new Auth.PasswordGrantTypeCredentials()
            {
                UserName = GetTestParam("UserNamePath") + @"\" + GetTestParam("UserName"),
                Password = GetTestParam("Password"),
                ClientId = GetTestParam("ClientId"),
                ClientSecret = GetTestParam("ClientSecret"),
            };

            // Random int, padded to 3 digits, with leading zeros if needed.
            var id = new Random().Next(0, 1000).ToString("D3");

            statistics = new
            {
                operationId = $"SUBSCRIPTION_ID_{id}",
                data = new
                {
                    statistics = new[]
                    {
                        new
                        {
                            statisticId = $"STATISTIC_ID_{id}_0",
                            definition = new
                            {
                                notificationMode = "Periodical",
                                subject = "DNStatus",
                                insensitivity = 0,
                                category = "CurrentTime",
                                mainMask = "*",
                                notificationFrequency = 5,
                            },
                            objectId = GetTestParam("UserName"),
                            objectType = "Agent"
                        },
                    }
                }
            };
        }

        async Task<HttpClient> InitHttpClient()
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("x-api-key", APIKey);
            var token = await Auth.Authenticate(httpClient, BaseURL, credentials);
            httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            return httpClient;
        }

        BayeuxClient InitStatisticsBayeuxClient(HttpClient httpClient)
        {
            var bayeuxClient = new BayeuxClient(httpClient, BaseURL + "/statistics/v3/notifications");

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            bayeuxClient.ConnectionStateChanged += (e, args) =>
                Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            return bayeuxClient;
        }

        [TestMethod]
        public async Task Stop_without_start()
        {
            var httpClient = await InitHttpClient();
            using (var bayeuxClient = InitStatisticsBayeuxClient(httpClient))
            {
                Debug.WriteLine("Disposing...");
            }
            Debug.WriteLine("Disposed.");
        }

        [TestMethod]
        public async Task Subscribe_statistic()
        {
            var httpClient = await InitHttpClient();

            using (var bayeuxClient = InitStatisticsBayeuxClient(httpClient))
            {
                await bayeuxClient.Start();

                var response = await httpClient.PostAsync(
                    BaseURL + "/statistics/v3/subscriptions?verbose=INFO",
                    new StringContent(
                        JsonConvert.SerializeObject(statistics),
                        Encoding.UTF8,
                        "application/json"));

                var responseContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine("Response to Subscribe: " + responseContent);

                Thread.Sleep(TimeSpan.FromSeconds(1));

                await bayeuxClient.Subscribe("/statistics/v3/updates"); // due to the wait, several events are already received along with the subscribe response
                await bayeuxClient.Subscribe("/statistics/v3/service");

                // I have received the following non-compliant error response from the Statistics API:
                // request: [{"clientId":"256fs7hljxavbz317cdt1d7t882v","channel":"/meta/subscribe","subscription":"/pepe"}]
                // response: {"timestamp":1536851691737,"status":500,"error":"Internal Server Error","message":"java.lang.IllegalArgumentException: Invalid channel id: pepe","path":"/statistics/v3/notifications"}

                Thread.Sleep(TimeSpan.FromSeconds(11));

                await bayeuxClient.Unsubscribe("/statistics/v3/service");
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        [TestMethod]
        [ExpectedException(typeof(BayeuxRequestException))]
        // response: {"timestamp":1536851691737,"status":500,"error":"Internal Server Error",
        // "message":"java.lang.IllegalArgumentException: Invalid channel id: pepe",
        // "path":"/statistics/v3/notifications"}
        public async Task Subscribe_invalid_channel_id()
        {
            var httpClient = await InitHttpClient();
            var bayeuxClient = new BayeuxClient(httpClient, BaseURL + "/statistics/v3/notifications");
            await bayeuxClient.Start();
            await bayeuxClient.Subscribe("pepe");
        }

        [TestMethod]
        [ExpectedException(typeof(BayeuxProtocolException))]
        public async Task Server_responds_with_no_channel()
        {
            const string URI = "http://localhost:9191";
            var serverStub = HttpMockRepository.At(URI);
            serverStub.Stub(x => x.Post(""))
                .Return(JsonConvert.SerializeObject(new { }))
                .OK();

            using (var bayeuxClient = new BayeuxClient(new HttpClient(), URI))
            {
                await bayeuxClient.Start();
            }
        }

        [TestMethod]
        [ExpectedException(typeof(BayeuxProtocolException))]
        public async Task Server_advices_no_reconnect_on_handshake()
        {
            const string URI = "http://localhost:9191";
            var serverStub = HttpMockRepository.At(URI);
            serverStub.Stub(x => x.Post(""))
                .Return(JsonConvert.SerializeObject(
                    new []
                    {
                        new
                        {
                            minimumVersion = "1.0",
                            clientId = "nv8g1psdzxpb9yol3z1l6zvk2p",
                            supportedConnectionTypes = new [] { "long-polling","callback-polling" },
                            advice = new { interval = 0, timeout = 20000, reconnect = "none" /*!*/ },
                            channel = "/meta/handshake",
                            version = "1.0",
                            successful = true,
                        }
                    }))
                .OK();

            using (var bayeuxClient = new BayeuxClient(new HttpClient(), URI))
            {
                await bayeuxClient.Start();
            }
        }

        readonly object normalHandshakeResponse =
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
    }
}
