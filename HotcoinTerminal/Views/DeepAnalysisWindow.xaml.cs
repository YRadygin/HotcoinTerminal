using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HotcoinTerminal.Views;

/// <summary>
/// Окно углублённого AI-анализа по паре. Пока заглушка:
/// здесь будут подробные рекомендации, уровни и тайминги удержания позиции.
/// </summary>
public sealed partial class DeepAnalysisWindow : Window
{
    public DeepAnalysisWindow(string pair)
    {
        InitializeComponent();
        Title = $"Углублённый AI-анализ — {pair}";
        HeaderText.Text = Title;
        AppWindow.Resize(new SizeInt32(680, 480));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
