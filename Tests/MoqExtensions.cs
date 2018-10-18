using Moq.Language;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tests
{
    public static class MoqExtensions
    {
        public static ISetupSequentialResult<Task<T>> ReturnsIndefinitely<T>(this ISetupSequentialResult<Task<T>> setup, Func<Task<T>> valueFunction)
        {
            for (var i = 0; i < 100; i++)
                setup.Returns(valueFunction);

            return setup;
        }
    }
}
