using System.Net.WebSockets;

namespace Api;

public class WebSocketReceiver
{
    private readonly WebSocket _webSocket;

    public WebSocketReceiver(WebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    public async Task<string> ReceiveToken(CancellationToken token)
    {
        ArraySegment<byte> buffer = new(new byte[100]);
        WebSocketReceiveResult? result;

        using MemoryStream ms = new();

        do
        {
            result = await _webSocket.ReceiveAsync(buffer, token);
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);

        if (result.MessageType != WebSocketMessageType.Text)
            throw new Exception("only text messages are supported for authentication.");

        byte[] array = ms.ToArray();
        return System.Text.Encoding.UTF8.GetString(array);
    }

    public async Task<Pixel> ReceivePixel(CancellationToken token)
    {
        ArraySegment<byte> buffer = new(new byte[10]);
        WebSocketReceiveResult? result;

        using MemoryStream ms = new();

        do
        {
            result = await _webSocket.ReceiveAsync(buffer, token);
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);

        if (result.MessageType != WebSocketMessageType.Binary)
            throw new Exception("only binary messages are supported.");

        byte[] array = ms.ToArray();
        return Pixel.FromBytes(array);
    }
}