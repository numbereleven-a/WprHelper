using System.ComponentModel;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using WprHelper.Contracts;
using Forms = System.Windows.Forms;

namespace WprHelper.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF owns the window lifetime; OnClosing disposes the tray icon.")]
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon? _trayIcon;
    private bool _closingAfterCapture;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        ApplyElementLanguage();
        _viewModel = viewModel;
        DataContext = viewModel;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("WPR Helper", null, (_, _) => Restore());
        menu.Items.Add("Stop", null, (_, _) => { if (_viewModel.StopCommand.CanExecute(null)) _viewModel.StopCommand.Execute(null); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "WPR Helper",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Restore();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsRunning && !_closingAfterCapture)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(5000, LocalizationService.Get("FinalizingTitle"),
                LocalizationService.Get("FinalizingMessage"), Forms.ToolTipIcon.Info);
            var finished = await _viewModel.StopAndWaitForShutdownAsync(TimeSpan.FromSeconds(15));
            if (!finished)
            {
                Restore();
                var closeAnyway = System.Windows.MessageBox.Show(LocalizationService.Get("FinalizationStillRunning"),
                    LocalizationService.Get("FinalizingTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (closeAnyway != MessageBoxResult.Yes) return;
            }
            _closingAfterCapture = true;
            Close();
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        base.OnClosing(e);
    }

    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        LocalizationService.Apply(_viewModel.Language);
        ApplyElementLanguage();
    }

    private void ApplyElementLanguage() =>
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
}
