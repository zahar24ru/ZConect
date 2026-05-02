using System.Diagnostics;

namespace ZConectService;

/// <summary>
/// Installs/uninstalls ZConect as a Windows Service via sc.exe.
/// Idempotent: re-running --install updates existing service instead of failing.
/// Must be run from an elevated (admin) command prompt.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "ZConectService";
    public const string DisplayName = "ZConect Remote Desktop Service";
    public const string Description = "Manages ZConect remote desktop sessions, enables unattended access and UAC support.";

    public static int Install()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "ZConectService.exe");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"ERROR: {exePath} not found.");
            return 1;
        }

        // Create ProgramData directory.
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZConect");
        Directory.CreateDirectory(dataDir);

        Console.WriteLine($"Installing service: {ServiceName}");
        Console.WriteLine($"  Binary: {exePath}");
        Console.WriteLine($"  Data:   {dataDir}");

        if (ServiceExists())
        {
            Console.WriteLine("Service already exists — updating config to match current binary path.");
            // Stop if running so binPath change takes effect cleanly.
            StopAndWait(timeoutMs: 15000);
            // sc config: update binPath, startup type, account in place. Preserves ACL and SID.
            var cfgResult = RunSc($"config {ServiceName} binPath= \"\\\"{exePath}\\\"\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
            if (cfgResult != 0)
            {
                Console.Error.WriteLine("sc config failed.");
                return cfgResult;
            }
        }
        else
        {
            var createResult = RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\"\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
            if (createResult != 0)
            {
                Console.Error.WriteLine("sc create failed.");
                return createResult;
            }
        }

        // Always (re)apply description + failure actions (idempotent).
        RunSc($"description {ServiceName} \"{Description}\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        Console.WriteLine("Service installed successfully.");
        Console.WriteLine($"  Start:  sc start {ServiceName}");
        Console.WriteLine($"  Stop:   sc stop {ServiceName}");
        Console.WriteLine($"  Status: sc query {ServiceName}");
        return 0;
    }

    public static int Uninstall()
    {
        if (!ServiceExists())
        {
            Console.WriteLine("Service not installed — nothing to uninstall.");
            return 0;
        }

        Console.WriteLine($"Stopping service: {ServiceName}");
        StopAndWait(timeoutMs: 15000);

        Console.WriteLine($"Deleting service: {ServiceName}");
        var result = RunSc($"delete {ServiceName}");
        if (result != 0)
        {
            Console.Error.WriteLine("sc delete failed.");
            return result;
        }

        Console.WriteLine("Service uninstalled.");
        return 0;
    }

    public static int Start()
    {
        Console.WriteLine($"Starting service: {ServiceName}");
        return RunSc($"start {ServiceName}");
    }

    public static int Stop()
    {
        Console.WriteLine($"Stopping service: {ServiceName}");
        StopAndWait(timeoutMs: 15000);
        return 0;
    }

    /// <summary>Check if service is registered in SCM.</summary>
    public static bool ServiceExists()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {ServiceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            // 1060 = service does not exist
            return !output.Contains("1060") && proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Issue stop + poll until SERVICE_STOPPED or timeout. Safe to call if already stopped.</summary>
    private static void StopAndWait(int timeoutMs)
    {
        // Fire stop — may return immediately even if service is still shutting down.
        RunSc($"stop {ServiceName}");

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsStopped()) return;
            Thread.Sleep(500);
        }
        Console.Error.WriteLine($"WARN: service did not reach STOPPED state within {timeoutMs}ms.");
    }

    private static bool IsStopped()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {ServiceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            // Output contains "STATE              : 1  STOPPED" when stopped.
            return output.Contains("STOPPED");
        }
        catch { return true; } // treat errors as "stopped" to avoid infinite loop
    }

    private static int RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sc.exe error: {ex.Message}");
            return -1;
        }
    }
}
