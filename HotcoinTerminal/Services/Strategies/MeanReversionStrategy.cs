namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Возврат к среднему: перепроданность на часовиках.
/// Условия: RSI(14) низкий, закрытие ушло от SMA20 на 1.5+ сигмы вниз,
/// цена у дна 48-часового диапазона. Чем экстремальнее — тем выше скор,
/// но «нож» (падение 24ч глубже −15%) отсекаем: там перепроданность лишь усиливается.
/// </summary>
public sealed class MeanReversionStrategy : IStrategy
{
    public string Name => "Mean Rev";

    private const double MaxRsi = 35;         // выше — не перепроданность
    private const double MinZScore = -1.5;    // отклонение вниз хотя бы на 1.5 сигмы
    private const double KnifeChange = -15;   // глубже за 24ч — «падающий нож», пропускаем

    public StrategySignal? Evaluate(MarketSnapshot s)
    {
        if (s.Candles.Count < 30) return null;

        double rsi = s.Rsi14;
        double z = s.ZScore;

        if (rsi > MaxRsi) return null;
        if (z > MinZScore) return null;
        if (s.Ticker.Change < KnifeChange) return null;

        // Скоринг: база 50 + глубина RSI (до +20) + |z| (до +20) + положение у дна диапазона (до +10)
        int score = 50
            + (int)Math.Min((MaxRsi - rsi) / MaxRsi * 20 * 2, 20)          // rsi 35 -> 0, rsi 17.5 -> 20
            + (int)Math.Min((Math.Abs(z) - 1.5) / 1.5 * 20, 20)            // z -1.5 -> 0, z -3 -> 20
            + (int)((1.0 - Math.Min(s.RangePosition48 / 0.25, 1.0)) * 10); // у самого дна -> +10

        return new StrategySignal
        {
            Strategy = Name,
            Score = Math.Clamp(score, 0, 100),
            Note = $"Перепроданность: RSI {rsi:0}, {z:0.0}σ от SMA20, позиция в диапазоне 48ч {s.RangePosition48:P0}, 24ч {s.Ticker.Change:+0.0;-0.0}%"
        };
    }
}
