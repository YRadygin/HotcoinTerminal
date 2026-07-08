using HotcoinTerminal.Models;

namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Чистая математика индикаторов по списку свечей (старые -> новые).
/// Без внешних зависимостей; при нехватке данных возвращаются нейтральные значения.
/// </summary>
public static class Indicators
{
    /// <summary>Простая скользящая средняя закрытий за period свечей. 0, если данных мало.</summary>
    public static double Sma(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count < period || period <= 0) return 0;
        double sum = 0;
        for (int i = c.Count - period; i < c.Count; i++) sum += c[i].Close;
        return sum / period;
    }

    /// <summary>Стандартное отклонение закрытий за period свечей.</summary>
    public static double StdDev(IReadOnlyList<Candle> c, int period)
    {
        if (c.Count < period || period <= 1) return 0;
        double mean = Sma(c, period), sum = 0;
        for (int i = c.Count - period; i < c.Count; i++)
        {
            double d = c[i].Close - mean;
            sum += d * d;
        }
        return Math.Sqrt(sum / period);
    }

    /// <summary>RSI по Уайлдеру. 50 (нейтрально), если данных мало.</summary>
    public static double Rsi(IReadOnlyList<Candle> c, int period = 14)
    {
        if (c.Count < period + 1) return 50;

        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = c[i].Close - c[i - 1].Close;
            if (d >= 0) gain += d; else loss -= d;
        }
        gain /= period; loss /= period;

        for (int i = period + 1; i < c.Count; i++)
        {
            double d = c[i].Close - c[i - 1].Close;
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
        }

        if (loss <= 0) return 100;
        double rs = gain / loss;
        return 100 - 100 / (1 + rs);
    }

    /// <summary>ATR(period) в процентах от последней цены. 0, если данных мало.</summary>
    public static double AtrPercent(IReadOnlyList<Candle> c, int period = 14)
    {
        if (c.Count < period + 1) return 0;

        double atr = 0;
        for (int i = c.Count - period; i < c.Count; i++) atr += TrueRange(c, i);
        atr /= period;

        double last = c[^1].Close;
        return last > 0 ? atr / last * 100.0 : 0;
    }

    private static double TrueRange(IReadOnlyList<Candle> c, int i)
    {
        double prevClose = c[i - 1].Close;
        return Math.Max(c[i].High - c[i].Low,
               Math.Max(Math.Abs(c[i].High - prevClose), Math.Abs(c[i].Low - prevClose)));
    }

    /// <summary>Позиция последнего закрытия в диапазоне последних lookback свечей: 0 = дно, 1 = вершина.</summary>
    public static double RangePosition(IReadOnlyList<Candle> c, int lookback)
    {
        if (c.Count == 0) return 0.5;
        int from = Math.Max(0, c.Count - lookback);

        double hi = double.MinValue, lo = double.MaxValue;
        for (int i = from; i < c.Count; i++)
        {
            hi = Math.Max(hi, c[i].High);
            lo = Math.Min(lo, c[i].Low);
        }
        if (hi <= lo) return 0.5;
        return Math.Clamp((c[^1].Close - lo) / (hi - lo), 0, 1);
    }

    /// <summary>Максимум High за lookback свечей, НЕ считая последних skipLast (для проверки пробоя).</summary>
    public static double HighestHigh(IReadOnlyList<Candle> c, int lookback, int skipLast = 1)
    {
        int end = c.Count - skipLast;
        int from = Math.Max(0, end - lookback);
        double hi = 0;
        for (int i = from; i < end; i++) hi = Math.Max(hi, c[i].High);
        return hi;
    }

    /// <summary>Средний объём за period свечей, не считая последних skipLast.</summary>
    public static double AverageVolume(IReadOnlyList<Candle> c, int period, int skipLast = 1)
    {
        int end = c.Count - skipLast;
        int from = Math.Max(0, end - period);
        if (end <= from) return 0;
        double sum = 0;
        for (int i = from; i < end; i++) sum += c[i].Volume;
        return sum / (end - from);
    }

    /// <summary>Число пересечений ценой её SMA(period) за lookback свечей — мера «пилы» (хорошо для сетки).</summary>
    public static int SmaCrossCount(IReadOnlyList<Candle> c, int period, int lookback)
    {
        if (c.Count < period + 2) return 0;
        int from = Math.Max(period, c.Count - lookback);
        int crosses = 0;

        for (int i = from; i < c.Count; i++)
        {
            double smaPrev = SmaAt(c, period, i - 1);
            double smaCur = SmaAt(c, period, i);
            bool wasAbove = c[i - 1].Close >= smaPrev;
            bool isAbove = c[i].Close >= smaCur;
            if (wasAbove != isAbove) crosses++;
        }
        return crosses;
    }

    private static double SmaAt(IReadOnlyList<Candle> c, int period, int endIndexInclusive)
    {
        int from = endIndexInclusive - period + 1;
        if (from < 0) return c[endIndexInclusive].Close;
        double sum = 0;
        for (int i = from; i <= endIndexInclusive; i++) sum += c[i].Close;
        return sum / period;
    }
}
