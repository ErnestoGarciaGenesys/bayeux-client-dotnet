using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        
        public async Task Connect(CancellationToken cancellationToken)
        {
            await client.Request(
                new
                {
                    clientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                },
                cancellationToken);

            client.OnConnectionStateChanged(BayeuxClient.ConnectionState.Connected);
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

        public Task DoSubscriptionOperation(
            string metaChannel, 
            IEnumerable<string> channels, 
            CancellationToken cancellationToken)
        {
            return client.Request(
                channels.Select(channel =>
                    new
                    {
                        clientId,
                        channel = metaChannel,
                        subscription = channel,
                    }),
                cancellationToken);
        }
    }
}
