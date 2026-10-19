using System.ComponentModel;
using FileBox.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>タブ内の分割ペインを木構造から描画する。区画のビューはキャッシュして再配置する（状態を保つため）。</summary>
public sealed class PaneHost : Grid
{
    readonly TabViewModel _tab;
    readonly IWorkspaceActions _actions;
    readonly Dictionary<string, (Border Frame, UIElement View, TextBlock Title)> _cache = new();

    public PaneHost(TabViewModel tab, IWorkspaceActions actions)
    {
        _tab = tab;
        _actions = actions;
        _tab.LayoutChanged += Rebuild;
        _tab.PropertyChanged += OnTabChanged;
        Rebuild();
    }

    public UIElement? ViewFor(PaneViewModel pane) => _cache.TryGetValue(pane.Id, out var c) ? c.View : null;

    void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.ActivePane)) UpdateHighlight();
    }

    void Rebuild()
    {
        // 既存の枠を親から外してから組み立て直す
        foreach (var (frame, _, _) in _cache.Values)
            if (frame.Parent is Panel p) p.Children.Remove(frame);
        Children.Clear();

        var live = _tab.Panes.Select(p => p.Id).ToHashSet();
        foreach (var dead in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(dead);

        Children.Add(Build(_tab.Root));
        UpdateHighlight();
    }

    UIElement Build(PaneNode node)
    {
        if (node is LeafNode leaf) return Frame(leaf.Pane);
        var split = (SplitNode)node;
        var grid = new Grid();
        bool horizontal = split.Orientation == Orientation.Horizontal;
        var first = Build(split.First);
        var second = Build(split.Second);
        var handle = new SplitterHandle(horizontal);

        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            SetColumn((FrameworkElement)first, 0);
            SetColumn(handle, 1);
            SetColumn((FrameworkElement)second, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(split.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - split.Ratio, GridUnitType.Star) });
            SetRow((FrameworkElement)first, 0);
            SetRow(handle, 1);
            SetRow((FrameworkElement)second, 2);
        }
        grid.Children.Add(first);
        grid.Children.Add(handle);
        grid.Children.Add(second);

        handle.Dragged += position =>
        {
            double total = horizontal ? grid.ActualWidth : grid.ActualHeight;
            if (total <= 0) return;
            double ratio = Math.Clamp(position / total, 0.15, 0.85);
            split.Ratio = ratio;
            if (horizontal)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
                grid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
            }
            else
            {
                grid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
                grid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
            }
        };
        handle.Grid = grid;
        return grid;
    }

    Border Frame(PaneViewModel pane)
    {
        if (_cache.TryGetValue(pane.Id, out var cached))
        {
            UpdateFrameButtons(cached.Frame);
            return cached.Frame;
        }

        UIElement view = pane switch
        {
            FolderPaneViewModel f => new FolderPaneView(f, _actions),
            PreviewPaneViewModel p => new PreviewPaneView(p, _actions),
            SearchPaneViewModel s => new SearchPaneView(s, _actions),
            InboxPaneViewModel i => new InboxPaneView(i, _actions),
            CapacityPaneViewModel c => new CapacityPaneView(c, _actions),
            AdminPaneViewModel a => new AdminPaneView(a),
            ShellPaneViewModel sh => new ShellPaneView(sh, _actions),
            _ => new TextBlock { Text = "?" },
        };

        var title = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        title.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = pane, Path = new PropertyPath(nameof(PaneViewModel.Title)) });

        var header = new Grid { Padding = new Thickness(12, 6, 6, 6), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Tag = "buttons" };
        var splitMenu = new MenuFlyout();
        void AddSplit(string label, Orientation orientation)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => _actions.SplitPane(pane, orientation);
            splitMenu.Items.Add(item);
        }
        AddSplit("左右に分割 (Ctrl+\\)", Orientation.Horizontal);
        AddSplit("上下に分割 (Ctrl+Shift+\\)", Orientation.Vertical);
        var splitContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        splitContent.Children.Add(SplitIcon(vertical: false));
        splitContent.Children.Add(new TextBlock { Text = "分割", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var splitButton = new DropDownButton { Content = splitContent, Flyout = splitMenu,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4), MinHeight = 32 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(splitButton, "区画を分割");
        buttons.Children.Add(splitButton);
        buttons.Children.Add(SmallButton(new FontIcon { Glyph = "", FontSize = 14 }, "この区画を閉じる", () => _actions.ClosePane(pane)));
        SetColumn(buttons, 1);
        header.Children.Add(buttons);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(header);
        SetRow((FrameworkElement)view, 1);
        body.Children.Add(view);

        var frame = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2, 6, 2, 2),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Tag = pane,
        };
        frame.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _actions.ActivatePane(pane)), true);
        frame.GotFocus += (_, _) => _actions.ActivatePane(pane);
        _cache[pane.Id] = (frame, view, title);
        UpdateFrameButtons(frame);
        return frame;
    }

    void UpdateFrameButtons(Border frame)
    {
        var buttons = ((Grid)((Grid)frame.Child).Children[0]).Children.OfType<StackPanel>().First();
        bool canSplit = _tab.CanSplit;
        buttons.Children[0].Visibility = canSplit ? Visibility.Visible : Visibility.Collapsed;
        buttons.Children[1].Visibility = _tab.PaneCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (frame.Tag is PaneViewModel pane && _cache.TryGetValue(pane.Id, out var c) && c.View is FolderPaneView folder)
            folder.SetNavigationVisible(_tab.PaneCount > 1);
    }

    /// <summary>分割の向きが一目で分かるアイコン（枠の中に縦または横の仕切り線）。</summary>
    static UIElement SplitIcon(bool vertical)
    {
        var stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var grid = new Grid { Width = 18, Height = 16 };
        grid.Children.Add(new Border { BorderBrush = stroke, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) });
        grid.Children.Add(new Border
        {
            Background = stroke,
            Width = vertical ? double.NaN : 1.5,
            Height = vertical ? 1.5 : double.NaN,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Stretch,
        });
        return grid;
    }

    static Button SmallButton(UIElement content, string tip, Action action)
    {
        var b = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 40,
            MinHeight = 36,
            CornerRadius = new CornerRadius(6),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    void UpdateHighlight()
    {
        bool multi = _tab.PaneCount > 1;
        foreach (var (frame, _, _) in _cache.Values)
        {
            bool active = multi && ReferenceEquals(frame.Tag, _tab.ActivePane);
            frame.BorderBrush = active
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
    }

    public void Detach()
    {
        _tab.LayoutChanged -= Rebuild;
        _tab.PropertyChanged -= OnTabChanged;
    }
}

/// <summary>区画の境界。ドラッグで比率を変える。</summary>
public sealed class SplitterHandle : Grid
{
    readonly bool _horizontal;
    bool _dragging;
    public Grid? Grid { get; set; }
    public event Action<double>? Dragged;

    public SplitterHandle(bool horizontal)
    {
        _horizontal = horizontal;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        var line = new Border
        {
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Width = horizontal ? 1 : double.NaN,
            Height = horizontal ? double.NaN : 1,
        };
        Children.Add(line);
        PointerPressed += (_, e) =>
        {
            _dragging = CapturePointer(e.Pointer);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (!_dragging || Grid == null) return;
            var p = e.GetCurrentPoint(Grid).Position;
            Dragged?.Invoke(_horizontal ? p.X : p.Y);
        };
        PointerReleased += (_, e) =>
        {
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
        };
        PointerCaptureLost += (_, _) => _dragging = false;
    }
}
