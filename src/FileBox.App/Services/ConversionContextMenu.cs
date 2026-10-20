using System.Runtime.InteropServices;
using FileBox.Data;
using Microsoft.Win32;

namespace FileBox.App.Services;

/// <summary>利用者がオンにしたときだけ、固有の動詞を HKCU に登録する。既定の動詞は変えない。</summary>
public static class ConversionContextMenu
{
    const string Verb = "FileBox.Tools";
    static readonly string[] Classes = ["*", "Directory"];
    public static bool Enabled(UserStore store) => store.GetSetting("tools.contextMenu") == "1";

    public static void Apply(UserStore store, bool enabled)
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileBox.exe");
        foreach (string cls in Classes)
        {
            string path = $@"Software\Classes\{cls}\shell\{Verb}";
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("MUIVerb", "FileBox で変換・整理…");
                key.SetValue("Icon", $"\"{executable}\",0");
                key.SetValue("MultiSelectModel", "Document");
                using var command = key.CreateSubKey("command");
                command.SetValue("", $"\"{executable}\" --tools \"%1\"");
            }
            else Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        store.SetSetting("tools.contextMenu", enabled ? "1" : "0");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
