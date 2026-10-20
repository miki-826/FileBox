using System.Globalization;

namespace FileBox.Core.Tools;

/// <summary>利用者の値をオプション文字列として連結せず、対応表からエンジンの引数を作る。</summary>
public static class MediaCommands
{
    static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static List<string> Image(string input, string output, ToolKind kind, ToolOptions o, int? quality = null)
    {
        o.Validate(kind);
        // 入力を同名の作業ファイルへコピーしてから呼ぶため、@file や coder: の入力は受け付けない。
        var args = new List<string> { "-limit", "memory", "256MiB", "-limit", "map", "512MiB", "-limit", "disk", "2GiB", "-limit", "time", "600", input, "-auto-orient" };
        switch (kind)
        {
            case ToolKind.ImageResize: args.AddRange(["-resize", $"{o.Width}x{o.Height}" + (o.KeepAspect ? "" : "!")]); break;
            case ToolKind.ImageRotate:
                args.AddRange(["-background", o.Format is "jpg" or "bmp" ? o.Background : "none"]);
                if (o.Crop) args.AddRange(["-set", "option:original_size", "%wx%h"]);
                args.AddRange(["-rotate", N(o.Angle)]);
                if (o.Crop) args.AddRange(["-gravity", "center", "-extent", "%[original_size]"]);
                break;
            case ToolKind.ImageStripMetadata: args.Add("-strip"); break;
            case ToolKind.ImageRemoveColor: args.AddRange(["-alpha", "set", "-fuzz", N(o.Tolerance) + "%", "-transparent", o.Background]); break;
        }
        if (o.Format is "jpg" or "bmp") args.AddRange(["-background", o.Background, "-alpha", "remove", "-alpha", "off"]);
        if (o.Format == "ico") args.AddRange(["-resize", "256x256>"]);
        args.AddRange(["-quality", N(quality ?? o.Quality), ImageCoder(o.Format) + ":" + output]);
        return args;
    }

    public static string ImageCoder(string format) => format.ToLowerInvariant().TrimStart('.') switch
    {
        "jpg" or "jpeg" => "JPEG", "png" => "PNG", "webp" => "WEBP", "heic" or "heif" => "HEIC", "ico" => "ICO",
        "gif" => "GIF", "bmp" => "BMP", "tif" or "tiff" => "TIFF", "svg" => "MSVG", _ => throw new ArgumentException("出力形式を選択してください"),
    };

    public static string VideoEncoder(ToolOptions o) => (o.Codec, o.Hardware) switch
    {
        ("h264", "cpu") => "libx264", ("h265", "cpu") => "libx265", ("vp9", "cpu") => "libvpx-vp9", ("av1", "cpu") => "libaom-av1",
        ("h264", "nvenc") => "h264_nvenc", ("h265", "nvenc") => "hevc_nvenc", ("av1", "nvenc") => "av1_nvenc",
        ("h264", "qsv") => "h264_qsv", ("h265", "qsv") => "hevc_qsv", ("av1", "qsv") => "av1_qsv",
        ("h264", "amf") => "h264_amf", ("h265", "amf") => "hevc_amf", ("av1", "amf") => "av1_amf",
        _ => throw new ArgumentException("このコーデックとハードウェアの組み合わせには対応していません"),
    };

    public static List<string> Media(string input, string output, ToolKind kind, ToolOptions o)
    {
        o.Validate(kind);
        var a = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-progress", "pipe:1", "-protocol_whitelist", "file,pipe",
            "-format_whitelist", "mov,matroska,webm,avi,asf,mpeg,mpegts,mp3,wav,aiff,aac,ogg,flac", "-i", input };
        if (o.StartSeconds > 0) a.AddRange(["-ss", N(o.StartSeconds)]);
        if (o.EndSeconds > 0) a.AddRange(["-t", N(o.EndSeconds - o.StartSeconds)]);
        if (kind is ToolKind.AudioConvert or ToolKind.VideoAudio)
        {
            a.AddRange(["-map", "0:a:0", "-vn", "-c:a", AudioEncoder(o.Format)]);
        }
        else if (kind == ToolKind.VideoThumbnail) a.AddRange(["-map", "0:v:0", "-frames:v", "1", "-an", "-update", "1"]);
        else if (kind == ToolKind.VideoGif)
        {
            a.AddRange(["-filter_complex", $"fps={o.FrameRate},scale={o.Width}:-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-an", "-loop", "0"]);
        }
        else if (kind == ToolKind.VideoTrim && o.FastTrim)
        {
            a.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero"]);
        }
        else
        {
            a.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-c:v", VideoEncoder(o), "-c:a", o.Format == "webm" ? "libopus" : "aac", "-b:a", "128k"]);
            if (kind == ToolKind.VideoCompress && o.TargetMiB > 0)
            {
                double seconds = o.EndSeconds - o.StartSeconds;
                double bitrate = o.TargetMiB * 1024 * 1024 * 8 / seconds * 0.95 - 128000;
                if (bitrate < 16000) throw new ArgumentException("目標容量が小さすぎます。容量を増やすか音声抽出を選んでください");
                a.AddRange(["-b:v", N(bitrate / 1000) + "k"]);
            }
            else if (o.Hardware == "cpu")
            {
                int crf = Math.Clamp((100 - o.Quality) / 2, 0, o.Codec is "vp9" or "av1" ? 63 : 51);
                a.AddRange(["-crf", crf.ToString(CultureInfo.InvariantCulture)]);
                if (o.Codec is "vp9" or "av1") a.AddRange(["-b:v", "0"]);
            }
            else a.AddRange(["-b:v", N(1 + o.Quality / 10.0) + "M"]);
            if (kind == ToolKind.VideoResize)
                a.AddRange(["-vf", o.KeepAspect ? $"scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2" : $"scale={o.Width}:{o.Height}"]);
            if (kind == ToolKind.VideoRotate)
                a.AddRange(["-vf", (((o.Angle % 360) + 360) % 360) switch { 90 => "transpose=1", 180 => "hflip,vflip", _ => "transpose=2" }]);
            a.AddRange(["-pix_fmt", "yuv420p"]);
        }
        a.AddRange(["-map_metadata", "-1", output]);
        return a;
    }

    static string AudioEncoder(string format) => format switch
    {
        "mp3" => "libmp3lame", "wav" or "aif" or "aiff" => format == "wav" ? "pcm_s16le" : "pcm_s16be",
        "aac" or "m4a" => "aac", "ogg" => "libvorbis", "wma" => "wmav2", "flac" => "flac", "opus" => "libopus",
        _ => throw new ArgumentException("対応していない音声形式です"),
    };
}
