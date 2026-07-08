using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using HotcoinTerminal.Helpers;
using HotcoinTerminal.Models;
using HotcoinTerminal.Services;

namespace HotcoinTerminal.Controls;

/// <summary>
/// Лёгкий свечной график на Canvas: свечи + MA(10) + объём + RSI(14).
/// Заглушка на случайных данных; позже сюда будут приходить свечи из API.
/// При необходимости легко заменяется на ScottPlot или TradingView (WebView2).
/// </summary>
public sealed partial class CandleChartControl : UserControl
{
    private const double RightAxisWidth = 64;
    private const double Pad = 8;

    private List<Candle> _candles = new();

    public CandleChartControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Regenerate(11);
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Реальные свечи из API.</summary>
    public void SetCandles(List<Candle> candles)
    {
        if (candles.Count > 0) _candles = candles;
        Redraw();
    }

    /// <summary>Перегенерировать стаб-данные (используется при смене таймфрейма).</summary>
    public void Regenerate(int seed)
    {
        _candles = StubDataService.GenerateCandles(88, seed);
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        ChartCanvas.Children.Clear();
        if (w < 80 || h < 120 || _candles.Count == 0) return;

        double left = Pad, right = w - RightAxisWidth;

        // Вертикальная раскладка: цена 58% / объём 12% / RSI 22%
        double priceTop = Pad, priceBottom = h * 0.58;
        double volTop = h * 0.61, volBottom = h * 0.72;
        double rsiTop = h * 0.78, rsiBottom = h - Pad;

        DrawPriceArea(left, right, priceTop, priceBottom);
        DrawVolumeArea(left, right, volTop, volBottom);
        DrawRsiArea(left, right, w, rsiTop, rsiBottom);
    }

