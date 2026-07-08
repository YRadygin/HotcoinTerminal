using HotcoinTerminal.Models;
using HotcoinTerminal.Services.Api;

namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Снимок рынка по одной паре — всё, что нужно стратегии для оценки.
/// Индикаторы считаются один раз здесь и переиспользуются всеми стратегиями.
/// </summary>
public sealed class MarketSnapshot
{
    public required string Symbol { get; init; }          // btc_usdt
    public required TickerInfo Ticker { get; init; }
    public required IReadOnlyList<Candle> Candles { get; init; } // часовые свечи, старые -> новые

    /// <summary>Аномалия оборота: текущий 24ч-оборот к среднему за сессию (1.0 = норма).</summary>
    public double VolumeAnomaly { get; init; } = 1.0;

    // ---- Ленивая инициализация индикаторов (считаются при первом обращении) ----

    private double? _rsi, _sma20, _std20, _atrPct, _rangePos;

    /// <summary>RSI(14) по закрытиям часовых свечей.</summary>
    public double Rsi14 => _rsi ??= Indicators.Rsi(Candles, 14);

    /// <summary>SMA(20) по закрытиям.</summary>
    public double Sma20 => _sma20 ??= Indicators.Sma(Candles, 20);

    /// <summary>Стандартное отклонение закрытий за 20 свечей.</summary>
    public double Std20 => _std20 ??= Indicators.StdDev(Candles, 20);

    /// <summary>ATR(14) в процентах от последней цены — «дыхание» пары.</summary>
    public double AtrPercent => _atrPct ??= Indicators.AtrPercent(Candles, 14);

    /// <summary>Позиция цены внутри диапазона последних 48ч: 0 = у дна, 1 = у вершины.</summary>
    public double RangePosition48 => _rangePos ??= Indicators.RangePosition(Candles, 48);

    /// <summary>Z-score: на сколько сигм закрытие отклонилось от SMA20. Минус = ниже среднего.</summary>
    public double ZScore
    {
        get
        {
            if (Candles.Count == 0 || Std20 <= 0) return 0;
            return (Candles[^1].Close - Sma20) / Std20;
        }
    }
}

/// <summary>Результат оценки пары одной стратегией.</summary>
public sealed class StrategySignal
{
    public required string Strategy { get; init; }  // имя для чипов скринера: Grid / Mean Rev / Momentum
    public required int Score { get; init; }        // 0..100
    public string Note { get; init; } = "";         // краткое обоснование — пойдёт в AI-анализ
}

/// <summary>Стратегия скринера: смотрит на снимок рынка и решает, есть ли сетап.</summary>
public interface IStrategy
{
    /// <summary>Имя, совпадающее с чипом-фильтром в UI.</summary>
    string Name { get; }

    /// <summary>Оценка пары. null — сетапа нет, пара стратегии не интересна.</summary>
    StrategySignal? Evaluate(MarketSnapshot snapshot);
}
