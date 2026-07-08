namespace HotcoinTerminal.Services.Api;

/// <summary>Торговая пара из GET /v1/common/symbols.</summary>
public sealed class SymbolInfo
{
    public string Symbol { get; init; } = "";        // btc_usdt
    public string BaseCurrency { get; init; } = "";  // btc
    public string QuoteCurrency { get; init; } = ""; // usdt
    public string State { get; init; } = "";         // online / enable / ...
}

/// <summary>Тикер пары (значения в API приходят строками).</summary>
public sealed class TickerInfo
{
    public string Symbol { get; init; } = "";
    public double Last { get; init; }
    public double Buy { get; init; }
    public double Sell { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Vol { get; init; }     // объём 24ч в базовой валюте
    public double Change { get; init; }  // изменение 24ч, %

    /// <summary>Приблизительный оборот в котируемой валюте — для фильтра ликвидности и сортировки.</summary>
    public double QuoteVolume => Vol * Last;

    /// <summary>Спред в процентах от цены (для фильтра качества пары).</summary>
    public double SpreadPercent => Last > 0 && Sell > 0 && Buy > 0 ? (Sell - Buy) / Last * 100.0 : 100.0;
}
