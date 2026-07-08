using HotcoinTerminal.Models;
using HotcoinTerminal.Services.Api;
using HotcoinTerminal.Services.Strategies;

namespace HotcoinTerminal.Services;

/// <summary>
/// Мозг скринера. Цикл: раз в RefreshInterval забирает тикеры одним запросом,
/// фильтрует по ликвидности/спреду, дешёвым пре-скорингом отбирает топ-кандидатов,
/// и только для них движок стратегий (StrategyEngine) подтягивает часовые свечи
/// и считает настоящие индикаторы. Итог публикуется событием SignalsUpdated.
/// </summary>
public sealed class MarketDataService
{
    public static MarketDataService Instance { get; } = new();

    // ---- Настройки скринера ----
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private const double MinQuoteVolumeUsd = 500_000; // фильтр ликвидности, $/сутки
    private const double MaxSpreadPercent = 0.5;      // фильтр спреда, %
    private const int MaxDeepCandidates = 40;         // скольким парам грузим свечи за цикл
    private const int TopN = 60;                      // строк в скринере
    private static readonly TimeSpan KlineCacheTtl = TimeSpan.FromSeconds(60);

    private readonly HotcoinPublicClient _client = new();
    private readonly StrategyEngine _engine;
    private readonly EventFeedService _eventFeed = new();
    private readonly AnnouncementFeedService _announcements = new();

    private HashSet<string> _onlineUsdtSymbols = new();
    private readonly Dictionary<string, TickerInfo> _lastTickers = new();
    private readonly Dictionary<string, Queue<double>> _volumeHistory = new(); // для аномалий объёма
    private readonly Dictionary<(string Symbol, int Step), (DateTime At, List<Candle> Data)> _klineCache = new();
    private readonly object _lock = new();

    private Task? _loop;
    private int _consecutiveFailures;

    /// <summary>Свежие строки скринера + время обновления. Подписчик сам уходит в UI-поток.</summary>
    public event Action<IReadOnlyList<SignalRow>, DateTime>? SignalsUpdated;

    /// <summary>Ошибка цикла (текст для статус-бара).</summary>
    public event Action<string>? RefreshFailed;

    /// <summary>Лента событий скринера (новые сверху). Подписчик сам уходит в UI-поток.</summary>
    public event Action<IReadOnlyList<EventItem>>? EventsUpdated;

    private MarketDataService()
    {
        // Движку отдаём «сырой» метод клиента: у движка свой кэш (TTL 5 мин),
        // а _klineCache с TTL 60с остаётся для графика (там нужна свежесть).
        _engine = new StrategyEngine((symbol, step, ct) => _client.GetKlinesAsync(symbol, step, ct: ct));

        // Анонсы биржи вливаются в общую ленту событий
        _announcements.AnnouncementsArrived += items =>
            EventsUpdated?.Invoke(_eventFeed.AddExternal(items));
    }

    // ---------------- Публичный интерфейс ----------------

    /// <summary>Запуск фонового цикла. Повторные вызовы игнорируются.</summary>
    public void Start()
    {
        _loop ??= Task.Run(RunLoopAsync);
        _announcements.Start();
    }

    /// <summary>Принудительное обновление вне расписания (кнопка «обновить»).</summary>
    public Task ForceRefreshAsync() => RefreshOnceAsync();

    /// <summary>Тикер по паре вида "BTC/USDT" (для шапки графика).</summary>
    public TickerInfo? TryGetTicker(string pairDisplay)
    {
        lock (_lock)
            return _lastTickers.TryGetValue(ToSymbol(pairDisplay), out var t) ? t : null;
    }

    /// <summary>Свечи с минутным кэшем (клики по парам туда-сюда не бомбят API).
    /// maxCount: для графика берём максимум, что отдаёт биржа (по умолчанию 1000).</summary>
    public async Task<List<Candle>> GetKlinesAsync(string pairDisplay, int stepSeconds, int maxCount = 1000)
    {
        var key = (Symbol: ToSymbol(pairDisplay), Step: stepSeconds);
        lock (_lock)
        {
            if (_klineCache.TryGetValue(key, out var cached) &&
                DateTime.UtcNow - cached.At < KlineCacheTtl)
                return cached.Data;
        }

        var candles = await _client.GetKlinesAsync(key.Symbol, stepSeconds, maxCount);
        lock (_lock) _klineCache[key] = (DateTime.UtcNow, candles);
        return candles;
    }

    /// <summary>
    /// Все доступные USDT-пары в отображаемом виде ("BTC/USDT") для поиска над графиком —
    /// независимо от того, прошли ли они фильтры скринера.
    /// Источник: справочник /v1/common/symbols; если он не загрузился — последние тикеры.
    /// </summary>
    public IReadOnlyList<string> GetAllPairs()
    {
        lock (_lock)
        {
            var source = _onlineUsdtSymbols.Count > 0
                ? _onlineUsdtSymbols.AsEnumerable()
                : _lastTickers.Keys.Where(s => s.EndsWith("_usdt"));

            return source.Select(ToDisplay).OrderBy(p => p).ToList();
        }
    }

    public static string ToSymbol(string pairDisplay) =>
        pairDisplay.Replace("/", "_").ToLowerInvariant();     // BTC/USDT -> btc_usdt

    public static string ToDisplay(string symbol) =>
        symbol.Replace("_", "/").ToUpperInvariant();          // btc_usdt -> BTC/USDT

    // ---------------- Цикл ----------------

