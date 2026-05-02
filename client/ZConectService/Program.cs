using ZConectService;

// ── CLI argument handling ──────────────────────────────────────────
var cliArgs = Environment.GetCommandLineArgs();
if (cliArgs.Length > 1)
{
    var cmd = cliArgs[1].ToLowerInvariant().TrimStart('-', '/');
    var exitCode = cmd switch
    {
        "install" => ServiceInstaller.Install(),
        "uninstall" => ServiceInstaller.Uninstall(),
        "start" => ServiceInstaller.Start(),
        "stop" => ServiceInstaller.Stop(),
        "help" or "?" => ShowHelp(),
        _ => ShowHelp()
    };
    Environment.Exit(exitCode);
    return;
}

// ── Run as Windows Service (no args = SCM invocation) ──────────────
var logger = new ServiceLogger();
var config = ServiceConfig.Load();

logger.Info("Service", "initializing");

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = ServiceInstaller.ServiceName;
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(logger);
            services.AddSingleton(config);
            services.AddHostedService<ServiceWorker>();
        });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.Error("Service", "host_fatal", ex.ToString());
    throw;
}
finally
{
    logger.Dispose();
}

static int ShowHelp()
{
    Console.WriteLine("""
    ZConect Service — manages remote desktop sessions

    Usage:
      ZConectService.exe                 Run as Windows Service (called by SCM)
      ZConectService.exe --install       Install as Windows Service (requires admin)
      ZConectService.exe --uninstall     Uninstall the Windows Service
      ZConectService.exe --start         Start the service
      ZConectService.exe --stop          Stop the service
      ZConectService.exe --help          Show this help

    Config: C:\ProgramData\ZConect\service-config.json
    Logs:   C:\ProgramData\ZConect\service.log
    """);
    return 0;
}
