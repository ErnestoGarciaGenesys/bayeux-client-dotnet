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

        [TestMethod]
        public void Rethrow_does_not_preserve_stack_trace_line_in_same_method()
        {
            try
            {
                throw new Exception("Test");
            }
            catch
            {
                throw;
            }
        }

        void ThrowExc() => throw new Exception("Test");

        [TestMethod]
        public void Rethrow_preserves_stack_trace_in_another_method()
        {
            try
            {
                ThrowExc();
            }
            catch
            {
                throw;
            }
        }

        [TestMethod]
        public async Task Rethrow_preserves_stack_trace_in_same_method_async()
        {
            try
            {
                await Task.Run(() => throw new Exception("Test"));
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                throw;
            }
        }

        async Task ThrowExcAsync()
        {
            await Task.Run(() => throw new Exception("Test"));
        }

        [TestMethod]
        public async Task Rethrow_preserves_stack_trace_in_another_method_async()
        {
            try
            {
                await ThrowExcAsync();
            }
            catch
            {
                throw;
            }
        }
    }
}
