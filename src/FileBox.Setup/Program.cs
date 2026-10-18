using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FileBox.Setup
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupWindow());
        }
    }

    sealed class SetupWindow : Form
    {
        const string DownloadRoot = "https://github.com/miki-826/FileBox/releases/download/preview/";
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "ダウンロードを準備しています…", TextAlign = ContentAlignment.MiddleLeft };
        readonly Label detail = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
        readonly Button primary = new Button { Text = "FileBox を起動", AutoSize = true, Enabled = false };
        readonly Button folder = new Button { Text = "保存先を開く", AutoSize = true, Visible = false };
        readonly Button cancel = new Button { Text = "キャンセル", AutoSize = true };
        CancellationTokenSource cancellation;
        bool busy;
        string installedPath;

        public SetupWindow()
        {
            Text = "FileBox のダウンロード — 開発プレビュー";
            ClientSize = new Size(560, 320);
            Font = new Font("Yu Gothic UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 22), ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 44, 48, 44, 24, 48, 24, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = "FileBox を準備します", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Dock = DockStyle.Fill });
            layout.Controls.Add(new Label { Text = "最新版をダウンロードし、このユーザー用のフォルダに展開します。\n開発プレビューです。製品版ではありません。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText });
            layout.Controls.Add(status);
            layout.Controls.Add(bar);
            layout.Controls.Add(detail);
            layout.Controls.Add(new Label { Text = "Windows x64 ／ 必要な .NET と Windows App SDK も同梱", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 9), ForeColor = SystemColors.GrayText });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(primary);
            buttons.Controls.Add(folder);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            Shown += async delegate { await DownloadAsync(); };
            cancel.Click += delegate { if (busy) cancellation.Cancel(); else Close(); };
            primary.Click += async delegate
            {
                if (installedPath == null) { await DownloadAsync(); return; }
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(installedPath, "FileBox.exe")) { UseShellExecute = true, WorkingDirectory = installedPath });
                    Close();
                }
                catch (Exception ex) { MessageBox.Show(this, "起動できませんでした。\n" + ex.Message, "FileBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            folder.Click += delegate { Process.Start(new ProcessStartInfo(installedPath) { UseShellExecute = true }); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; cancellation.Cancel(); }
            };
        }

        async Task DownloadAsync()
        {
            busy = true;
            primary.Enabled = false;
            cancel.Text = "キャンセル";
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FileBox", "preview-" + Guid.NewGuid().ToString("N"));
            string zip = Path.Combine(work, "download.zip");
            string app = Path.Combine(work, "app");
            try
            {
                if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("FileBox は 64 ビット版 Windows 用です。");
                Directory.CreateDirectory(work);
                status.Text = "最新版の FileBox をダウンロードしています…";
                detail.Text = "取得元: GitHub / miki-826 / FileBox";
                bar.Style = ProgressBarStyle.Marquee;
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("FileBox-Preview-Setup/0.1");
                    using (var response = await client.GetAsync(DownloadRoot + "FileBox-preview-win-x64.zip", HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();
                        long? length = response.Content.Headers.ContentLength;
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            long received = 0;
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                received += count;
                                if (received > 1024L * 1024 * 1024) throw new InvalidDataException("ダウンロードサイズが大きすぎます。");
                                await output.WriteAsync(buffer, 0, count, token);
                                if (length > 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = (int)Math.Min(100, received * 100 / length.Value); }
                                detail.Text = string.Format("{0:N1} MB{1}", received / 1048576.0, length > 0 ? " / " + (length.Value / 1048576.0).ToString("N1") + " MB" : " を取得済み");
                            }
                        }
                    }
                    status.Text = "ダウンロードしたファイルを確認しています…";
                    bar.Style = ProgressBarStyle.Marquee;
                    using (var response = await client.GetAsync(DownloadRoot + "FileBox-preview-win-x64.zip.sha256", token))
                    {
                        response.EnsureSuccessStatusCode();
                        string checksum = await response.Content.ReadAsStringAsync();
                        await Task.Run(delegate { Package.Verify(zip, checksum); }, token);
                    }
                }
                token.ThrowIfCancellationRequested();
                status.Text = "FileBox を展開しています…";
                bar.Style = ProgressBarStyle.Continuous;
                var progress = new Progress<int>(value => { bar.Value = Math.Min(100, value); });
                await Task.Run(delegate { Package.Extract(zip, app, token, value => ((IProgress<int>)progress).Report(value)); }, token);
                token.ThrowIfCancellationRequested();
                File.Delete(zip);
                installedPath = app;
                status.Text = "準備ができました。FileBox を起動できます。";
                detail.Text = "保存先: " + installedPath;
                bar.Value = 100;
                primary.Text = "FileBox を起動";
                folder.Visible = true;
            }
            catch (OperationCanceledException)
            {
                status.Text = "ダウンロードをキャンセルしました。";
                detail.Text = "「再試行」で最初からやり直せます。";
                primary.Text = "再試行";
            }
            catch (Exception ex)
            {
                status.Text = "FileBox の準備を完了できませんでした。";
                detail.Text = "通信環境と空き容量を確認して再試行してください。\n" + ex.Message;
                primary.Text = "再試行";
            }
            finally
            {
                // 今回新規作成した一時フォルダだけを片付け、既存の本体と設定は触らない。
                if (installedPath == null && Directory.Exists(work))
                {
                    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                busy = false;
                cancellation.Dispose();
                bar.Style = ProgressBarStyle.Continuous;
                cancel.Text = "閉じる";
                primary.Enabled = true;
            }
        }
    }
}
