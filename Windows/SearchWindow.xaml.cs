using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FenceDesk.Models;

namespace FenceDesk.Windows;

public partial class SearchWindow : Window
{
    private readonly MainWindow _host;
    private readonly List<SearchResult> _allResults;
    private readonly ObservableCollection<SearchResult> _view = new();
    private bool _autoCloseReady;
    private bool _closed;
    private bool _isClosing;

    public SearchWindow(MainWindow host, List<SearchResult> results)
    {
        _host = host;
        _allResults = results;

        InitializeComponent();
        ResultsList.ItemsSource = _view;

        Opacity = 0;
        Loaded += OnLoaded;
        Activated += (_, _) =>
        {
            _autoCloseReady = false;
            _ = MarkAutoCloseReadyAsync();
        };
        Deactivated += (_, _) =>
        {
            if (_autoCloseReady && !_closed && !_isClosing)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_closed && !_isClosing) Close();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async Task MarkAutoCloseReadyAsync()
    {
        await Task.Delay(500);
        if (!_closed && IsActive) _autoCloseReady = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        ApplyFilter();
        FocusQuery();
    }

    public void FocusQuery()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || !IsVisible) return;
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
            QueryPlaceholder.Visibility = string.IsNullOrEmpty(QueryBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        QueryPlaceholder.Visibility = string.IsNullOrEmpty(QueryBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = QueryBox.Text.Trim();
        IEnumerable<SearchResult> filtered = _allResults;

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered
                .Where(r =>
                    r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    r.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    r.FenceTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(r => MatchRank(r, query))
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        else
        {
            filtered = filtered.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var results = filtered.Take(120).ToList();
        _view.Clear();
        foreach (var result in results)
        {
            _view.Add(result);
        }

        CountText.Text = results.Count == 0 ? "" : $"{results.Count} 项";
        EmptyState.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var result in results.Take(24))
        {
            _ = result.EnsureIconAsync();
        }

        if (results.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private static int MatchRank(SearchResult result, string query)
    {
        if (result.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (result.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 1;
        if (result.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 2;
        return 3;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up && _view.Count > 0)
        {
            var index = ResultsList.SelectedIndex;
            index += e.Key == Key.Down ? 1 : -1;
            index = Math.Clamp(index, 0, _view.Count - 1);
            ResultsList.SelectedIndex = index;
            ResultsList.ScrollIntoView(_view[index]);
            e.Handled = true;
        }
    }

    private void LaunchSelected()
    {
        if (ResultsList.SelectedItem is SearchResult result)
        {
            _host.LaunchItem(result.Item);
            Close();
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LaunchSelected();
    }

    private void ResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResult result)
        {
            _ = result.EnsureIconAsync();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _closed = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
