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

        // Один экземпляр страницы на всё время жизни приложения:
        // без этого каждый возврат на вкладку создавал бы новый WebView2
        // (и новый процесс msedgewebview2.exe) — классическая утечка.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

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
            {
                Chart.SetCandles(pair, candles);
                SyncIndicatorToggles(pair);
            }
        }
        catch (Exception ex)
        {
            ScreenerStatus.Text = $"Свечи не загрузились: {ex.Message}";
        }
    }

    // ---------- Поиск по всем парам Hotcoin (не только из скринера) ----------

    private void OnPairSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = sender.Text.Trim();
        sender.ItemsSource = query.Length == 0
            ? null
            : MarketDataService.Instance.GetAllPairs()
                .Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .ToList();
    }

    private void OnPairSearchChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string pair) _ = ShowPairAsync(pair);
    }

    private void OnPairSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var pair = args.ChosenSuggestion as string
                   ?? MarketDataService.Instance.GetAllPairs()
                       .FirstOrDefault(p => p.Contains(sender.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pair is not null) _ = ShowPairAsync(pair);
    }

    /// <summary>Открыть произвольную пару на графике (в обход выбора в скринере).</summary>
    private async Task ShowPairAsync(string pair)
    {
        PairSearchBox.Text = "";
        SignalsList.SelectedItem = null; // визуально отвязываем от скринера

        PairTitle.Text = pair;
        var ticker = MarketDataService.Instance.TryGetTicker(pair);
        if (ticker is not null)
        {
            PairPrice.Text = FormatPrice(ticker.Last);
            var brush = ticker.Change >= 0 ? Helpers.Palette.UpBrush : Helpers.Palette.DownBrush;
            PairChange.Text = (ticker.Change >= 0 ? "+" : "") + ticker.Change.ToString("0.0") + "%";
            PairChange.Foreground = brush;
            PairPrice.Foreground = brush;
        }
        else
        {
            PairPrice.Text = "—";
            PairChange.Text = "";
        }

        await LoadKlinesAsync(pair);
    }

    // ---------- Индикаторы (галочки в меню = раскладка текущей пары) ----------

    private void OnIndicatorToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string tag } item) return;

        // Tag: "id|type|paramsJson"
        var parts = tag.Split('|', 3);
        if (parts.Length != 3) return;

        if (item.IsChecked) Chart.AddIndicator(parts[0], parts[1], parts[2]);
        else Chart.RemoveIndicator(parts[0]);
    }

    /// <summary>После смены пары выставить галочки по её сохранённой раскладке.</summary>
    private void SyncIndicatorToggles(string pair)
    {
        var layout = ChartSettingsStore.Instance.LoadLayout(pair) ?? ChartSettingsStore.DefaultLayout;

        foreach (var item in new[] { IndMa10, IndMa20, IndEma50, IndBB, IndRsi14 })
        {
            var id = (item.Tag as string)?.Split('|', 2)[0] ?? "";
            item.IsChecked = layout.Contains($"\"{id}\"", StringComparison.Ordinal);
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
