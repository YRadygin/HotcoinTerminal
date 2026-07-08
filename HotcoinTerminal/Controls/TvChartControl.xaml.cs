using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HotcoinTerminal.Models;
using HotcoinTerminal.Services;

namespace HotcoinTerminal.Controls;

/// <summary>
/// График на TradingView Lightweight Charts внутри WebView2.
/// C# отдаёт свечи и команды в JS (Assets/Chart/chart.html), JS присылает
/// события (готовность, изменение разметки, запрос истории) через postMessage.
///
/// Публичный API повторяет старый CandleChartControl (SetCandles) и расширяет его:
/// индикаторы, уровни, автосохранение раскладки по символу (ChartSettingsStore).
/// </summary>
public sealed partial class TvChartControl : UserControl
{
    // Реестр живых экземпляров: WebView2 обязан быть закрыт явно (Close()),
    // иначе процессы msedgewebview2.exe переживают окно. CloseAll() зовётся
    // из MainWindow.Closed.
    private static readonly HashSet<TvChartControl> Live = new();
    private static readonly object LiveLock = new();

    private const int MaxPendingScripts = 64;

    private bool _pageReady;
    private bool _closed;
    private readonly Queue<string> _pendingScripts = new();
    private string _currentSymbol = "";

    /// <summary>JS попросил более старую историю (скролл влево к краю данных).
    /// Аргументы: символ ("BTC/USDT") и unix-время самой старой свечи на графике.</summary>
    public event Action<string, long>? HistoryRequested;

    public TvChartControl()
    {
        InitializeComponent();
        lock (LiveLock) Live.Add(this);
        Loaded += async (_, _) => await InitAsync();
    }

    /// <summary>Явное закрытие всех WebView2. Вызывать при закрытии главного окна —
    /// это единственный надёжный способ прибить браузерные процессы сразу.</summary>
    public static void CloseAll()
    {
        List<TvChartControl> snapshot;
        lock (LiveLock) { snapshot = Live.ToList(); Live.Clear(); }
        foreach (var c in snapshot) c.CloseWebView();
    }

    /// <summary>Закрыть ядро WebView2 этого контрола. После закрытия контрол мёртв
    /// (повторная инициализация не поддерживается WebView2).</summary>
    public void CloseWebView()
    {
        if (_closed) return;
        _closed = true;
        _pageReady = false;
        _pendingScripts.Clear();

        try
        {
            if (Web.CoreWebView2 is not null)
                Web.CoreWebView2.WebMessageReceived -= OnWebMessage;
            Web.Close(); // освобождает ядро и завершает процессы WebView2
        }
        catch { /* закрытие при выходе не должно ронять приложение */ }

        lock (LiveLock) Live.Remove(this);
    }

    // ---------------- Инициализация WebView2 ----------------

