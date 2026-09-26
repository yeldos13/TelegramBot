using System.Net;
using System.Text.Json;

namespace AnikiChatBot.Modules.Media
{
    public static class MediaHttp
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        public static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return client;
        }

        public static async Task<string> GetStringAsync(string url, CancellationToken ct, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie != null)
                request.Headers.Add("Cookie", cookie);

            using var response = await Client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            string json = await GetStringAsync(url, ct);
            return JsonDocument.Parse(json);
        }

        public static async Task<Uri> ResolveRedirectsAsync(Uri uri, CancellationToken ct)
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.RequestMessage?.RequestUri ?? uri;
        }

        public static async Task<bool> DownloadAsync(string url, string path, long maxBytes, CancellationToken ct)
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            if (response.Content.Headers.ContentLength > maxBytes)
                return false;

            long total = 0;
            var buffer = new byte[81920];

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(path))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (total > maxBytes || total == 0)
            {
                File.Delete(path);
                return false;
            }

            return true;
        }

        public static IEnumerable<JsonElement> FindAll(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == propertyName)
                        yield return property.Value;

                    foreach (var found in FindAll(property.Value, propertyName))
                        yield return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var found in FindAll(item, propertyName))
                        yield return found;
                }
            }
        }

        public static bool IsHost(Uri uri, params string[] domains)
        {
            string host = uri.Host.ToLowerInvariant();
            return domains.Any(d => host == d || host.EndsWith("." + d));
        }

        public static int? GetInt(this JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return null;

            return value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : null;
        }

        public static string? GetStringOrNull(this JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
