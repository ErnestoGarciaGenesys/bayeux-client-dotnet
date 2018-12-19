using Genesys.Bayeux.Client.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    /// <summary>
    /// Abstraction for any HTTP client implementation.
    /// Enables the implementation of retry policies, useful for servers that may occasionally need a session refresh. Retries are general not supported by HttpClient, as, for some versions, SendAsync disposes the content of HttpRequestMessage. This means that a failed SendAsync call can't be retried, as the HttpRequestMessage can't be reused.
    /// </summary>
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

    class HttpTransport
    {
        static readonly ILog log = BayeuxClient.log;

        readonly IHttpPost httpPost;
        readonly string url;
        readonly Action<IEnumerable<JObject>> eventPublisher;

        public HttpTransport(
            IHttpPost httpPost, 
            string url,
            Action<IEnumerable<JObject>> eventPublisher)
        {
            this.httpPost = httpPost;
            this.url = url;
            this.eventPublisher = eventPublisher;
        }

        public async Task<JObject> Request(IEnumerable<object> requests, CancellationToken cancellationToken)
        {
            var messageStr = JsonConvert.SerializeObject(requests);
            log.Debug($"Posting: {messageStr}");
            var httpResponse = await httpPost.PostAsync(url, messageStr, cancellationToken);

            // As a stream it could have better performance, but logging is easier with strings.
            var responseStr = await httpResponse.Content.ReadAsStringAsync();
            log.Debug($"Received: {responseStr}");
            httpResponse.EnsureSuccessStatusCode();

            var responseToken = JToken.Parse(responseStr);
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