    // ---------- Цена ----------
    private void DrawPriceArea(double left, double right, double top, double bottom)
    {
        double min = _candles.Min(c => c.Low);
        double max = _candles.Max(c => c.High);
        double range = Math.Max(1e-9, max - min);
        double Y(double price) => bottom - (price - min) / range * (bottom - top);

        // Горизонтальная сетка + подписи цены
        for (int i = 0; i < 4; i++)
        {
            double gy = top + i * (bottom - top) / 3;
            double priceAtLine = max - i * range / 3;
            AddLine(left, gy, right, gy, Palette.GridLineBrush, 1);
            AddText(right + 8, gy - 8, FormatPrice(priceAtLine), 11, Palette.TextTertiaryBrush);
        }

        double step = (right - left) / _candles.Count;
        double bodyW = Math.Max(2, step * 0.62);

        var maPoints = new PointCollection();

        for (int i = 0; i < _candles.Count; i++)
        {
            var c = _candles[i];
            double x = left + i * step + step / 2;
            var brush = c.IsUp ? Palette.UpBrush : Palette.DownBrush;

            // Тень
            AddLine(x, Y(c.High), x, Y(c.Low), brush, 1.1);

            // Тело
            double yTop = Y(Math.Max(c.Open, c.Close));
            double yBot = Y(Math.Min(c.Open, c.Close));
            var body = new Rectangle
            {
                Width = bodyW,
                Height = Math.Max(1.5, yBot - yTop),
                Fill = brush,
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetLeft(body, x - bodyW / 2);
            Canvas.SetTop(body, yTop);
            ChartCanvas.Children.Add(body);

            // MA(10)
            if (i >= 9)
            {
                double ma = 0;
                for (int k = i - 9; k <= i; k++) ma += _candles[k].Close;
                ma /= 10;
                maPoints.Add(new Windows.Foundation.Point(x, Y(ma)));
            }
        }

        ChartCanvas.Children.Add(new Polyline
        {
            Points = maPoints,
            Stroke = Palette.AccentBrush,
            StrokeThickness = 2,
            Opacity = 0.9
        });

        // Плашка последней цены
        double lastClose = _candles[^1].Close;
        double tagY = Y(lastClose);
        var lastBrush = _candles[^1].IsUp ? Palette.UpBrush : Palette.DownBrush;
        var tag = new Border
        {
            Background = lastBrush,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = FormatPrice(lastClose),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0D, 0x10, 0x17))
            }
        };
        Canvas.SetLeft(tag, right + 4);
        Canvas.SetTop(tag, tagY - 11);
        ChartCanvas.Children.Add(tag);
    }

    // ---------- Объём ----------
    private void DrawVolumeArea(double left, double right, double top, double bottom)
    {
        AddText(left, top - 16, "Объём", 10.5, Palette.TextTertiaryBrush);

        double maxVol = Math.Max(1e-9, _candles.Max(c => c.Volume));
        double step = (right - left) / _candles.Count;
        double bodyW = Math.Max(2, step * 0.62);

        for (int i = 0; i < _candles.Count; i++)
        {
            var c = _candles[i];
            double x = left + i * step + step / 2;
            double vh = c.Volume / maxVol * (bottom - top);
            var bar = new Rectangle
            {
                Width = bodyW,
                Height = Math.Max(1, vh),
                Fill = c.IsUp ? Palette.UpBrush : Palette.DownBrush,
                Opacity = 0.4,
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetLeft(bar, x - bodyW / 2);
            Canvas.SetTop(bar, bottom - vh);
            ChartCanvas.Children.Add(bar);
        }
    }

    // ---------- RSI ----------
    private void DrawRsiArea(double left, double right, double fullWidth, double top, double bottom)
    {
        // Подложка секции
        var bg = new Rectangle
        {
            Width = fullWidth - 2 * Pad,
            Height = bottom - top + 24,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(8, 0xFF, 0xFF, 0xFF)),
            RadiusX = 10,
            RadiusY = 10
        };
        Canvas.SetLeft(bg, Pad);
        Canvas.SetTop(bg, top - 18);
        ChartCanvas.Children.Add(bg);

        AddText(left + 8, top - 14, "RSI (14)", 10.5, Palette.TextTertiaryBrush);

        double Y(double rsi) => bottom - rsi / 100.0 * (bottom - top);

        // Уровни 30/70
        foreach (var (level, label) in new[] { (70.0, "70"), (30.0, "30") })
        {
            var dash = new Line
            {
                X1 = left, Y1 = Y(level), X2 = right, Y2 = Y(level),
                Stroke = Palette.TextTertiaryBrush,
                StrokeThickness = 1,
                Opacity = 0.4,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            ChartCanvas.Children.Add(dash);
            AddText(right + 8, Y(level) - 8, label, 10.5, Palette.TextTertiaryBrush);
        }

        // RSI(14) по ценам закрытия
        var rsi = ComputeRsi(_candles.Select(c => c.Close).ToArray(), 14);
        double step = (right - left) / _candles.Count;
        var pts = new PointCollection();
        for (int i = 0; i < rsi.Length; i++)
        {
            if (double.IsNaN(rsi[i])) continue;
            pts.Add(new Windows.Foundation.Point(left + i * step + step / 2, Y(rsi[i])));
        }
        ChartCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = Palette.UpBrush,
            StrokeThickness = 2
        });
    }

    private static double[] ComputeRsi(double[] closes, int period)
    {
        var rsi = new double[closes.Length];
        Array.Fill(rsi, double.NaN);
        if (closes.Length <= period) return rsi;

        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = closes[i] - closes[i - 1];
            if (d >= 0) gain += d; else loss -= d;
        }
        gain /= period; loss /= period;
        rsi[period] = loss < 1e-12 ? 100 : 100 - 100 / (1 + gain / loss);

        for (int i = period + 1; i < closes.Length; i++)
        {
            double d = closes[i] - closes[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
            rsi[i] = loss < 1e-12 ? 100 : 100 - 100 / (1 + gain / loss);
        }
        return rsi;
    }

    // ---------- Хелперы ----------
    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness)
    {
        ChartCanvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = stroke,
            StrokeThickness = thickness
        });
    }

    private void AddText(double x, double y, string text, double size, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        ChartCanvas.Children.Add(tb);
    }

    private static string FormatPrice(double p) =>
        p.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
}
