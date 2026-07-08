using HotcoinTerminal.Models;

namespace HotcoinTerminal.Services;

/// <summary>
/// Живая лента событий скринера. Сравнивает выдачу движка между циклами
/// и превращает разницу в человеческие сообщения: новый сигнал, рост скора,
/// смена стратегии по паре. Хранит скользящий буфер последних событий.
/// Внешних запросов не делает — работает только на данных скринера.
/// </summary>
public sealed class EventFeedService
{
    private const int MaxEvents = 60;        // размер ленты
    private const int MinScoreForNew = 60;   // «новый сигнал» — только с приличным скором
    private const int MinScoreJump = 10;     // рост скора, достойный упоминания

    private Dictionary<string, SignalRow> _previous = new(); // Pair -> строка прошлого цикла
    private readonly LinkedList<EventItem> _feed = new();    // новые в начале
    private readonly object _lock = new();
    private bool _firstCycle = true;

    /// <summary>
    /// Принимает свежие строки скринера, возвращает актуальную ленту (новые сверху).
    /// Первый цикл после старта только запоминается — иначе лента взорвётся
    /// «новыми сигналами» по всем парам сразу.
    /// </summary>
    public IReadOnlyList<EventItem> Process(IReadOnlyList<SignalRow> rows, DateTime at)
    {
        var current = rows.ToDictionary(r => r.Pair);
        string time = at.ToString("HH:mm");

        lock (_lock)
        {
            if (_firstCycle)
            {
                _firstCycle = false;
                _previous = current;
                return _feed.ToList();
            }

            var fresh = new List<EventItem>();

            foreach (var row in rows)
            {
                if (!_previous.TryGetValue(row.Pair, out var old))
                {
                    // Пары не было в прошлой выдаче — новый сигнал
                    if (row.Score >= MinScoreForNew)
                        fresh.Add(Make(time, $"Новый сигнал: {row.Pair}, скор {row.Score}", row.Strategy));
                }
                else if (old.Strategy != row.Strategy)
                {
                    fresh.Add(Make(time, $"{row.Pair}: смена сетапа {old.Strategy} → {row.Strategy}, скор {row.Score}", row.Strategy));
                }
                else if (row.Score - old.Score >= MinScoreJump)
                {
                    fresh.Add(Make(time, $"{row.Pair}: скор вырос {old.Score} → {row.Score}", row.Strategy));
                }
            }

            // Сигнал погас: пара с высоким скором пропала из выдачи
            foreach (var old in _previous.Values)
            {
                if (old.Score >= 75 && !current.ContainsKey(old.Pair))
                    fresh.Add(Make(time, $"Сигнал погас: {old.Pair} ({old.Strategy})", old.Strategy));
            }

            // Новые события — в начало ленты (порядок внутри цикла: по скору)
            foreach (var e in fresh.OrderBy(f => f.Message))
                _feed.AddFirst(e);

            while (_feed.Count > MaxEvents)
                _feed.RemoveLast();

            _previous = current;
            return _feed.ToList();
        }
    }

    private static EventItem Make(string time, string message, string tag) =>
        new() { Time = time, Message = message, Tag = tag };
}
