using HotcoinTerminal.Models;
using HotcoinTerminal.Services.Api;

namespace HotcoinTerminal.Services;

/// <summary>
/// Мозг скринера. Крутит фоновый цикл: раз в RefreshInterval забирает тикеры по всем парам
/// одним запросом, фильтрует по ликвидности/спреду, размечает эвристикой v0 и публикует
/// событие SignalsUpdated. UI подписывается и сам маршалит в свой поток.
/// Позже эвристику v0 заменит движок стратегий (IStrategy).
/// </summary>
public sealed class MarketDataService
{
    public static MarketDataService Instance { get; } = new();

    // ---- Настройки скринера ----
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private const double MinQuoteVolumeUsd = 500_000; // фильтр ликвидности, $/сутки
    private const double MaxSpreadPercent = 0.5;      // фильтр спреда, %
    private const int TopN = 60;                      // строк в скринере
    private static readonly TimeSpan KlineCacheTtl = TimeSpan.FromSeconds(60);

    private readonly HotcoinPublicClient _client = new();

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

    private MarketDataService() { }

    // ---------------- Публичный интерфейс ----------------

    /// <summary>Запуск фонового цикла. Повторные вызовы игнорируются.</summary>
    public void Start()
    {
        _loop ??= Task.Run(RunLoopAsync);
    }

    /// <summary>Принудительное обновление вне расписания (кнопка «обновить»).</summary>
    public Task ForceRefreshAsync() => RefreshOnceAsync();

    /// <summary>Тикер по паре вида "BTC/USDT" (для шапки графика).</summary>
    public TickerInfo? TryGetTicker(string pairDisplay)
    {
        lock (_lock)
            return _lastTickers.TryGetValue(ToSymbol(pairDisplay), out var t) ? t : null;
    }

    /// <summary>Свечи с минутным кэшем (клики по парам туда-сюда не бомбят API).</summary>
    public async Task<List<Candle>> GetKlinesAsync(string pairDisplay, int stepSeconds)
    {
        var key = (Symbol: ToSymbol(pairDisplay), Step: stepSeconds);
        lock (_lock)
        {
            if (_klineCache.TryGetValue(key, out var cached) &&
                DateTime.UtcNow - cached.At < KlineCacheTtl)
                return cached.Data;
        }

        var candles = await _client.GetKlinesAsync(key.Symbol, stepSeconds);
        lock (_lock) _klineCache[key] = (DateTime.UtcNow, candles);
        return candles;
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
            var rows = BuildSignals(tickers);
            _consecutiveFailures = 0;
            SignalsUpdated?.Invoke(rows, DateTime.Now);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            RefreshFailed?.Invoke($"Нет связи с API: {ex.Message}");
        }
    }

    // ---------------- Конвейер скринера ----------------

    private List<SignalRow> BuildSignals(List<TickerInfo> tickers)
    {
        HashSet<string> online;
        lock (_lock) online = _onlineUsdtSymbols;

        var rows = new List<SignalRow>();

        foreach (var t in tickers)
        {
            var symbol = t.Symbol.ToLowerInvariant();

            // Только пары к USDT (по справочнику, а если его нет — по суффиксу)
            bool isUsdt = online.Count > 0 ? online.Contains(symbol) : symbol.EndsWith("_usdt");
            if (!isUsdt || t.Last <= 0) continue;

            // Фильтр ликвидности и спреда — на неликвиде любая стратегия проигрывает комиссиям
            if (t.QuoteVolume < MinQuoteVolumeUsd) continue;
            if (t.SpreadPercent > MaxSpreadPercent) continue;

            var (strategy, score) = ClassifyV0(t, VolumeAnomaly(symbol, t.QuoteVolume));

            rows.Add(new SignalRow
            {
                Pair = ToDisplay(symbol),
                Strategy = strategy,
                Score = score,
                ChangePercent = t.Change
            });
        }

        lock (_lock)
        {
            _lastTickers.Clear();
            foreach (var t in tickers) _lastTickers[t.Symbol.ToLowerInvariant()] = t;
        }

        return rows
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => Math.Abs(r.ChangePercent))
            .Take(TopN)
            .ToList();
    }

    /// <summary>
    /// ЭВРИСТИКА v0 (заглушка до движка стратегий): разметка по одному тикеру.
    /// Сильное падение -> кандидат Mean Rev, рост на аномальном объёме -> Momentum,
    /// узкий дневной диапазон -> Grid.
    /// </summary>
    private static (string Strategy, int Score) ClassifyV0(TickerInfo t, double volumeAnomaly)
    {
        double ch = t.Change;
        double rangePct = t.Last > 0 && t.High > t.Low ? (t.High - t.Low) / t.Last * 100.0 : 0;

        if (ch <= -4)
        {
            int score = 55 + (int)Math.Min(Math.Abs(ch) * 3, 30);
            return ("Mean Rev", Math.Min(score, 95));
        }

        if (ch >= 4)
        {
            int score = 55 + (int)Math.Min(ch * 2, 25) + (volumeAnomaly > 1.5 ? 10 : 0);
            return ("Momentum", Math.Min(score, 95));
        }

        // Боковик: чем уже диапазон при живом объёме, тем интереснее для сетки
        int gridScore = (int)Math.Clamp(68 - rangePct * 4, 40, 72);
        return ("Grid", gridScore);
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
