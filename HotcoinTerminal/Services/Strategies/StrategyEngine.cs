using HotcoinTerminal.Models;
using HotcoinTerminal.Services.Api;

namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Движок стратегий. Получает кандидатов (тикеры, прошедшие фильтры ликвидности),
/// для каждого подтягивает часовые свечи (свой кэш, TTL 5 мин, не более 4 запросов параллельно),
/// прогоняет все стратегии и возвращает лучший сигнал по паре.
/// Ограничение по числу кандидатов защищает от лимитов API (20 зап/с у Hotcoin).
/// </summary>
public sealed class StrategyEngine
{
    private const int KlineStepSeconds = 3600;                       // анализ на часовых свечах
    private static readonly TimeSpan KlineTtl = TimeSpan.FromMinutes(5);
    private const int MaxParallelFetches = 4;

    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly Func<string, int, CancellationToken, Task<List<Candle>>> _fetchKlines;

    private readonly Dictionary<string, (DateTime At, List<Candle> Data)> _cache = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _throttle = new(MaxParallelFetches);

    public StrategyEngine(Func<string, int, CancellationToken, Task<List<Candle>>> fetchKlines,
        IEnumerable<IStrategy>? strategies = null)
    {
        _fetchKlines = fetchKlines;
        _strategies = (strategies ?? DefaultStrategies()).ToList();
    }

    public static IEnumerable<IStrategy> DefaultStrategies() => new IStrategy[]
    {
        new GridStrategy(),
        new MeanReversionStrategy(),
        new MomentumStrategy()
        // Событийная стратегия появится вместе с календарём анлоков/листингов
    };

    /// <summary>
    /// Оценивает кандидатов. candidates: символ + тикер + аномалия оборота.
    /// Возвращает лучший сигнал по каждой паре, где хоть одна стратегия увидела сетап.
    /// </summary>
    public async Task<List<SignalRow>> EvaluateAsync(
        IReadOnlyList<(string Symbol, TickerInfo Ticker, double VolumeAnomaly)> candidates,
        CancellationToken ct = default)
    {
        var tasks = candidates.Select(c => EvaluateOneAsync(c.Symbol, c.Ticker, c.VolumeAnomaly, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    private async Task<SignalRow?> EvaluateOneAsync(string symbol, TickerInfo ticker,
        double volumeAnomaly, CancellationToken ct)
    {
        List<Candle>? candles = await GetKlinesCachedAsync(symbol, ct);
        if (candles is null || candles.Count == 0) return null;

        var snapshot = new MarketSnapshot
        {
            Symbol = symbol,
            Ticker = ticker,
            Candles = candles,
            VolumeAnomaly = volumeAnomaly
        };

        StrategySignal? best = null;
        foreach (var strategy in _strategies)
        {
            StrategySignal? signal;
            try { signal = strategy.Evaluate(snapshot); }
            catch { continue; } // одна сломавшаяся стратегия не валит весь цикл

            if (signal is not null && (best is null || signal.Score > best.Score))
                best = signal;
        }

        if (best is null) return null;

        return new SignalRow
        {
            Pair = MarketDataService.ToDisplay(symbol),
            Strategy = best.Strategy,
            Score = best.Score,
            ChangePercent = ticker.Change,
            Note = best.Note
        };
    }

    private async Task<List<Candle>?> GetKlinesCachedAsync(string symbol, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(symbol, out var cached) && DateTime.UtcNow - cached.At < KlineTtl)
                return cached.Data;
        }

        await _throttle.WaitAsync(ct);
        try
        {
            // Повторная проверка: пока ждали семафор, свечи мог загрузить другой поток
            lock (_lock)
            {
                if (_cache.TryGetValue(symbol, out var cached) && DateTime.UtcNow - cached.At < KlineTtl)
                    return cached.Data;
            }

            var candles = await _fetchKlines(symbol, KlineStepSeconds, ct);
            lock (_lock)
            {
                _cache[symbol] = (DateTime.UtcNow, candles);

                // Не даём кэшу расти бесконечно: выкидываем протухшие записи
                if (_cache.Count > 300)
                {
                    var stale = _cache.Where(kv => DateTime.UtcNow - kv.Value.At > KlineTtl)
                                      .Select(kv => kv.Key).ToList();
                    foreach (var k in stale) _cache.Remove(k);
                }
            }
            return candles;
        }
        catch
        {
            return null; // пара без свечей просто не попадёт в выдачу этого цикла
        }
        finally
        {
            _throttle.Release();
        }
    }
}
