using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Tests
{
    [TestClass]
    public class JsonChecks
    {
        [TestMethod]
        [ExpectedException(typeof(JsonSerializationException))]
        public void Deserialize_object_as_array() => DeserializeAsArray(@"{""hello"":""world""}");

        [TestMethod]
        public void Deserialize_array_as_array() => DeserializeAsArray(@"[{""hello"":""world""}]");

        [TestMethod]
        public void Deserialize_empty_as_array() => DeserializeAsArray("");

        List<Dictionary<string, string>> DeserializeAsArray(string s) =>
            JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(s);
    }
}
