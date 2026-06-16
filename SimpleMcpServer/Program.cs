using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SimpleMcpServer;

/// <summary>
/// 외부 NuGet 패키지 없이, TcpListener 기반의 아주 단순한 HTTP 서버로
/// MCP(Model Context Protocol) JSON-RPC 요청을 처리하는 예제 서버.
/// 제공하는 도구(tool)는 단 하나: 랜덤 문자열을 반환하는 get_random_string.
/// </summary>
internal static class Program
{
    private const int DefaultPort = 9153;
    private static readonly Random Rng = new();

    private static async Task Main(string[] args)
    {
        int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : DefaultPort;

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[SimpleMcpServer] Listening on http://127.0.0.1:{port}/  (Ctrl+C to stop)");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, path, headers, body) = await ReadHttpRequestAsync(stream);
                Console.WriteLine($"[SimpleMcpServer] {method} {path} ({body.Length} bytes)");

                if (method != "POST")
                {
                    await WriteHttpResponseAsync(stream, 405, "Method Not Allowed",
                        "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Only POST is supported.\n"));
                    return;
                }

                JsonNode? response;
                try
                {
                    var request = body.Length == 0 ? null : JsonNode.Parse(body)?.AsObject();
                    response = HandleJsonRpc(request);
                }
                catch (Exception ex)
                {
                    response = BuildError(null, -32700, $"Parse error: {ex.Message}");
                }

                if (response is null)
                {
                    // JSON-RPC notification (id 없음) -> 응답 본문 없이 202만 반환
                    await WriteHttpResponseAsync(stream, 202, "Accepted", "application/json", []);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json", bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimpleMcpServer] Error: {ex.Message}");
            }
        }
    }

    // ----- 아주 단순한 HTTP 요청 파서 (헤더 끝까지 읽고 Content-Length만큼 바디 읽기) -----
    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)>
        ReadHttpRequestAsync(NetworkStream stream)
    {
        var raw = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(buffer);
            if (n == 0) break;
            raw.Add(buffer[0]);
            if (raw.Count >= 4 &&
                raw[^4] == (byte)'\r' && raw[^3] == (byte)'\n' &&
                raw[^2] == (byte)'\r' && raw[^1] == (byte)'\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(raw.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        string method = "";
        string path = "";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2) { method = parts[0]; path = parts[1]; }
                continue;
            }

            var idx = lines[i].IndexOf(':');
            if (idx > 0)
                headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        byte[] requestBody = [];
        if (headers.TryGetValue("Content-Length", out var clStr) &&
            int.TryParse(clStr, out var contentLength) && contentLength > 0)
        {
            requestBody = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(requestBody.AsMemory(read, contentLength - read));
                if (n == 0) break;
                read += n;
            }
        }

        return (method, path, headers, requestBody);
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream, int statusCode, string statusText, string contentType, byte[] body)
    {
        var header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        if (body.Length > 0)
            await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    // ----- MCP(JSON-RPC 2.0) 처리 -----

    private static JsonNode? HandleJsonRpc(JsonObject? request)
    {
        if (request is null)
            return BuildError(null, -32600, "Invalid Request");

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        bool isNotification = id is null; // JSON-RPC notification은 id가 없음 -> 응답 보내지 않음

        switch (method)
        {
            case "initialize":
                return BuildResult(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "simple-mcp-server",
                        ["version"] = "1.0.0"
                    }
                });

            case "notifications/initialized":
                return null;

            case "tools/list":
                return BuildResult(id, new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "get_random_string",
                            ["description"] = "랜덤한 문자열을 반환합니다.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["length"] = new JsonObject
                                    {
                                        ["type"] = "integer",
                                        ["description"] = "반환할 문자열의 길이 (기본값 16)"
                                    }
                                }
                            }
                        }
                    }
                });

            case "tools/call":
                return HandleToolsCall(id, request["params"] as JsonObject);

            case "ping":
                return BuildResult(id, new JsonObject());

            default:
                return isNotification ? null : BuildError(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonNode HandleToolsCall(JsonNode? id, JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (name != "get_random_string")
            return BuildError(id, -32602, $"Unknown tool: {name}");

        int length = 16;
        if (@params?["arguments"] is JsonObject argsObj && argsObj["length"] is JsonNode lenNode)
            length = lenNode.GetValue<int>();
        length = Math.Clamp(length, 1, 1024);

        return BuildResult(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = GenerateRandomString(length) }
            },
            ["isError"] = false
        });
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
            sb.Append(chars[Rng.Next(chars.Length)]);
        return sb.ToString();
    }

    private static JsonObject BuildResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject BuildError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
