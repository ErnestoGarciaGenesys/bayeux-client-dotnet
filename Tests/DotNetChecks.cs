using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests
{
    [TestClass]
    public class DotNetChecks
    {
        [TestMethod]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task Check_cancel()
        {
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await new HttpClient().GetAsync("http://www.google.com", cancel.Token);
        }

        [TestMethod]
        public async Task Check_TaskScheduler_Current()
        {
            Debug.WriteLine(new { currentTaskScheduler = TaskScheduler.Current });
            Debug.WriteLine($"Outside task: Task.Id = {Task.CurrentId}");
            Debug.WriteLine($"Outside task: SynchronizationContext.Current = {SynchronizationContext.Current}");

            await Task.Run(() =>
            {
                Debug.WriteLine($"Inside task: Task.Id = {Task.CurrentId}");
                Debug.WriteLine($"Inside task: SynchronizationContext.Current = {SynchronizationContext.Current}");
            });
        }
    }
}
