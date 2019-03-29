using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;

namespace Genesys.Bayeux.Client
{
    public class HttpLongPollingTransportOptions
    {
        [Obsolete("Please use either the HttpLongPollingTransportOptions(IHttpPost httpPost, string uri) or HttpLongPollingTransportOptions(HttpClient client, string uri) constructors.")]
        public HttpLongPollingTransportOptions()
        {
            
        }
        public HttpLongPollingTransportOptions(IHttpPost httpPost, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new ArgumentNullException(nameof(uri), "Must provide a valid Uri");
            }
            this.HttpPost = httpPost ?? throw new ArgumentNullException(nameof(httpPost),"HttpPost cannot be null");
            Uri = uri;
        }

        public HttpLongPollingTransportOptions(HttpClient client, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new ArgumentNullException(nameof(uri), "Must provide a valid Uri");
            }
            HttpClient = client ?? throw new ArgumentNullException(nameof(client), "HttpClient cannot be null");
            Uri = uri;
        }

        private IHttpPost _httpPost;
        private HttpClient _httpClient;
        /// <summary>
        /// An HTTP POST implementation. It should not do HTTP pipelining (rarely done for POSTs anyway).
        /// See https://docs.cometd.org/current/reference/#_two_connection_operation.
        /// 
        /// <para>
        /// Set this property or HttpClient, but not both.
        /// </para>
        /// 
        /// <para>
        /// Enables the implementation of retry policies; useful for servers that may occasionally need a session refresh. Retries are general not supported by HttpClient, as (for some versions) SendAsync disposes the content of HttpRequestMessage. This means that a failed SendAsync call can't be retried, as the HttpRequestMessage can't be reused.
        /// </para>
        /// </summary>
        public IHttpPost HttpPost {
            get => _httpPost;
            set => _httpPost = value;
        }

        /// <summary>
        /// HttpClient to use.
        /// 
        /// <para>
        /// Set this property or HttpPost, but not both.
        /// </para>
        /// </summary>
        public  HttpClient HttpClient
        {
            get => _httpClient;
            set => _httpClient = value;
        }

        public string Uri { get; set; }

        internal HttpLongPollingTransport Build(Action<IEnumerable<JObject>> eventPublisher)
        {
            if (Uri == null)
                throw new Exception("Please set Uri.");

            if (HttpPost == null)
            {
                return new HttpLongPollingTransport(
                    new HttpClientHttpPost(HttpClient ?? new HttpClient()),
                    Uri,
                    eventPublisher);                    
            }
            else
            {

                return new HttpLongPollingTransport(
                    HttpPost,
                    Uri,
                    eventPublisher);
            }
        }        
    }
}
