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
                                notificationFrequency = 5,
                            },
                            objectId = GetTestParam("UserName"),
                            objectType = "Agent"
                        },
                    }
                }
            };
        }

        [TestMethod]
        public async Task Subscribe_statistic()
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("x-api-key", APIKey);
            var token = await Auth.Authenticate(httpClient, BaseURL, credentials);
            httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

            var bayeuxClient = new BayeuxClient(httpClient, BaseURL);
            await bayeuxClient.Handshake();
            await bayeuxClient.Subscribe("/statistics/v3/service");
            await bayeuxClient.Subscribe("/statistics/v3/updates");
            
            var response = await httpClient.PostAsync(
                "https://gapi-use1.genesyscloud.com/statistics/v3/subscriptions?verbose=INFO",
                new StringContent(
                    JsonConvert.SerializeObject(statistics),
                    Encoding.UTF8,
                    "application/json"));

            var responseContent = await response.Content.ReadAsStringAsync();
            Debug.WriteLine("Response to Subscribe: " + responseContent);

            bayeuxClient.MessagesReceived += (e, s) =>
                Debug.WriteLine("Messages received: " + s);

            bayeuxClient.StartLongPolling();

            Thread.Sleep(TimeSpan.FromMinutes(1));
        }

        [TestMethod]
        public void Check_JSON_deserialization()
        {
            //var json = "[{\"hello\":\"world\"}]"; // OK
            //var json = ""; // OK. Returns null
            var json = "{\"hello\":\"world\"}"; // throws exception, as expected
            var result = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
            Debug.WriteLine(result);
        }
    }
}
