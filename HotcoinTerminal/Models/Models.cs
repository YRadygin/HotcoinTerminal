using Microsoft.UI.Xaml.Media;
using HotcoinTerminal.Helpers;

namespace HotcoinTerminal.Models;

/// <summary>Свеча OHLCV (заглушка, позже придёт из API биржи).</summary>
public sealed class Candle
{
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public double Volume { get; init; }
    public bool IsUp => Close >= Open;
}

/// <summary>Строка скринера: пара + стратегия + скоринг + изменение за 24ч.</summary>
public sealed class SignalRow
{
    public string Pair { get; init; } = "";
    public string Strategy { get; init; } = "";
    public int Score { get; init; }
    public double ChangePercent { get; init; }

    public string ChangeText => (ChangePercent >= 0 ? "+" : "") + ChangePercent.ToString("0.0") + "%";

    public SolidColorBrush ChangeBrush =>
        ChangePercent >= 0 ? Palette.UpBrush : Palette.DownBrush;

    public SolidColorBrush ScoreForeground =>
        Score >= 75 ? Palette.UpBrush : Score >= 60 ? Palette.AccentBrush : Palette.TextTertiaryBrush;

    public SolidColorBrush ScoreBackground =>
        Score >= 75 ? Palette.UpFaintBrush : Score >= 60 ? Palette.AccentFaintBrush : Palette.NeutralFaintBrush;
}

/// <summary>Событие в ленте (заглушка).</summary>
public sealed class EventItem
{
    public string Time { get; init; } = "";
    public string Message { get; init; } = "";
    public string Tag { get; init; } = "";

    public SolidColorBrush TagBrush => Tag switch
    {
        "Momentum" => Palette.UpBrush,
        "Mean Rev" => Palette.UpBrush,
        "События" => Palette.AccentBrush,
        _ => Palette.DownBrush
    };

    public SolidColorBrush TagFaintBrush => Tag switch
    {
        "Momentum" => Palette.UpFaintBrush,
        "Mean Rev" => Palette.UpFaintBrush,
        "События" => Palette.AccentFaintBrush,
        _ => Palette.DownFaintBrush
    };
}
