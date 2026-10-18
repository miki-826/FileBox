using FileBox.Contracts;

namespace FileBox.Indexer.Volumes;

/// <summary>1ボリューム分のインデックス構築と変更追従。</summary>
internal interface IVolumeSource : IDisposable
{
    VolumeStatus Status { get; }
    /// <summary>初回構築を行い、完了後は変更追従を続ける（キャンセルまで戻らない）。</summary>
    Task RunAsync(CancellationToken ct);
    void SaveSnapshot();
}
