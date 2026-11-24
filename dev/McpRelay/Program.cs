using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var tokenPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GameHelper2.Dev", "mcp-token");

using var transport = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
using var client = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(70) };
client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");

while (await Console.In.ReadLineAsync() is { } line)
{
    JsonElement? id = null;
    try
    {
        using var message = JsonDocument.Parse(line);
        if (message.RootElement.ValueKind == JsonValueKind.Object &&
            message.RootElement.TryGetProperty("id", out var requestId))
        {
            id = requestId.Clone();
        }

        var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Invalid local MCP token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:3002/")
        {
            Content = new StringContent(line, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            await Console.Out.WriteLineAsync(await response.Content.ReadAsStringAsync());
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                               HttpRequestException or OperationCanceledException or InvalidDataException)
    {
        const string message = "Local GameHelper2 MCP unavailable. Check that the developer overlay and its bridge are running.";
        if (id is { } originalId)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = originalId,
                error = new { code = -32000, message },
            }));
        }
        else
        {
            await Console.Error.WriteLineAsync(message);
        }
    }
}
