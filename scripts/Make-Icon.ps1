# FileBox のアイコン（src/FileBox.App/Assets/FileBox.ico と FileBox.png）を作る。
# Windows PowerShell 5.1（System.Drawing）で実行する。デザインを変えたらこのスクリプトを直して作り直す。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $repoRoot 'src/FileBox.App/Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class FileBoxIcon
{
    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 青い角丸の下地に、白いフォルダ（箱）と取っ手。どの大きさでも形が読めるよう太めに描く
    public static byte[] Png(int size, string savePath)
    {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                using (var bg = Rounded(0, 0, s, s, s * 0.22f))
                using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(0, s), Color.FromArgb(0x3B, 0x8B, 0xF6), Color.FromArgb(0x1D, 0x4E, 0xD8)))
                    g.FillPath(grad, bg);
                using (var back = new GraphicsPath())
                using (var brush = new SolidBrush(Color.FromArgb(0xBF, 0xDB, 0xFE)))
                {
                    back.AddPolygon(new[]
                    {
                        new PointF(s * 0.18f, s * 0.28f), new PointF(s * 0.42f, s * 0.28f), new PointF(s * 0.49f, s * 0.36f),
                        new PointF(s * 0.82f, s * 0.36f), new PointF(s * 0.82f, s * 0.60f), new PointF(s * 0.18f, s * 0.60f),
                    });
                    g.FillPath(brush, back);
                }
                using (var front = Rounded(s * 0.14f, s * 0.43f, s * 0.72f, s * 0.39f, s * 0.06f))
                    g.FillPath(Brushes.White, front);
                using (var handle = Rounded(s * 0.38f, s * 0.555f, s * 0.24f, s * 0.075f, s * 0.0375f))
                using (var brush = new SolidBrush(Color.FromArgb(0x1D, 0x4E, 0xD8)))
                    g.FillPath(brush, handle);
            }
            if (savePath != null) bmp.Save(savePath, ImageFormat.Png);
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    // ICO（PNG 圧縮の各サイズを並べる。Windows Vista 以降で読める形式）
    public static void Write(string icoPath, string pngPath)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = Png(sizes[i], sizes[i] == 256 ? pngPath : null);
        using (var fs = File.Create(icoPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)images[i].Length); w.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

[FileBoxIcon]::Write((Join-Path $assets 'FileBox.ico'), (Join-Path $assets 'FileBox.png'))
Write-Host "アイコン: $assets"
