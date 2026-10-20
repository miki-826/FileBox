using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace FileBox.App.Views;

/// <summary>
/// 読み上げ（UI オートメーション）の名前を付ける。アイコンと文字を並べたボタンなど、中身が文字だけでない部品は
/// 名前が自動で付かないため、見えている文字と同じ名前を付ける。
/// </summary>
public static class Accessibility
{
    public static T Named<T>(this T element, string name) where T : UIElement
    {
        AutomationProperties.SetName(element, name);
        return element;
    }
}
