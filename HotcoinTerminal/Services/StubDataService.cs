using HotcoinTerminal.Models;

namespace HotcoinTerminal.Services;

/// <summary>
/// Временный источник данных. На следующем этапе будет заменён сервисным слоем
/// Hotcoin API (REST + WebSocket) с тем же набором методов.
/// </summary>
public static class StubDataService
{
    /// <summary>Случайное блуждание: падение ~55% ряда, затем восстановление.</summary>
    public static List<Candle> GenerateCandles(int count, int seed = 11, double basePrice = 63000)
    {
        var rnd = new Random(seed);
        var list = new List<Candle>(count);
        double p = basePrice * 1.04;

        for (int i = 0; i < count; i++)
        {
            double drift = i < count * 0.55 ? -0.010 : 0.013;
            double open = p;
            double close = p * (1 + drift + (rnd.NextDouble() - 0.5) * 0.05);
            double high = Math.Max(open, close) * (1 + rnd.NextDouble() * 0.012);
            double low = Math.Min(open, close) * (1 - rnd.NextDouble() * 0.012);
            double vol = rnd.NextDouble() * (Math.Abs(close - open) / p > 0.02 ? 1.6 : 0.8);

            list.Add(new Candle { Open = open, High = high, Low = low, Close = close, Volume = vol });
            p = close;
        }
        return list;
    }

    public static List<SignalRow> GenerateSignals() => new()
    {
        new() { Pair = "BTC/USDT",  Strategy = "Mean Rev",  Score = 84, ChangePercent = 2.1 },
        new() { Pair = "HYPE/USDT", Strategy = "Momentum",  Score = 81, ChangePercent = 6.4 },
        new() { Pair = "SOL/USDT",  Strategy = "Grid",      Score = 76, ChangePercent = 1.2 },
        new() { Pair = "NEAR/USDT", Strategy = "События",   Score = 73, ChangePercent = 2.9 },
        new() { Pair = "ETH/USDT",  Strategy = "Mean Rev",  Score = 69, ChangePercent = 1.3 },
        new() { Pair = "ALLO/USDT", Strategy = "Momentum",  Score = 66, ChangePercent = 41.2 },
        new() { Pair = "XRP/USDT",  Strategy = "Grid",      Score = 58, ChangePercent = -1.6 },
        new() { Pair = "TRX/USDT",  Strategy = "Grid",      Score = 55, ChangePercent = 0.4 },
        new() { Pair = "DOGE/USDT", Strategy = "Mean Rev",  Score = 47, ChangePercent = -0.8 },
        new() { Pair = "AAVE/USDT", Strategy = "События",   Score = 44, ChangePercent = 2.2 },
    };

    public static List<EventItem> GenerateEvents() => new()
    {
        new() { Time = "14:32", Message = "ALLO/USDT — всплеск объёма ×3.4, пробой локального максимума", Tag = "Momentum" },
        new() { Time = "14:17", Message = "BTC/USDT — RSI вышел из зоны перепроданности, сетап на отскок", Tag = "Mean Rev" },
        new() { Time = "13:58", Message = "NEAR/USDT — анонс 7 июля: повышенная активность, следить за входом", Tag = "События" },
        new() { Time = "13:41", Message = "XRP/USDT — цена покинула диапазон сетки, стратегия на паузе", Tag = "Grid" },
    };
}
