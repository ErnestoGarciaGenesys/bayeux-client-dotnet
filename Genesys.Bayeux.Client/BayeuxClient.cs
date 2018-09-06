using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    public class BayeuxClient
    {
        public string BaseUrl { get; }

        public HttpClient HttpClient { get; }

        public string ClientId { get; private set; }

        int nextId = 0;

        public BayeuxClient(HttpClient httpClient, string baseUrl)
        {
            HttpClient = httpClient;
            BaseUrl = baseUrl;
        }

        // The "id" field is optional, so this may be not needed
        int GenerateId()
        {
            return nextId++;
        }

        public async Task Handshake()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/statistics/v3/notifications")
            {
                Content = ToJsonContent(
                    new
                    {
                        id = GenerateId(),
                        channel = "/meta/handshake",
                        supportedConnectionTypes = new[] { "long-polling" },
                    })
            };

            var httpResponse = await HttpClient.SendAsync(request);
            var httpResponseContent = await httpResponse.Content.ReadAsStringAsync();
            Debug.WriteLine(httpResponseContent);

            // If not empty or a JSON array, this will throw a Newtonsoft.Json.JsonSerializationException.
            var response = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(httpResponseContent);
            foreach (var message in response)
            {
                message.TryGetValue("clientId", out object clientIdObj);
                if (clientIdObj is string clientId)
                    ClientId = clientId;
            }
        }

        public event EventHandler<string> MessagesReceived;
        
        public void StartLongPolling()
        {
            Poll().ContinueWith(task =>
            {
                Debug.WriteLine("Poll ended.");
                if (!task.IsFaulted)
                {
                    task.Result.Content.ReadAsStringAsync().ContinueWith(
                        s => MessagesReceived?.Invoke(this, s.Result));
                }

                StartLongPolling();
            });
        }

        public async Task<HttpResponseMessage> Subscribe(string channel)
        {
            return await HttpClient.PostAsync(
                BaseUrl + "/statistics/v3/notifications",
                ToJsonContent(
                    new {       
                        clientId = ClientId,
                        channel = "/meta/subscribe",
                        id = GenerateId(),
                        subscription = channel,
                    }));
        }

        public async Task<HttpResponseMessage> Poll()
        {
            Debug.WriteLine("Polling...");
            return await HttpClient.PostAsync(
                BaseUrl + "/statistics/v3/notifications",
                ToJsonContent(
                    new
                    {
                        clientId = ClientId,
                        channel = "/meta/connect",
                        id = GenerateId(),
                        connectionType = "long-polling",
                    }));
        }

        public async Task<HttpResponseMessage> Disconnect()
        {
            return await HttpClient.PostAsync(
                BaseUrl + "/statistics/v3/notifications",
                ToJsonContent(
                    new
                    {
                        clientId = ClientId,
                        channel = "/meta/disconnect",
                        id = GenerateId(),
                    }));
        }

        HttpContent ToJsonContent(object message)
        {
            var result = JsonConvert.SerializeObject(message);
            Debug.WriteLine("HTTP request JSON content:\n" + result);
            return new StringContent(result, Encoding.UTF8, "application/json"); ;
        }
    }
}
