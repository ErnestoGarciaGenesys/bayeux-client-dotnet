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
        readonly IContext context;

        public BayeuxConnection(
            string clientId,
            IContext context)
        {
            this.clientId = clientId;
            this.context = context;
        }
        
        public async Task<JObject> Connect(CancellationToken cancellationToken)
        {
            var response = await context.Request(
                new
                {
                    clientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                },
                cancellationToken);

            context.SetConnectionState(BayeuxClient.ConnectionState.Connected);

            return response;
        }

        public Task Disconnect(CancellationToken cancellationToken)
        {
            return context.Request(
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
            return context.RequestMany(
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
