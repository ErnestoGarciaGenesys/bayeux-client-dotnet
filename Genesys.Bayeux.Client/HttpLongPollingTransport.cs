using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    internal static class HttpClientExtensions
    {
        public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient httpClient, string requestUri, string jsonContent, CancellationToken cancellationToken)
        {
            StringContent content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            return httpClient.PostAsync(requestUri, content, cancellationToken);
        }
    }

    class HttpLongPollingTransport : IBayeuxTransport
    {
        static readonly ILog log = BayeuxClient.log;

        readonly HttpClient httpClient;
        readonly string url;
        readonly Func<IEnumerable<JObject>, CancellationToken, Task> eventPublisher;

        public HttpLongPollingTransport(
            HttpClient httpClient,
            string url,
            Func<IEnumerable<JObject>, CancellationToken, Task> eventPublisher)
        {
            this.httpClient = httpClient;
            this.url = url;
            this.eventPublisher = eventPublisher;
        }

        public void Dispose() { }

        public Task OpenAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task<JObject> RequestAsync(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            var messageStr = JsonConvert.SerializeObject(requests);
            log.Debug(() => $"Posting: {messageStr}");

            var httpResponse = await httpClient.PostJsonAsync(url, messageStr, cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var responseStr = await httpResponse.Content.ReadAsStringAsync();
                log.Debug(() => $"Received: {responseStr}");
            }

            httpResponse.EnsureSuccessStatusCode();

            var responseToken = JToken.ReadFrom(new JsonTextReader(new StreamReader(await httpResponse.Content.ReadAsStreamAsync())));
            log.Debug(() => $"Received: {responseToken.ToString(Formatting.None)}");

            IEnumerable<JToken> tokens = responseToken is JArray ?
                (IEnumerable<JToken>)responseToken :
                new[] { responseToken };

            // https://docs.cometd.org/current/reference/#_delivery
            // Event messages MAY be sent to the client in the same HTTP response
            // as any other message other than a /meta/handshake response.
            JObject responseObj = null;
            var events = new List<JObject>();

            foreach (var token in tokens)
            {
                JObject message = (JObject)token;
                var channel = (string)message["channel"];

                if (channel == null)
                    throw new BayeuxProtocolException("No 'channel' field in message.");

                if (channel.StartsWith("/meta/"))
                    responseObj = message;
                else
                    events.Add(message);
            }

            await eventPublisher(events, cancellationToken);

            return responseObj;
        }
    }
}
