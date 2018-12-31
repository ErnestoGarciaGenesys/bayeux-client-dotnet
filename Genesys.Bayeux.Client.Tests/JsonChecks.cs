using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesys.Bayeux.Client.Tests
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

        [TestMethod]
        public void Write_JSON()
        {
            var input = JsonConvert.SerializeObject(new { greeting = "hola" });
            var read = JToken.Parse(input);
            //ReadFrom(new JsonTextReader(reader));
            
            Debug.WriteLine($"Received: {read.ToString(Formatting.None)}");
        }
    }
}
