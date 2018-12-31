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

        [TestMethod]
        public void Array_is_IEnumerable()
        {
            Assert.IsTrue(new object[0] is System.Collections.IEnumerable);
        }


        [TestMethod]
        public async Task Check_if_Task_WhenAny_is_canceled()
        {
            var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            Task completedTask = await Task.WhenAny(
                Task.Delay(TimeSpan.FromSeconds(60)),
                Task.Delay(TimeSpan.FromSeconds(30), cancellationSource.Token));
            
            Assert.IsTrue(completedTask.IsCanceled);
        }

        async Task PleaseThrow()
        {
            await Task.FromResult(0);
            throw new OperationCanceledException();
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public async Task Check_exception_thrown_on_cancel()
        {
            await PleaseThrow();
        }

        async Task PleaseThrow2(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(0);
        }

        [TestMethod]
        [ExpectedException(typeof(OperationCanceledException))]
        public async Task Check_exception_thrown_on_cancel_with_cancellationToken_as_method_parameter()
        {
            await PleaseThrow2(new CancellationToken(canceled: true));
        }

        [TestMethod]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task Check_exception_thrown_on_cancel_with_cancellationToken_as_Task_paramter()
        {
            var cancellationToken = new CancellationToken(canceled: true);
            await Task.Run(
                () => PleaseThrow2(cancellationToken), 
                cancellationToken);
        }
    }
}
