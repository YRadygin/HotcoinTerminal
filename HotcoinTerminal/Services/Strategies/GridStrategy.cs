namespace HotcoinTerminal.Services.Strategies;

/// <summary>
/// Сетка: ищем устойчивый боковик с «живой» волатильностью внутри диапазона.
/// Идеал: цена часто пилит вокруг средней (много пересечений SMA), ATR достаточен,
/// чтобы шаг сетки перекрывал комиссии, а суточный тренд близок к нулю.
/// </summary>
public sealed class GridStrategy : IStrategy
{
    public string Name => "Grid";

    // Минимальный ATR% на часе: ниже — колебания не окупят комиссии (~0.2% круг + спред)
    private const double MinAtrPercent = 0.35;
    private const double MaxAtrPercent = 3.0;   // выше — это уже не боковик, а шторм
    private const double MaxAbsChange = 3.0;    // |изменение 24ч| больше — есть тренд, сетке опасно

    public StrategySignal? Evaluate(MarketSnapshot s)
    {
        if (s.Candles.Count < 48) return null;

        double atr = s.AtrPercent;
        double absChange = Math.Abs(s.Ticker.Change);

        if (atr < MinAtrPercent || atr > MaxAtrPercent) return null;
        if (absChange > MaxAbsChange) return null;

        // Пила: сколько раз за 48ч цена пересекла SMA(20). 6+ — отличный боковик.
        int crosses = Indicators.SmaCrossCount(s.Candles, 20, 48);
        if (crosses < 3) return null; // цена липнет к одной стороне — вероятен тренд

        // Скоринг: база 50 + пила (до +20) + волатильность (до +15) + отсутствие тренда (до +15)
        int score = 50
            + Math.Min(crosses * 3, 20)
            + (int)Math.Min((atr - MinAtrPercent) / (1.5 - MinAtrPercent) * 15, 15)
            + (int)((1.0 - Math.Min(absChange / MaxAbsChange, 1.0)) * 15);

        return new StrategySignal
        {
            Strategy = Name,
            Score = Math.Clamp(score, 0, 100),
            Note = $"Боковик: {crosses} пересечений SMA20 за 48ч, ATR {atr:0.00}%/ч, изменение 24ч {s.Ticker.Change:+0.0;-0.0}%"
        };
    }
}
