using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    public class BayeuxConnection
    {
        readonly string clientId;
        readonly IBayeuxClientContext context;

        public BayeuxConnection(
            string clientId,
            IBayeuxClientContext context)
        {
            this.clientId = clientId;
            this.context = context;
        }

        public async Task<JObject> ConnectAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var response = await context.RequestAsync(
                new
                {
                    clientId,
                    channel = "/meta/connect",
                    connectionType = "long-polling",
                },
                cancellationToken);

            await context.SetConnectionStateAsync(ConnectionState.Connected, cancellationToken);

            return response;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
         => context.RequestAsync(
                new
                {
                    clientId,
                    channel = "/meta/disconnect",
                },
                cancellationToken);

        public Task DoSubscriptionAsync(
            IEnumerable<string> channelsToSubscribe,
            IEnumerable<string> channelsToUnsubscribe,
            CancellationToken cancellationToken)
        {
            return context.RequestManyAsync(
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
