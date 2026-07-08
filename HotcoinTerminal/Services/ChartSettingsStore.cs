using System.Text.Json;

namespace HotcoinTerminal.Services;

/// <summary>
/// Хранилище раскладок графика по символам: какие индикаторы включены
/// и какие уровни нарисованы для каждой пары (BTC/USDT — своё, ETH/USDT — своё).
///
/// Файл: %LocalAppData%\HotcoinTerminal\chart-layouts.json
/// Формат: { "BTC/USDT": "<layout-json>", ... } — сам layout-json генерирует
/// и понимает JS-сторона (chartApi.getLayout / applyLayout), C# его не разбирает.
///
/// Это первое место в проекте, где что-то пишется на диск. Секретов здесь нет —
/// только настройки отображения.
/// </summary>
public sealed class ChartSettingsStore
{
    public static ChartSettingsStore Instance { get; } = new();

    /// <summary>Раскладка по умолчанию для пары без сохранённых настроек:
    /// MA(10) + RSI(14) — как было на старом Canvas-графике.</summary>
    public const string DefaultLayout =
        """{"indicators":[{"id":"ma10","type":"ma","params":{"period":10}},{"id":"rsi14","type":"rsi","params":{"period":14}}],"priceLines":[]}""";

    private readonly string _path;
    private readonly Dictionary<string, string> _layouts;
    private readonly object _lock = new();

    private ChartSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HotcoinTerminal");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "chart-layouts.json");
        _layouts = LoadFile();
    }

    public string? LoadLayout(string pairDisplay)
    {
        lock (_lock)
            return _layouts.TryGetValue(pairDisplay, out var l) ? l : null;
    }

    public void SaveLayout(string pairDisplay, string layoutJson)
    {
        if (pairDisplay.Length == 0 || layoutJson.Length == 0) return;
        lock (_lock)
        {
            _layouts[pairDisplay] = layoutJson;
            SaveFile();
        }
    }

    // ---------------- Файл ----------------

    private Dictionary<string, string> LoadFile()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_path)) ?? new();
        }
        catch { /* повреждённый файл -> начинаем с чистого */ }
        return new();
    }

    private void SaveFile()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_layouts,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* нет прав/диск занят — не критично для работы графика */ }
    }
}
