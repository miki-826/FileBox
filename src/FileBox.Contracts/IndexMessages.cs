using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBox.Contracts;

/// <summary>App ⇔ Indexer 間の IPC 定義（01_アーキテクチャ §3.3）。</summary>
public static class IndexPipe
{
    public const string Name = "FileBox.Index";
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>4バイト長(LE) + UTF-8 JSON で1メッセージを書き込む。</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>1メッセージを読み込む。接続が閉じられた場合は null。</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return default;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes) throw new InvalidDataException($"不正なメッセージ長: {length}");
        byte[] body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false)) return default;
        return JsonSerializer.Deserialize<T>(body, Options);
    }

    static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>Call は名前付きの操作（Method）を JSON 引数で呼ぶ汎用要求（権限・容量・監査・管理）。</summary>
public enum IndexRequestType { Search, Status, Shutdown, Suggest, Call }

public sealed class IndexRequest
{
    public long Id { get; set; }
    public IndexRequestType Type { get; set; }
    public string? Query { get; set; }
    public int Limit { get; set; } = 500;
    /// <summary>Suggest: 正規化済みの語（ひらがな・小文字）。</summary>
    public List<string>? Terms { get; set; }
    /// <summary>Call: 操作名（PolicyMethods）と引数。</summary>
    public string? Method { get; set; }
    public System.Text.Json.Nodes.JsonNode? Args { get; set; }
}

public sealed class IndexResponse
{
    public long Id { get; set; }
    public SearchResult? Search { get; set; }
    public IndexStatus? Status { get; set; }
    public List<SuggestFolder>? Suggest { get; set; }
    /// <summary>Call の戻り値。</summary>
    public System.Text.Json.Nodes.JsonNode? Result { get; set; }
    public string? Error { get; set; }
}

/// <summary>保存先提案の候補フォルダ（02_機能設計 §1.3-2）。直下のファイル名・サブフォルダ名を添える。</summary>
public sealed class SuggestFolder
{
    public string Path { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public List<string> Subfolders { get; set; } = new();
    public DateTime? ModifiedUtc { get; set; }
}

public sealed class SearchResult
{
    public int Total { get; set; }
    public double ElapsedMs { get; set; }
    public bool Truncated { get; set; }
    public List<SearchItem> Items { get; set; } = new();
}

public sealed class SearchItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
}

public enum IndexState { Starting, Scanning, Ready }

public sealed class IndexStatus
{
    public IndexState State { get; set; }
    public int EntryCount { get; set; }
    public bool Elevated { get; set; }
    public List<VolumeStatus> Volumes { get; set; } = new();
}

public sealed class VolumeStatus
{
    public string Root { get; set; } = "";
    /// <summary>"MFT" / "Snapshot" / "Scan"</summary>
    public string Method { get; set; } = "";
    public bool Ready { get; set; }
    public int Entries { get; set; }
    public double BuildSeconds { get; set; }
}
