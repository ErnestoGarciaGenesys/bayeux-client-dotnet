using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Genesys.Bayeux.Client.BayeuxClient;

namespace Genesys.Bayeux.Client
{
    class BayeuxConnection
    {
        readonly string clientId;
        readonly BayeuxClient client; // TODO: remove this field

        public BayeuxConnection(
            string clientId,
            BayeuxClient client)
        {
            this.clientId = clientId;
            this.client = client;
        }
        
        public async Task<JObject> Connect(CancellationToken cancellationToken)
        {
            var response = await client.Request(
                new
                {
                    clientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                },
                cancellationToken);

            client.OnConnectionStateChanged(BayeuxClient.ConnectionState.Connected);

            return response;
        }

        public Task Disconnect(CancellationToken cancellationToken)
        {
            return client.Request(
                new
                {
                    clientId,
                    channel = "/meta/disconnect",
                },
                cancellationToken);
        }

        public Task DoSubscription(
            IEnumerable<string> channelsToSubscribe, 
            IEnumerable<string> channelsToUnsubscribe, 
            CancellationToken cancellationToken)
        {
            return client.Request(
                channelsToSubscribe.Select(channel =>
                    new
                    {
                        clientId,
                        channel = "/meta/subscribe",
                        subscription = channel,
                    })
                    .Concat(channelsToUnsubscribe.Select(channel =>
                    new
                    {
                        clientId,
                        channel = "/meta/unsubscribe",
                        subscription = channel,
                    })),
                cancellationToken);
        }
    }
}
