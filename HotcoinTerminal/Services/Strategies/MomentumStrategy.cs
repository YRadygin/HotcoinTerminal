namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Импульс: всплеск объёма + пробой локального максимума.
/// Условия: объём последних 3 часовых свечей заметно выше среднего за 48ч,
/// закрытие пробило максимум предыдущих 24 свечей, суточное изменение положительное.
/// Дополнительный вес даёт аномалия суточного оборота из скринера.
/// </summary>
public sealed class MomentumStrategy : IStrategy
{
    public string Name => "Momentum";

    private const double MinVolumeRatio = 2.0;   // объём 3 последних свечей к среднему
    private const double MinChange = 2.0;        // минимум +2% за 24ч
    private const double OverheatChange = 40.0;  // выше +40% за сутки — вероятен памп на излёте

    public StrategySignal? Evaluate(MarketSnapshot s)
    {
        if (s.Candles.Count < 30) return null;
        if (s.Ticker.Change < MinChange || s.Ticker.Change > OverheatChange) return null;

        var c = s.Candles;

        // Всплеск объёма: среднее последних 3 свечей против среднего за 48ч до них
        double recentVol = (c[^1].Volume + c[^2].Volume + c[^3].Volume) / 3.0;
        double baseVol = Indicators.AverageVolume(c, 48, skipLast: 3);
        if (baseVol <= 0) return null;
        double volRatio = recentVol / baseVol;
        if (volRatio < MinVolumeRatio) return null;

        // Пробой: закрытие выше максимума предыдущих 24 свечей
        double prevHigh = Indicators.HighestHigh(c, 24, skipLast: 1);
        bool breakout = prevHigh > 0 && c[^1].Close > prevHigh;
        if (!breakout) return null;

        // Скоринг: база 55 + сила объёма (до +20) + рост (до +15) + аномалия суточного оборота (до +10)
        int score = 55
            + (int)Math.Min((volRatio - MinVolumeRatio) / 3.0 * 20, 20)   // x2 -> 0, x5 -> 20
            + (int)Math.Min(s.Ticker.Change / 15.0 * 15, 15)
            + (s.VolumeAnomaly >= 2.0 ? 10 : s.VolumeAnomaly >= 1.5 ? 5 : 0);

        return new StrategySignal
        {
            Strategy = Name,
            Score = Math.Clamp(score, 0, 100),
            Note = $"Импульс: объём x{volRatio:0.0} к среднему, пробой максимума 24ч, рост {s.Ticker.Change:+0.0}% за сутки"
        };
    }
}
