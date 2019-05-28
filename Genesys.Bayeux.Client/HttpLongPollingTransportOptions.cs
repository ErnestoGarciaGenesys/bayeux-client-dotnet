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
        public IHttpPost HttpPost { get; set; }

        /// <summary>
        /// HttpClient to use.
        /// 
        /// <para>
        /// Set this property or HttpPost, but not both.
        /// </para>
        /// </summary>
        public HttpClient HttpClient { get; set; }

        public string Uri { get; set; }

        internal HttpLongPollingTransport Build()
        {
            if (Uri == null)
                throw new Exception("Please set Uri.");

            if (HttpPost == null)
            {
                return new HttpLongPollingTransport(
                    new HttpClientHttpPost(HttpClient ?? new HttpClient()),
                    Uri);                    
            }
            else
            {
                if (HttpClient != null)
                    throw new Exception("Set HttpPost or HttpClient, but not both.");

                return new HttpLongPollingTransport(
                    HttpPost,
                    Uri);
            }
        }        
    }
}
