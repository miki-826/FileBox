using FileBox.Core.Security;
using FileBox.Core.Tools;
using FileBox.Data;
using FileBox.Tools;

namespace FileBox.App.Services;

public sealed record ConversionResult(string Input, string State, string? Output = null, string? Message = null, string? Detail = null)
{
    public override string ToString() => $"{State}  {Path.GetFileName(Input)}" + (Output != null ? $" → {Output}" : "") + (Message != null ? $"\n{Message}" : "");
}
public sealed record ConversionProgress(int Done, int Total, string Current);

public static class ConversionService
{
    public static EnginePaths Engines(UserStore store) => new(store.GetSetting("tools.engine.ffmpeg") ?? "", store.GetSetting("tools.engine.magick") ?? "",
        store.GetSetting("tools.engine.7z") ?? "", store.GetSetting("tools.engine.java") ?? "", store.GetSetting("tools.engine.cfr") ?? "");

    public static async Task<IReadOnlyList<ConversionResult>> RunAsync(UserStore store, IReadOnlyList<string> sources, ToolKind kind,
        ToolOptions options, string destination, IProgress<ConversionProgress> progress, IProgress<ConversionResult> resultProgress, CancellationToken token)
    {
        options.Validate(kind);
        var definition = ToolCatalog.Get(kind);
        var results = new List<ConversionResult>();
        var engine = new ToolEngine(Engines(store));
        var supported = sources.Where(definition.Accepts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groups = kind is ToolKind.PdfMerge or ToolKind.ArchiveCreate ? new List<IReadOnlyList<string>> { supported } : supported.Select(p => (IReadOnlyList<string>)new[] { p }).ToList();
        void Result(ConversionResult result) { results.Add(result); resultProgress.Report(result); }
        foreach (string path in sources.Except(supported)) Result(new(path, "スキップ", Message: "選択した処理に対応していない種類です"));
        int done = 0;
        foreach (var inputs in groups.Where(g => g.Count > 0))
        {
            string first = inputs[0];
            if (token.IsCancellationRequested) { foreach (string path in inputs) Result(new(path, "キャンセル")); continue; }
            progress.Report(new(done, supported.Count, first));
            try
            {
                if (inputs.Any(p => !Guard.Can(Permission.Read, p, "convert")) || !Guard.Can(Permission.Create, destination, "convert"))
                {
                    foreach (string path in inputs) Result(new(path, "スキップ", Message: "読み取りまたは保存の権限がありません")); continue;
                }
                foreach (string path in inputs)
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクは処理できません。実際のファイルを指定してください");
                // 一時領域も権限・容量の対象にする。公開直前には結果の実サイズで再確認する。
                long inputBytes = inputs.Where(File.Exists).Sum(p => new FileInfo(p).Length);
                if (!await Guard.OutputAsync(destination, inputBytes, "convert")) throw new UnauthorizedAccessException("保存先の権限または容量を確認してください");
                using var stage = new SafeOutput(destination);
                var prepared = await Task.Run(async () =>
                {
                    if (kind is ToolKind.ImageOcr or ToolKind.PdfImages or ToolKind.PdfText)
                        return await NativeConversion.ExecuteAsync(first, kind, options, stage.WorkPath, token);
                    return await engine.ExecuteAsync(inputs, kind, options, stage.WorkPath, token, p => PolicyService.Instance.Can(Permission.Read, p));
                }, token);
                token.ThrowIfCancellationRequested();
                long size = await Task.Run(() => prepared.IsFolder ? Directory.EnumerateFiles(prepared.Path, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : new FileInfo(prepared.Path).Length, token);
                if (!await Guard.OutputAsync(destination, size, "convert")) throw new UnauthorizedAccessException("保存先の権限または容量を確認してください");
                token.ThrowIfCancellationRequested();
                string output = prepared.IsFolder ? stage.PublishFolder(prepared.Path, prepared.Name) : stage.PublishFile(prepared.Path, prepared.Name);
                Guard.Created("convert", output);
                if (prepared.IsFolder) PolicyService.Instance.RecordOwners(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToList());
                foreach (string path in inputs) Result(new(path, "成功", output, prepared.Notice));
            }
            catch (OperationCanceledException) { foreach (string path in inputs) Result(new(path, "キャンセル")); }
            catch (ToolEngineException ex)
            {
                foreach (string path in inputs) Result(new(path, "失敗", Message: ex.Message, Detail: ex.EngineDetail));
                PolicyService.Instance.Audit("convert", first, null, null, "error", "外部エンジンの処理失敗");
            }
            catch (Exception ex)
            {
                foreach (string path in inputs) Result(new(path, "失敗", Message: ex.Message));
                PolicyService.Instance.Audit("convert", first, null, null, "error", ex.GetType().Name);
            }
            finally { done += inputs.Count; progress.Report(new(done, supported.Count, first)); }
        }
        return results;
    }
}
