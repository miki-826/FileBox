using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileBox.App.Services;

/// <summary>
/// カレンダーアプリが filebox:// をリンクにしない場合の転送口（02_機能設計 §4.4、既定 OFF）。
/// 127.0.0.1 だけで待ち受け、URL に秘密の文字列が含まれる要求だけを filebox:// に変換して開く。
/// 管理者権限・URL 予約が要らないよう、HTTP.sys ではなく TCP で最小限の GET だけを解釈する。
/// </summary>
public sealed class LocalLinkServer : IDisposable
{
    readonly int _port;
    readonly string _secret;
    readonly Action<string> _open;
    TcpListener? _listener;
    CancellationTokenSource? _cts;

    public LocalLinkServer(int port, string secret, Action<string> open)
    {
        _port = port;
        _secret = secret;
        _open = open;
    }

    public bool Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine($"リンク転送を開始できません: {ex.Message}");
            _listener = null;
            return false;
        }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return true;
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.ReceiveTimeout = 3000;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
            string request = Encoding.ASCII.GetString(buffer, 0, read);
            string firstLine = request.Split("\r\n", 2)[0];
            var parts = firstLine.Split(' ');
            string? link = null;
            if (parts.Length >= 2 && parts[0] == "GET")
            {
                string prefix = "/" + _secret + "/";
                if (parts[1].StartsWith(prefix, StringComparison.Ordinal))
                    link = "filebox://" + parts[1][prefix.Length..];
            }

            if (link == null)
            {
                await WriteAsync(stream, "404 Not Found", "<p>Not Found</p>", ct);
                return;
            }
            _open(link);
            await WriteAsync(stream, "200 OK",
                "<p style=\"font-family:sans-serif\">FileBox で開きました。このタブは閉じてかまいません。</p><script>setTimeout(()=>window.close(),800)</script>", ct);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    static async Task WriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        string html = $"<!doctype html><meta charset=\"utf-8\"><title>FileBox</title>{body}";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bytes, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }
}
