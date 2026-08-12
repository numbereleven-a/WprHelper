using System.Windows;
using System.IO;
using WprHelper.Contracts;
using WprHelper.Core;
using WprHelper.Infrastructure;

namespace WprHelper.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnhandled(args.Exception);
            args.Handled = true;
            System.Windows.MessageBox.Show(args.Exception.Message, "WPR Helper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogUnhandled(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogUnhandled(args.Exception); args.SetObserved(); };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--elevated-worker", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var pipe = GetArg(e.Args, "--pipe");
                var session = Guid.Parse(GetArg(e.Args, "--session"));
                var exitCode = await ServiceRegistry.CreateWorkerHost().RunAsync(pipe, session, CancellationToken.None);
                Shutdown(exitCode);
            }
            catch (Exception ex)
            {
                try
                {
                    var logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WprHelper", "Logs");
                    Directory.CreateDirectory(logRoot);
                    var logPath = Path.Combine(logRoot, "worker-startup.log");
                    await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}");
                }
                catch { }
                Shutdown(1);
            }
            return;
        }

        try
        {
            var services = ServiceRegistry.Create();
            LocalizationService.Apply(LanguagePreference.Automatic);
            var viewModel = new MainWindowViewModel(services.SessionManager, services.ProfileRepository, services.Paths, services.CommandBuilder);
            await viewModel.InitializeAsync();
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogUnhandled(ex);
            System.Windows.MessageBox.Show(ex.Message, "WPR Helper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string GetArg(string[] args, string name)
    {
        var index = args.ToList().FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Missing argument {name}.");
        return args[index + 1];
    }

    private static void LogUnhandled(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WprHelper", "Logs");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "unhandled.log"), $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch { }
    }
}

internal sealed class ServiceRegistry
{
    public required IStoragePathResolver Paths { get; init; }
    public required IProfileRepository ProfileRepository { get; init; }
    public required ISessionManager SessionManager { get; init; }
    public required IWprCommandBuilder CommandBuilder { get; init; }

    public static ServiceRegistry Create()
    {
        IClock clock = new SystemClock();
        IStoragePathResolver paths = new StoragePathResolver();
        IDiskSpaceService disk = new DiskSpaceService();
        ITargetProcessLauncher target = new TargetProcessLauncher();
        var workerClient = new ElevatedWorkerClient(target);
        ISessionRepository sessionRepository = new JsonSessionRepository(paths, clock);
        IFileTransferService transfer = new FileTransferService(new HashService());
        var evaluator = new StopConditionEvaluator();
        var validator = new ProfileValidator();
        IWprCommandBuilder commands = new WprCommandBuilder();
        return new ServiceRegistry
        {
            Paths = paths,
            ProfileRepository = new JsonProfileRepository(paths),
            CommandBuilder = commands,
            SessionManager = new SessionManager(validator, paths, sessionRepository, workerClient, new WprCapabilityDetector(), transfer, disk, clock)
        };
    }

    public static ElevatedWorkerHost CreateWorkerHost()
    {
        IClock clock = new SystemClock();
        IWprCommandBuilder commands = new WprCommandBuilder();
        IWprController wpr = new WprController(commands);
        return new ElevatedWorkerHost(wpr, new DiskSpaceService(), new StopConditionEvaluator(), clock,
            new ProfileValidator());
    }
}
