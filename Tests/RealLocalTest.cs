using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genesys.Bayeux.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Tests
{
    [TestClass]
    public class RealLocalTest
    {
        const string url = "http://localhost:8080/bayeux/";

        [TestMethod]
        public async Task Run_for_a_while()
        {
            var httpClient = new HttpClient();
            var bayeuxClient = new BayeuxClient(httpClient, url);

            bayeuxClient.EventReceived += (e, args) =>
                Debug.WriteLine($"Event received on channel {args.Channel} with data\n{args.Data}");

            bayeuxClient.ConnectionStateChanged += (e, args) =>
                Debug.WriteLine($"Bayeux connection state changed to {args.ConnectionState}");

            bayeuxClient.AddSubscriptions("/**");

            await bayeuxClient.Start();

            using (bayeuxClient)
            {
                Thread.Sleep(TimeSpan.FromSeconds(60));
            }
        }
    }
}
