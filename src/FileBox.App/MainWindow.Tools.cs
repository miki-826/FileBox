using FileBox.App.Services;
using FileBox.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App;

public sealed partial class MainWindow
{
    readonly List<string> _toolArguments = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _toolTimer;
    bool _toolDialogOpen;

    void QueueTools(IEnumerable<string> paths)
    {
        _toolArguments.AddRange(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
        if (_toolTimer == null)
        {
            _toolTimer = DispatcherQueue.CreateTimer();
            _toolTimer.Interval = TimeSpan.FromMilliseconds(800);
            _toolTimer.IsRepeating = false;
            _toolTimer.Tick += async (_, _) =>
            {
                if (Content.XamlRoot == null || _toolDialogOpen || VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => HasDialog(p.Child))) { _toolTimer.Start(); return; }
                var selected = _toolArguments.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); _toolArguments.Clear();
                _toolDialogOpen = true;
                try { await ConversionDialog.ShowAsync(Content.XamlRoot, selected); }
                catch (Exception ex) { ShowInfo(ex.Message, InfoBarSeverity.Error); }
                finally { _toolDialogOpen = false; }
            };
        }
        _toolTimer.Stop(); _toolTimer.Start();
    }

    static bool HasDialog(DependencyObject? node)
    {
        if (node is ContentDialog) return true;
        if (node == null) return false;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasDialog(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
}
