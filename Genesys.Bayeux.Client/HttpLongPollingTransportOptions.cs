using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    public class HttpLongPollingTransportOptions
    {
        /// <summary>
        /// HttpClient to use.
        /// <para>
        /// Set this property or HttpPost, but not both.
        /// </para>
        /// </summary>
        public HttpClient HttpClient;

        public string Uri { get; set; }

        internal HttpLongPollingTransport Build(Func<IEnumerable<JObject>, CancellationToken, Task> eventPublisher)
        {
            if (Uri == null)
                throw new Exception("Please set Uri.");

            return new HttpLongPollingTransport(HttpClient, Uri, eventPublisher);
        }
    }
}