    private async Task RunLoopAsync()
    {
        await LoadSymbolsAsync();

        while (true)
        {
            await RefreshOnceAsync();

            // Экспоненциальная пауза при повторных сбоях: 15с -> 30с -> 60с (макс)
            var delay = _consecutiveFailures switch
            {
                0 => RefreshInterval,
                1 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromSeconds(60)
            };
            await Task.Delay(delay);
        }
    }

    private async Task LoadSymbolsAsync()
    {
        try
        {
            var symbols = await _client.GetSymbolsAsync();
            var online = symbols
                .Where(s => s.QuoteCurrency.Equals("usdt", StringComparison.OrdinalIgnoreCase))
                .Where(s => s.State.Length == 0 ||
                            s.State.Contains("online", StringComparison.OrdinalIgnoreCase) ||
                            s.State.Contains("enable", StringComparison.OrdinalIgnoreCase) ||
                            s.State == "1")
                .Select(s => s.Symbol.ToLowerInvariant())
                .ToHashSet();

            lock (_lock) _onlineUsdtSymbols = online;
        }
        catch (Exception ex)
        {
            // Не смертельно: без справочника отфильтруем по суффиксу _usdt из тикеров
            RefreshFailed?.Invoke($"Справочник пар недоступен: {ex.Message}");
        }
    }

    private async Task RefreshOnceAsync()
    {
        try
        {
            var tickers = await _client.GetAllTickersAsync();
            var candidates = SelectCandidates(tickers);
            var rows = await _engine.EvaluateAsync(candidates);

            var top = rows.OrderByDescending(r => r.Score)
                          .ThenByDescending(r => Math.Abs(r.ChangePercent))
                          .Take(TopN)
                          .ToList();

            _consecutiveFailures = 0;
            SignalsUpdated?.Invoke(top, DateTime.Now);
            EventsUpdated?.Invoke(_eventFeed.Process(top, DateTime.Now));
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            RefreshFailed?.Invoke($"Нет связи с API: {ex.Message}");
        }
    }

    // ---------------- Отбор кандидатов (дешёвый этап, только тикеры) ----------------

    /// <summary>
    /// Фильтр ликвидности/спреда + пре-скоринг по тикеру.
    /// В движок уходят максимум MaxDeepCandidates самых «интересных» пар —
    /// это ограничивает число запросов свечей (у движка ещё и кэш 5 мин).
    /// </summary>
    private List<(string Symbol, TickerInfo Ticker, double VolumeAnomaly)> SelectCandidates(
        List<TickerInfo> tickers)
    {
        HashSet<string> online;
        lock (_lock) online = _onlineUsdtSymbols;

        var pre = new List<(string Symbol, TickerInfo Ticker, double Anomaly, double PreScore)>();

        foreach (var t in tickers)
        {
            var symbol = t.Symbol.ToLowerInvariant();

            // Только пары к USDT (по справочнику, а если его нет — по суффиксу)
            bool isUsdt = online.Count > 0 ? online.Contains(symbol) : symbol.EndsWith("_usdt");
            if (!isUsdt || t.Last <= 0) continue;

            // Фильтр ликвидности и спреда — на неликвиде любая стратегия проигрывает комиссиям
            if (t.QuoteVolume < MinQuoteVolumeUsd) continue;
            if (t.SpreadPercent > MaxSpreadPercent) continue;

            double anomaly = VolumeAnomaly(symbol, t.QuoteVolume);

            // Пре-скоринг: движение + аномалия оборота + ликвидность.
            // Это не сигнал, а приоритет очереди на глубокий анализ.
            double preScore =
                Math.Min(Math.Abs(t.Change), 20) * 2.0 +
                Math.Min(Math.Max(anomaly - 1.0, 0) * 10, 20) +
                Math.Min(Math.Log10(Math.Max(t.QuoteVolume, 1)) * 2, 16);

            pre.Add((symbol, t, anomaly, preScore));
        }

        lock (_lock)
        {
            _lastTickers.Clear();
            foreach (var t in tickers) _lastTickers[t.Symbol.ToLowerInvariant()] = t;
        }

        // 25 слотов «движущимся» + 15 слотов тихим ликвидным парам (кандидаты Grid),
        // иначе весь бюджет глубокого анализа съедают пары с большим |change|
        var movers = pre
            .OrderByDescending(p => p.PreScore)
            .Take(25);

        var quiet = pre
            .Where(p => Math.Abs(p.Ticker.Change) <= 3.0)
            .OrderByDescending(p => p.Ticker.QuoteVolume)
            .Take(15);

        return movers.Concat(quiet)
            .DistinctBy(p => p.Symbol)
            .Take(MaxDeepCandidates)
            .Select(p => (p.Symbol, p.Ticker, p.Anomaly))
            .ToList();
    }

    /// <summary>Отношение текущего оборота к среднему за последние циклы (внутри сессии).</summary>
    private double VolumeAnomaly(string symbol, double quoteVolume)
    {
        lock (_lock)
        {
            if (!_volumeHistory.TryGetValue(symbol, out var q))
                _volumeHistory[symbol] = q = new Queue<double>();

            double anomaly = 1.0;
            if (q.Count >= 4)
            {
                double avg = q.Average();
                if (avg > 0) anomaly = quoteVolume / avg;
            }

            q.Enqueue(quoteVolume);
            while (q.Count > 40) q.Dequeue();
            return anomaly;
        }
    }
}