    private async Task InitAsync()
    {
        if (_closed) return;               // ядро уже закрыто, реанимация невозможна
        if (Web.CoreWebView2 is not null) return; // повторный Loaded (возврат на кэшированную страницу)

        try
        {
            await Web.EnsureCoreWebView2Async();

            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;      // зумом управляет сам график
            core.Settings.AreDevToolsEnabled = true;         // F12 при отладке; в релизе можно выключить

            // Папка Assets/Chart из выходного каталога видна как https://chart.local/
            core.SetVirtualHostNameToFolderMapping(
                "chart.local",
                Path.Combine(AppContext.BaseDirectory, "Assets", "Chart"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            core.Navigate("https://chart.local/chart.html");
        }
        catch (Exception ex)
        {
            // Типовая причина — не установлен WebView2 Runtime (на Win11 есть всегда)
            LoadingText.Text = $"WebView2 недоступен: {ex.Message}";
        }
    }

    private void OnWebMessage(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.TryGetWebMessageAsString()); }
        catch { return; }

        using (doc)
        {
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    LoadingText.Visibility = Visibility.Collapsed;
                    while (_pendingScripts.Count > 0)
                        _ = Web.CoreWebView2.ExecuteScriptAsync(_pendingScripts.Dequeue());
                    break;

                case "layoutChanged":
                    // Пользователь изменил уровни/индикаторы -> сохраняем для этого символа
                    if (doc.RootElement.TryGetProperty("symbol", out var sym) &&
                        doc.RootElement.TryGetProperty("layout", out var layout))
                    {
                        ChartSettingsStore.Instance.SaveLayout(
                            sym.GetString() ?? "", layout.GetString() ?? "");
                    }
                    break;

                case "loadMoreHistory":
                    if (doc.RootElement.TryGetProperty("symbol", out var s2) &&
                        doc.RootElement.TryGetProperty("oldestTime", out var ot) &&
                        ot.TryGetInt64(out var oldest))
                    {
                        HistoryRequested?.Invoke(s2.GetString() ?? "", oldest);
                    }
                    break;
            }
        }
    }

    // ---------------- Публичный API ----------------

    /// <summary>Полная загрузка свечей для пары. При смене символа
    /// автоматически восстанавливает сохранённую раскладку (индикаторы, уровни).</summary>
    public void SetCandles(string pairDisplay, IReadOnlyList<Candle> candles)
    {
        bool symbolChanged = _currentSymbol != pairDisplay;
        _currentSymbol = pairDisplay;

        Run($"window.chartApi.setCandles({JsonSerializer.Serialize(pairDisplay)}, {ToJson(candles)});");

        if (symbolChanged)
        {
            var layout = ChartSettingsStore.Instance.LoadLayout(pairDisplay)
                         ?? ChartSettingsStore.DefaultLayout;
            Run($"window.chartApi.applyLayout({JsonSerializer.Serialize(layout)});");
        }
    }

    /// <summary>Обновить последнюю свечу (живой тик без полной перезагрузки).</summary>
    public void UpdateLast(Candle c) => Run($"window.chartApi.updateLast({ToJson(c)});");

    /// <summary>Добавить индикатор. type: ma | ema | bollinger | rsi.
    /// paramsJson, например: {"period":10} или {"period":20,"mult":2}.</summary>
    public void AddIndicator(string id, string type, string paramsJson)
    {
        Run($"window.chartApi.addIndicator({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(type)}, {paramsJson});");
        SaveLayoutSoon();
    }

    public void RemoveIndicator(string id)
    {
        Run($"window.chartApi.removeIndicator({JsonSerializer.Serialize(id)});");
        SaveLayoutSoon();
    }

    /// <summary>Горизонтальный уровень по цене (также ставится с графика: Ctrl+клик).</summary>
    public void AddPriceLine(string id, double price, string? colorHex = null)
    {
        var color = colorHex is null ? "null" : JsonSerializer.Serialize(colorHex);
        Run($"window.chartApi.addPriceLine({JsonSerializer.Serialize(id)}, {price.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {color});");
        SaveLayoutSoon();
    }

    // ---------------- Внутреннее ----------------

    /// <summary>После программного изменения раскладки — забрать её из JS и сохранить.</summary>
    private void SaveLayoutSoon()
    {
        if (!_pageReady || _currentSymbol.Length == 0) return;
        _ = SaveLayoutAsync(_currentSymbol);
    }

    private async Task SaveLayoutAsync(string symbol)
    {
        try
        {
            if (_closed || Web.CoreWebView2 is null) return;
            // ExecuteScriptAsync возвращает JSON-строку результата (строка в строке)
            var raw = await Web.CoreWebView2.ExecuteScriptAsync("window.chartApi.getLayout();");
            var layout = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrEmpty(layout))
                ChartSettingsStore.Instance.SaveLayout(symbol, layout);
        }
        catch { /* сохранение раскладки не должно ронять UI */ }
    }

    private void Run(string script)
    {
        if (_closed) return;

        if (_pageReady && Web.CoreWebView2 is not null)
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        else
        {
            // Страховка от бесконечного роста, если страница так и не прогрузилась
            if (_pendingScripts.Count >= MaxPendingScripts) _pendingScripts.Dequeue();
            _pendingScripts.Enqueue(script);
        }
    }

    /// <summary>Свечи -> компактный JSON [{t,o,h,l,c,v}] c инвариантной культурой.</summary>
    private static string ToJson(IReadOnlyList<Candle> candles)
    {
        var sb = new StringBuilder(candles.Count * 48).Append('[');
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":").Append(c.TimeSec)
              .Append(",\"o\":").Append(Inv(c.Open))
              .Append(",\"h\":").Append(Inv(c.High))
              .Append(",\"l\":").Append(Inv(c.Low))
              .Append(",\"c\":").Append(Inv(c.Close))
              .Append(",\"v\":").Append(Inv(c.Volume))
              .Append('}');
        }
        return sb.Append(']').ToString();
    }

    private static string ToJson(Candle c) => ToJson(new[] { c });

    private static string Inv(double d) =>
        d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
