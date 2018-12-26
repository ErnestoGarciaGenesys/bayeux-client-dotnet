using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;

namespace Genesys.Bayeux.Client
{
    public class WebSocketTransportOptions
    {
        public Func<WebSocket> WebSocketFactory { get; set; }
        public Uri Uri { get; set; }
        public TimeSpan? ResponseTimeout { get; set; }
        
        internal WebSocketTransport Build(Action<IEnumerable<JObject>> eventPublisher)
        {
            return new WebSocketTransport(
                WebSocketFactory ?? (() => SystemClientWebSocket.CreateClientWebSocket()),
                Uri ?? throw new Exception("Please set Uri."),
                ResponseTimeout ?? TimeSpan.FromSeconds(30), // TODO: this should be different for different kinds of requests
                eventPublisher);
        }        
    }
}
