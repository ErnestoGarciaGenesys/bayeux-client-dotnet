using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client;
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
            return (string) result;
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
                                notificationFrequency = 3,
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

        [TestMethod]
        public async Task Subscribe_statistic()
        {
            var httpClient = await InitHttpClient();
            var bayeuxClient = new BayeuxClient(httpClient, BaseURL + "/statistics/v3/notifications");
            await bayeuxClient.Start();

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            var response = await httpClient.PostAsync(
                BaseURL + "/statistics/v3/subscriptions?verbose=INFO",
                new StringContent(
                    JsonConvert.SerializeObject(statistics),
                    Encoding.UTF8,
                    "application/json"));

            var responseContent = await response.Content.ReadAsStringAsync();
            Debug.WriteLine("Response to Subscribe: " + responseContent);

            Thread.Sleep(TimeSpan.FromSeconds(5));

            await bayeuxClient.Subscribe("/statistics/v3/updates"); // due to the wait, several events are already received along with the subscribe response
            await bayeuxClient.Subscribe("/statistics/v3/service");

            Thread.Sleep(TimeSpan.FromSeconds(10));

            await bayeuxClient.Unsubscribe("/statistics/v3/service");

            // TODO: bayeuxClient.Dispose();
        }

        [TestMethod]
        [ExpectedException(typeof(BayeuxRequestFailedException))]
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
    }
}
