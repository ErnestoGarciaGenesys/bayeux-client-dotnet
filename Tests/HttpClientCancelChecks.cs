using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests
{
    [TestClass]
    public class HttpClientCancelChecks
    {
        [TestMethod]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task Check_cancel()
        {
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await new HttpClient().GetAsync("http://www.google.com", cancel.Token);
        }
    }
}
