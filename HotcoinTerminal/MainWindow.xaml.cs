using Microsoft.UI.Xaml;
using HotcoinTerminal.Views;

namespace HotcoinTerminal;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Контент заходит в область титлбара, DragRegion отвечает за перетаскивание окна
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        RootFrame.Navigate(typeof(AnalyticsPage));
    }

    private void OnAnalyticsTabClick(object sender, RoutedEventArgs e)
    {
        AnalyticsTab.IsChecked = true;
        TradingTab.IsChecked = false;
        if (RootFrame.CurrentSourcePageType != typeof(AnalyticsPage))
            RootFrame.Navigate(typeof(AnalyticsPage));
    }

    private void OnTradingTabClick(object sender, RoutedEventArgs e)
    {
        TradingTab.IsChecked = true;
        AnalyticsTab.IsChecked = false;
        if (RootFrame.CurrentSourcePageType != typeof(TradingPage))
            RootFrame.Navigate(typeof(TradingPage));
    }
}
