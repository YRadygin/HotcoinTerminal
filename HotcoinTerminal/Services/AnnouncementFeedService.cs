using HotcoinTerminal.Models;
using HotcoinTerminal.Services.Api;

namespace HotcoinTerminal.Services;

/// <summary>
/// Фоновый опрос Announcement Center (раз в PollInterval) и превращение новых
/// анонсов в события ленты с тегом «События». Помнит уже показанные URL,
/// при первом опросе берёт только несколько самых свежих — для контекста, без потопа.
/// Источник неофициальный (парсинг сайта), поэтому любой сбой здесь — не ошибка
/// приложения, а просто «раздел временно без данных».
/// </summary>
public sealed class AnnouncementFeedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private const int FirstBatchLimit = 5; // сколько свежих анонсов показать при старте

    private readonly HotcoinAnnouncementsClient _client = new();
    private readonly HashSet<string> _seenUrls = new();
    private readonly object _lock = new();
    private Task? _loop;
    private bool _first = true;

    /// <summary>Новые события анонсов (готовые EventItem, новые первыми).</summary>
    public event Action<IReadOnlyList<EventItem>>? AnnouncementsArrived;

    public void Start()
    {
        _loop ??= Task.Run(RunLoopAsync);
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            try
            {
                var items = await _client.GetLatestAsync();
                var fresh = FilterNew(items);
                if (fresh.Count > 0)
                    AnnouncementsArrived?.Invoke(fresh);
            }
            catch
            {
                // Молча ждём следующего цикла: лента анонсов — некритичный источник
            }
            await Task.Delay(PollInterval);
        }
    }

    private List<EventItem> FilterNew(List<Announcement> items)
    {
        var fresh = new List<EventItem>();

        lock (_lock)
        {
            // Свежие сверху: сортируем по дате по убыванию (пустые даты — в конец)
            var ordered = items
                .OrderByDescending(a => a.Date)
                .ToList();

            // Первый опрос: показываем только верхушку, остальное просто помечаем виденным
            int budget = _first ? FirstBatchLimit : int.MaxValue;

            foreach (var a in ordered)
            {
                if (!_seenUrls.Add(a.Url)) continue;
                if (fresh.Count >= budget) continue; // виденным пометили, в ленту не льём

                fresh.Add(ToEvent(a));
            }
            _first = false;
        }
        return fresh;
    }

    private static EventItem ToEvent(Announcement a)
    {
        string prefix = a.Kind switch
        {
            AnnouncementKind.Listing => "Листинг",
            AnnouncementKind.Delisting => "Делистинг",
            _ => "Приостановка"
        };

        string tickers = a.Tickers.Count > 0 ? $" [{string.Join(", ", a.Tickers)}]" : "";
        string date = a.Date.Length > 0 ? a.Date[5..] : ""; // MM-dd — компактно для ленты

        return new EventItem
        {
            Time = date,
            Message = $"{prefix}{tickers}: {a.Title}",
            Tag = "События"
        };
    }
}
