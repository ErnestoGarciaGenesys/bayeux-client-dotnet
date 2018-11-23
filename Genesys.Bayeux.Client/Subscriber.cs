using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using static Genesys.Bayeux.Client.BayeuxClient;

namespace Genesys.Bayeux.Client
{
    class Subscriber
    {
        readonly BayeuxClient client;
        readonly ChannelList subscribedChannels = new ChannelList();

        public Subscriber(BayeuxClient client)
        {
            this.client = client;
        }

        public void AddSubscription(IEnumerable<string> channels) =>
            subscribedChannels.Add(channels);

        public void RemoveSubscription(IEnumerable<string> channels) =>
            subscribedChannels.Remove(channels);

        public void OnConnected()
        {
            var resubscribeChannels = subscribedChannels.Copy();

            if (resubscribeChannels.Count != 0)
                _ = client.RequestSubscribe(resubscribeChannels, CancellationToken.None, throwIfNotConnected: false);
        }

        class ChannelList
        {
            readonly List<string> items;
            readonly object syncRoot;

            public ChannelList()
            {
                items = new List<string>();
                syncRoot = ((ICollection)items).SyncRoot;
            }

            public void Add(IEnumerable<string> channels)
            {
                lock (syncRoot)
                {
                    items.AddRange(channels);
                }
            }

            public void Remove(IEnumerable<string> channels)
            {
                lock (syncRoot)
                {
                    foreach (var channel in channels)
                        items.Remove(channel);
                }
            }

            public List<string> Copy()
            {
                lock (syncRoot)
                {
                    return new List<string>(items);
                }
            }
        }
    }
}
