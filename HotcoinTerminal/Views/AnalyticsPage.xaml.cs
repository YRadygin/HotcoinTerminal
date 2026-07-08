using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using HotcoinTerminal.Models;
using HotcoinTerminal.Services;

namespace HotcoinTerminal.Views;

public sealed partial class AnalyticsPage : Page
{
    private readonly ObservableCollection<SignalRow> _visibleSignals = new();
    private readonly ObservableCollection<EventItem> _events = new();
    private IReadOnlyList<EventItem> _allEvents = Array.Empty<EventItem>();
    private IReadOnlyList<SignalRow> _allSignals = Array.Empty<SignalRow>();
    private readonly HashSet<string> _activeStrategies = new() { "Grid", "Mean Rev", "Momentum", "События" };
    private string _searchText = "";
    private int _stepSeconds = 3600; // текущий таймфрейм, сек
    private bool _subscribed;

    public AnalyticsPage()
    {
        InitializeComponent();

        SignalsList.ItemsSource = _visibleSignals;
        EventsList.ItemsSource = _events; // лента событий 


        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                MarketDataService.Instance.EventsUpdated += OnEventsUpdated;
                MarketDataService.Instance.SignalsUpdated += OnSignalsUpdated;
                MarketDataService.Instance.RefreshFailed += OnRefreshFailed;
                _subscribed = true;
            }
            MarketDataService.Instance.Start();
        };

        Unloaded += (_, _) =>
        {
            if (_subscribed)
            {
                MarketDataService.Instance.EventsUpdated -= OnEventsUpdated;
                MarketDataService.Instance.SignalsUpdated -= OnSignalsUpdated;
                MarketDataService.Instance.RefreshFailed -= OnRefreshFailed;
                _subscribed = false;
            }
        };
    }

    private void OnEventsUpdated(IReadOnlyList<EventItem> events)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _allEvents = events;
            RefreshEventsFilter();
        });
    }

    /// <summary>Вкл (зелёный) — вся лента; выкл (красный) — только анонсы биржи.</summary>
    private void RefreshEventsFilter()
    {
        bool showAll = EventsFilterToggle.IsChecked == true;
        _events.Clear();
        foreach (var e in _allEvents)
            if (showAll || e.Tag == "События")
                _events.Add(e);
    }

    private void OnEventsFilterToggle(object sender, RoutedEventArgs e)
    {
        EventsFilterToggle.Foreground = EventsFilterToggle.IsChecked == true
            ? Helpers.Palette.UpBrush
            : Helpers.Palette.DownBrush;
        RefreshEventsFilter();
    }

    // ---------- Приём данных из MarketDataService (фоновый поток -> UI-поток) ----------

    private void OnSignalsUpdated(IReadOnlyList<SignalRow> rows, DateTime at)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var selectedPair = (SignalsList.SelectedItem as SignalRow)?.Pair;

            _allSignals = rows;
            RefreshFilter();
            ScreenerStatus.Text = $"{rows.Count} пар прошло фильтры · обновлено {at:HH:mm:ss}";

            // Восстановить выбранную пару после обновления списка
            var restored = _visibleSignals.FirstOrDefault(r => r.Pair == selectedPair);
            if (restored is not null)
                SignalsList.SelectedItem = restored;
            else if (SignalsList.SelectedItem is null && _visibleSignals.Count > 0)
                SignalsList.SelectedIndex = 0;
        });
    }

    private void OnRefreshFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() => ScreenerStatus.Text = message);
    }

    // ---------- Фильтрация скринера (локально, без запросов) ----------

    private void RefreshFilter()
    {
        _visibleSignals.Clear();
        foreach (var s in _allSignals)
        {
            if (!_activeStrategies.Contains(s.Strategy)) continue;
            if (_searchText.Length > 0 &&
                !s.Pair.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleSignals.Add(s);
        }
    }

    private void OnStrategyChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string strategy } chip)
        {
            if (chip.IsChecked == true) _activeStrategies.Add(strategy);
            else _activeStrategies.Remove(strategy);
            RefreshFilter();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        RefreshFilter();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ScreenerStatus.Text = "Обновление…";
        await MarketDataService.Instance.ForceRefreshAsync();
    }

    // ---------- Выбор пары -> шапка + свечи ----------

    private async void OnSignalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SignalsList.SelectedItem is not SignalRow row) return;

        PairTitle.Text = row.Pair;
        PairChange.Text = row.ChangeText;
        PairChange.Foreground = row.ChangeBrush;
        PairPrice.Foreground = row.ChangeBrush;

        var ticker = MarketDataService.Instance.TryGetTicker(row.Pair);
        if (ticker is not null)
            PairPrice.Text = FormatPrice(ticker.Last);

        await LoadKlinesAsync(row.Pair);
    }

    private async Task LoadKlinesAsync(string pair)
    {
        try
        {
            var candles = await MarketDataService.Instance.GetKlinesAsync(pair, _stepSeconds);

            // Пока грузили, пользователь мог кликнуть другую пару — не перерисовываем чужое
            if (candles.Count > 0 && PairTitle.Text == pair)
                Chart.SetCandles(candles);
        }
        catch (Exception ex)
        {
            ScreenerStatus.Text = $"Свечи не загрузились: {ex.Message}";
        }
    }

    // ---------- Таймфреймы (Tag = шаг в секундах) ----------

    private async void OnTimeframeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        if (clicked.Parent is StackPanel panel)
        {
            foreach (var child in panel.Children)
                if (child is ToggleButton tb && !ReferenceEquals(tb, clicked))
                    tb.IsChecked = false;
        }
        clicked.IsChecked = true;

        if (int.TryParse(clicked.Tag?.ToString(), out var step))
            _stepSeconds = step;

        await LoadKlinesAsync(PairTitle.Text);
    }

    // ---------- Углублённый AI-анализ ----------

    private void OnDeepAnalysisClick(object sender, RoutedEventArgs e)
    {
        var window = new DeepAnalysisWindow(PairTitle.Text);
        window.Activate();
    }

    // ---------- Хелперы ----------

    private static string FormatPrice(double p) => p switch
    {
        >= 1000 => p.ToString("N1"),
        >= 1 => p.ToString("N4"),
        _ => p.ToString("0.########")
    };
}
