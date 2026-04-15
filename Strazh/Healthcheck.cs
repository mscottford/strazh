
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Strazh
{
    public static class Healthcheck
    {
        public static async Task<bool> IsNeo4jReady(string neo4jUrl, short retry = 3)
        {
            var statusCode = await GetStatusCode(neo4jUrl);
            if (statusCode == HttpStatusCode.OK)
            {
                Console.WriteLine("Neo4j is ready to use.");
                return true;
            }
            if (retry > 0)
            {
                Console.WriteLine("Waiting for Neo4j...");
                await Task.Delay(10000);
                return await IsNeo4jReady(neo4jUrl, --retry);
            }
            return false;
        }

        private static async Task<HttpStatusCode> GetStatusCode(string neo4jUrl)
        {
            try
            {
                var uri = new Uri(neo4jUrl);
                var httpCheckUrl = $"http://{uri.Host}:7474/";
                using var client = new HttpClient();
                var result = await client.GetAsync(httpCheckUrl);
                return result.StatusCode;
            }
            catch
            {
                return HttpStatusCode.NotFound;
            }
        }
    }
}