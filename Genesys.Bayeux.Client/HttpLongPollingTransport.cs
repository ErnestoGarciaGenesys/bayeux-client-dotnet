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
    public interface IHttpPost
    {
        Task<HttpResponseMessage> PostAsync(string requestUri, string jsonContent, CancellationToken cancellationToken);
    }

    public class HttpClientHttpPost : IHttpPost
    {
        readonly HttpClient httpClient;

        public HttpClientHttpPost(HttpClient httpClient)
        {
            this.httpClient = httpClient;
        }

        public Task<HttpResponseMessage> PostAsync(string requestUri, string jsonContent, CancellationToken cancellationToken)
        {
            return httpClient.PostAsync(
                requestUri,
                new StringContent(jsonContent, Encoding.UTF8, "application/json"),
                cancellationToken);
        }
    }

    class HttpLongPollingTransport : IBayeuxTransport
    {
        static readonly ILog log = BayeuxClient.log;

        readonly IHttpPost httpPost;
        readonly string url;
        readonly Action<IEnumerable<JObject>> eventPublisher;

        public HttpLongPollingTransport(
            IHttpPost httpPost, 
            string url,
            Action<IEnumerable<JObject>> eventPublisher)
        {
            this.httpPost = httpPost;
            this.url = url;
            this.eventPublisher = eventPublisher;
        }

        public void Dispose() { }

        public Task Open(CancellationToken cancellationToken)
            => Task.FromResult(0);

        public async Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            var messageStr = JsonConvert.SerializeObject(requests);
            log.Debug(() => $"Posting: {messageStr}");

            var httpResponse = await httpPost.PostAsync(url, messageStr, cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var responseStr = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                log.Debug(() => $"Received: {responseStr}");
            }

            httpResponse.EnsureSuccessStatusCode();

            var responseToken = JToken.ReadFrom(new JsonTextReader(new StreamReader(await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))));
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

            eventPublisher(events);

            return responseObj;
        }
    }
}
