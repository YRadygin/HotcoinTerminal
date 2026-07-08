using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using HotcoinTerminal.Models;

namespace HotcoinTerminal.Services.Api;

/// <summary>
/// Клиент публичного Market API Hotcoin (api.hotcoinfin.com).
/// Все методы этого клиента НЕ требуют ключей и подписи.
/// Подписанные запросы (баланс, ордера) появятся отдельным клиентом на этапе торговой вкладки.
/// </summary>
public sealed class HotcoinPublicClient
{
    private const string BaseUrl = "https://api.hotcoinfin.com";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    // ---------------- Публичные методы ----------------

    /// <summary>GET /v1/common/symbols — справочник торговых пар. Лимит: 10 зап/с.</summary>
    public async Task<List<SymbolInfo>> GetSymbolsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{BaseUrl}/v1/common/symbols", ct);
        var result = new List<SymbolInfo>();

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var el in data.EnumerateArray())
        {
            result.Add(new SymbolInfo
            {
                Symbol = Str(el, "symbol"),
                BaseCurrency = Str(el, "baseCurrency"),
                QuoteCurrency = Str(el, "quoteCurrency"),
                State = Str(el, "state")
            });
        }
        return result;
    }

    /// <summary>
    /// Тикеры по ВСЕМ парам одним запросом — основа цикла скринера.
    /// Пробует /v1/market/ticker, при неудаче — /v1/ticker (массив "ticker").
    /// </summary>
    public async Task<List<TickerInfo>> GetAllTickersAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await ParseTickersAsync($"{BaseUrl}/v1/market/ticker", ct);
            if (list.Count > 0) return list;
        }
        catch (HttpRequestException) { /* пробуем запасной путь */ }

        return await ParseTickersAsync($"{BaseUrl}/v1/ticker", ct);
    }

    /// <summary>
    /// GET /v1/ticker?symbol=..&step=.. — свечи. Формат элемента: [время, O, H, L, C, объём].
    /// step в секундах: 60, 300, 900, 1800, 3600, 86400. Лимит: 20 зап/с.
    /// </summary>
    public async Task<List<Candle>> GetKlinesAsync(string symbol, int stepSeconds, int maxCount = 1000,
        CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            $"{BaseUrl}/v1/ticker?symbol={Uri.EscapeDataString(symbol)}&step={stepSeconds}", ct);

        var arr = FindKlineArray(doc.RootElement);
        var candles = new List<Candle>();
        if (arr is null) return candles;

        foreach (var row in arr.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;

            long time = ParseKlineTime(Num(row[0]));
            double o = Num(row[1]), h = Num(row[2]), l = Num(row[3]), c = Num(row[4]), v = Num(row[5]);

            // Нормализация на случай иного порядка O/H/L/C в ответе:
            // high всегда максимум, low всегда минимум из четырёх значений.
            double high = Math.Max(Math.Max(o, c), Math.Max(h, l));
            double low = Math.Min(Math.Min(o, c), Math.Min(h, l));

            candles.Add(new Candle { TimeSec = time, Open = o, High = high, Low = low, Close = c, Volume = v });
        }

        // Графику нужны строго возрастающие времена без дублей
        candles = candles
            .Where(x => x.TimeSec > 0)
            .GroupBy(x => x.TimeSec)
            .Select(g => g.Last())
            .OrderBy(x => x.TimeSec)
            .ToList();

        // Хвост последних maxCount свечей
        if (candles.Count > maxCount)
            candles = candles.GetRange(candles.Count - maxCount, maxCount);

        return candles;
    }

    /// <summary>
    /// Время свечи из API -> unix-секунды. Биржи присылают время в разных видах,
    /// определяем по порядку величины:
    ///   ~1.7e9  — unix-секунды (как есть),
    ///   ~1.7e12 — unix-миллисекунды (/1000),
    ///   ~2.0e11 — форматированное yyyyMMddHHmm,
    ///   ~2.0e13 — форматированное yyyyMMddHHmmss.
    /// </summary>
    private static long ParseKlineTime(double raw)
    {
        if (raw <= 0) return 0;

        if (raw < 1e10) return (long)raw;                    // unix-секунды
        if (raw is >= 1e12 and < 1e13) return (long)(raw / 1000); // unix-мс

        // Форматированная дата числом
        var s = ((long)raw).ToString(CultureInfo.InvariantCulture);
        string format = s.Length switch
        {
            12 => "yyyyMMddHHmm",
            14 => "yyyyMMddHHmmss",
            8 => "yyyyMMdd",
            _ => ""
        };
        if (format.Length > 0 &&
            DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        }
        return 0;
    }

    // ---------------- Внутреннее ----------------

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<List<TickerInfo>> ParseTickersAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        var root = doc.RootElement;
        var result = new List<TickerInfo>();

        JsonElement? arr = null;
        if (root.TryGetProperty("ticker", out var t) && t.ValueKind == JsonValueKind.Array) arr = t;
        else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) arr = d;
        if (arr is null) return result;

        foreach (var el in arr.Value.EnumerateArray())
        {
            var symbol = Str(el, "symbol");
            if (symbol.Length == 0) continue;

            result.Add(new TickerInfo
            {
                Symbol = symbol,
                Last = Num(el, "last"),
                Buy = Num(el, "buy"),
                Sell = Num(el, "sell"),
                High = Num(el, "high"),
                Low = Num(el, "low"),
                Vol = Num(el, "vol"),
                Change = Num(el, "change")
            });
        }
        return result;
    }

    /// <summary>Ищет массив свечей: data | data.period.data | ticker.</summary>
    private static JsonElement? FindKlineArray(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) return data;
            if (data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("period", out var period) &&
                period.TryGetProperty("data", out var pd) &&
                pd.ValueKind == JsonValueKind.Array) return pd;
        }
        if (root.TryGetProperty("ticker", out var t) && t.ValueKind == JsonValueKind.Array) return t;
        return null;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";

    /// <summary>Число, которое может прийти строкой ("10000.5") или числом. Всегда инвариантная культура.</summary>
    private static double Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? Num(p) : 0;

    private static double Num(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0
    };
}
