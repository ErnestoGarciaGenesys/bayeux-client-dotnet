using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Genesys.Bayeux.Client
{
    public class Auth
    {
        public class PasswordGrantTypeCredentials
        {
            public string UserName { get; set; }
            public string Password { get; set; }
            public string ClientId { get; set; }
            public string ClientSecret { get; set; }
        }

        public static async Task<string> Authenticate(HttpClient httpClient, string baseUrl, PasswordGrantTypeCredentials credentials)
        {
            var authRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/auth/v3" + "/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>()
                {
                    ["grant_type"] = "password",
                    ["scope"] = "*",
                    ["clientId"] = credentials.ClientId,
                    ["username"] = credentials.UserName,
                    ["password"] = credentials.Password,
                })
            };

            authRequest.Headers.Add("Authorization", "Basic " +
                    Convert.ToBase64String(Encoding.GetEncoding("ISO-8859-1").GetBytes(
                        credentials.ClientId + ":" + credentials.ClientSecret)));

            authRequest.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(authRequest);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(responseContent);

            var error = json["error"];
            if (error != null)
                throw new AuthException(error.ToString(), json["error_description"]?.ToString());
            
            return json["access_token"].ToString();
        }
    }
}

