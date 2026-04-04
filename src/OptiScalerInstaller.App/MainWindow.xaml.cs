using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using OptiScalerInstaller.App.ViewModels;

namespace OptiScalerInstaller.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool pendingLogScroll;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
        viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset) ||
            LogList.Items.Count == 0 ||
            pendingLogScroll)
        {
            return;
        }

        pendingLogScroll = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            pendingLogScroll = false;
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        }));
    }
}
