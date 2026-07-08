using System.Net.Http;
using System.Text.RegularExpressions;

namespace HotcoinTerminal.Services.Api;

/// <summary>Тип анонса — определяет и категорию запроса, и подачу в ленте.</summary>
public enum AnnouncementKind
{
    Listing,     // New Spot Trading Pairs — новые листинги (обычно всплеск интереса)
    Delisting,   // Removal Notice — делистинг (давление вниз)
    Suspension   // Currency Suspension — приостановка депозитов/торгов
}

/// <summary>Один анонс из Announcement Center.</summary>
public sealed class Announcement
{
    public required AnnouncementKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Date { get; init; } = "";              // yyyy-MM-dd как на сайте
    public IReadOnlyList<string> Tickers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Клиент Announcement Center Hotcoin (www.hotcoin.com/en_US/support/nav/2/).
/// ВНИМАНИЕ: это парсинг HTML сайта, а НЕ официальный API. Биржа может поменять
/// вёрстку без предупреждения — поэтому клиент максимально терпим к формату,
/// а вызывающий код обязан переживать пустой результат и исключения.
/// </summary>
public sealed class HotcoinAnnouncementsClient
{
    private const string BaseUrl = "https://www.hotcoin.com/en_US/support/nav/2/";

    // Коды категорий из навигации Announcement Center (постоянные идентификаторы разделов)
    private static readonly (AnnouncementKind Kind, string Code, string Id)[] Categories =
    {
        (AnnouncementKind.Listing,    "19112597563772928", "19112597569540096"), // New Spot Trading Pairs
        (AnnouncementKind.Delisting,  "11675154426761217", "13192980155994112"), // Removal Notice
        (AnnouncementKind.Suspension, "11675154484957185", "13192837656612864")  // Currency Suspension
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Без браузерного User-Agent сайт может отдавать заглушку
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HotcoinTerminal/1.0");
        return http;
    }

    /// <summary>Ссылка на статью анонса: href="...(/support/notice/slug/)" + заголовок внутри тега.</summary>
    private static readonly Regex LinkRx = new(
        "<a[^>]+href=\"(?<url>[^\"]*?/support/notice/[^\"]+?)\"[^>]*>(?<title>[^<]{10,300})</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Дата yyyy-MM-dd, идущая в разметке после ссылки.</summary>
    private static readonly Regex DateRx = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    /// <summary>Тикеры в заголовке: «List GCOIN (Playnance)» -> GCOIN. 2–8 заглавных букв/цифр.</summary>
    private static readonly Regex TickerRx = new(@"\b[A-Z][A-Z0-9]{1,7}\b", RegexOptions.Compiled);

    /// <summary>Слова, похожие на тикер, но тикером не являющиеся.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "HOTCOIN", "USDT", "USDC", "THE", "AND", "FOR", "WILL", "LIST", "SPOT",
        "NEW", "TRADING", "PAIRS", "GLOBAL", "GLOBALLY", "LAUNCH", "WITH", "ZERO",
        "FEES", "TIME", "NOTICE", "REMOVAL", "TOKEN", "TOKENS", "API", "P2P", "VIP"
    };

    /// <summary>Свежие анонсы всех отслеживаемых категорий (первая страница каждой).</summary>
    public async Task<List<Announcement>> GetLatestAsync(CancellationToken ct = default)
    {
        var result = new List<Announcement>();

        foreach (var (kind, code, id) in Categories)
        {
            try
            {
                var page = await Http.GetStringAsync($"{BaseUrl}?code={code}&id={id}", ct);
                result.AddRange(ParsePage(page, kind));
            }
            catch
            {
                // Одна недоступная категория не должна ронять остальные
            }
        }
        return result;
    }

    private static List<Announcement> ParsePage(string html, AnnouncementKind kind)
    {
        var items = new List<Announcement>();

        foreach (Match m in LinkRx.Matches(html))
        {
            string title = System.Net.WebUtility.HtmlDecode(m.Groups["title"].Value.Trim());
            string url = m.Groups["url"].Value;
            if (title.Length == 0) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://www.hotcoin.com" + url;

            // Дата — первое совпадение yyyy-MM-dd в ближайших 200 символах после ссылки
            string date = "";
            int from = m.Index + m.Length;
            var dm = DateRx.Match(html, from, Math.Min(200, html.Length - from));
            if (dm.Success) date = dm.Value;

            items.Add(new Announcement
            {
                Kind = kind,
                Title = title,
                Url = url,
                Date = date,
                Tickers = ExtractTickers(title)
            });
        }
        return items;
    }

    private static List<string> ExtractTickers(string title)
    {
        var tickers = new List<string>();
        foreach (Match m in TickerRx.Matches(title))
        {
            var word = m.Value;
            if (StopWords.Contains(word)) continue;
            if (!tickers.Contains(word)) tickers.Add(word);
        }
        return tickers;
    }
}
