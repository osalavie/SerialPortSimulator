using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using SerialPortSimulator.Services;

namespace SerialPortSimulator.DeviceManager;

internal sealed class DeviceManagerService : IDisposable
{
    private const string HardwareId = @"ROOT\SerialPortSimulator";
    private const string ResourcePrefix = "SerialPortSimulator.DeviceManager.DriverResources.";
    private static readonly string[] PackageFiles =
    [
        "VirtualSerial2um.dll", "virtualserial2um.inf", "wudf.cat",
        "VirtualSerial2um.cer", "devcon.exe"
    ];
    private readonly string _appRoot;
    private readonly string _packageRoot;
    private readonly string _driverRoot;
    private readonly string _infPath;
    private readonly string _devconPath;
    private readonly string _certificatePath;
    private bool _removeManagerLogAfterDispose;

    public DeviceManagerService(string appRoot)
    {
        _appRoot = Path.GetFullPath(appRoot);
        _packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SerialPortSimulator",
            "DevicePackage");
        _driverRoot = Path.Combine(_packageRoot, Guid.NewGuid().ToString("N"));
        _infPath = Path.Combine(_driverRoot, "virtualserial2um.inf");
        _devconPath = Path.Combine(_driverRoot, "devcon.exe");
        _certificatePath = Path.Combine(_driverRoot, "VirtualSerial2um.cer");
    }

    public int CreateDevice()
    {
        ValidateDriverPackage();
        ImportTestCertificateIfPresent();

        var result = RunDevcon("install", _infPath, HardwareId);
        if (result.ExitCode != 0)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"devcon install failed with exit code {result.ExitCode}.");

        DeviceManagerLog.Write($"Created device for {HardwareId}.");
        return ExitCodes.Success;
    }

    public int UpdateDriver()
    {
        ValidateDriverPackage();
        ImportTestCertificateIfPresent();

        var result = RunDevcon("update", _infPath, HardwareId);
        if (result.ExitCode != 0)
            throw new DeviceManagerException(
                ExitCodes.DriverInstallFailed,
                $"devcon update failed with exit code {result.ExitCode}.");

        DeviceManagerLog.Write($"Updated devices for {HardwareId}.");
        return ExitCodes.Success;
    }

    public int RemoveDevice(string instanceId)
    {
        if (!instanceId.StartsWith(@"ROOT\PORTS\", StringComparison.OrdinalIgnoreCase))
            throw new DeviceManagerException(ExitCodes.InvalidArguments, "Invalid device instance ID.");

        ValidateHardwareId(instanceId);
        ValidateDriverPackage();
        var result = RunDevcon("remove", $"@{instanceId}");
        if (result.ExitCode != 0)
            throw new DeviceManagerException(
                ExitCodes.DeviceRemovalFailed,
                $"devcon remove failed with exit code {result.ExitCode}.");

        DeviceManagerLog.Write($"Removed device {instanceId}.");
        if (!HasInstalledSimulatorDevices())
        {
            try
            {
                RemoveDriverPackages();
                RemoveTestCertificateIfPresent();
                DeviceManagerLog.Write("Removed unused simulator driver packages and test certificate.");
                _removeManagerLogAfterDispose = true;
            }
            catch (DeviceManagerException) { throw; }
            catch (Exception ex)
            {
                throw new DeviceManagerException(ExitCodes.DriverCleanupFailed,
                    $"Driver cleanup failed after removing {instanceId}: {ex.Message}");
            }
        }
        return ExitCodes.Success;
    }

    private static bool HasInstalledSimulatorDevices()
    {
        using RegistryKey? ports = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\ROOT\PORTS");
        if (ports is null) return false;
        foreach (string name in ports.GetSubKeyNames())
        {
            using RegistryKey? device = ports.OpenSubKey(name);
            string[] hardwareIds = device?.GetValue("HardwareID") as string[] ?? [];
            if (!hardwareIds.Contains(HardwareId, StringComparer.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNode(out _, $@"ROOT\PORTS\{name}", 0) == 0)
                return true;
        }
        return false;
    }

    private static void RemoveDriverPackages()
    {
        string infDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (string path in Directory.EnumerateFiles(infDirectory, "oem*.inf").ToArray())
        {
            string name = Path.GetFileName(path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^oem\d+\.inf$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            string contents = File.ReadAllText(path);
            if (!contents.Contains("ClassGuid={4D36E978-E325-11CE-BFC1-08002BE10318}",
                    StringComparison.OrdinalIgnoreCase) ||
                !contents.Contains("ProviderString=\"SerialPortSimulator\"",
                    StringComparison.OrdinalIgnoreCase) ||
                !contents.Contains("ROOT\\SerialPortSimulator", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!SetupUninstallOEMInf(name, 0, IntPtr.Zero))
                throw new DeviceManagerException(ExitCodes.DriverCleanupFailed,
                    $"Could not remove driver package {name}: Win32 {Marshal.GetLastWin32Error()}.");
            DeviceManagerLog.Write($"Removed driver package {name} from Driver Store.");
        }
    }

    private void RemoveTestCertificateIfPresent()
    {
        if (!File.Exists(_certificatePath)) return;
        using X509Certificate2 certificate =
            X509CertificateLoader.LoadCertificateFromFile(_certificatePath);
        foreach (StoreName storeName in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (X509Certificate2 match in store.Certificates.Find(
                         X509FindType.FindByThumbprint, certificate.Thumbprint, false))
                store.Remove(match);
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint deviceInstance, string deviceId, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupUninstallOEMInfW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupUninstallOEMInf(string infFileName, uint flags, IntPtr reserved);

    private void ValidateDriverPackage()
    {
        ExtractEmbeddedDriverPackage();
        foreach (string path in new[]
                 {
                     _infPath,
                     Path.Combine(_driverRoot, "VirtualSerial2um.dll"),
                     Path.Combine(_driverRoot, "wudf.cat"),
                     _devconPath
                 })
        {
            if (!File.Exists(path))
                throw new DeviceManagerException(
                    ExitCodes.DriverPackageMissing,
                    $"Required driver package file is missing: {path}");
        }
    }

    private void ExtractEmbeddedDriverPackage()
    {
        Directory.CreateDirectory(_driverRoot);
        Assembly assembly = Assembly.GetExecutingAssembly();
        var resources = new (string Name, string FileName, bool Required)[]
        {
            ("VirtualSerial2um.dll", "VirtualSerial2um.dll", true),
            ("virtualserial2um.inf", "virtualserial2um.inf", true),
            ("wudf.cat", "wudf.cat", true),
            ("VirtualSerial2um.cer", "VirtualSerial2um.cer", false),
            ("devcon.exe", "devcon.exe", true)
        };

        foreach (var resource in resources)
        {
            using Stream? stream = assembly.GetManifestResourceStream(
                ResourcePrefix + resource.Name);
            if (stream is null)
            {
                if (resource.Required)
                    throw new DeviceManagerException(
                        ExitCodes.DriverPackageMissing,
                        $"Embedded driver package resource is missing: {resource.Name}");
                continue;
            }

            string destination = Path.Combine(_driverRoot, resource.FileName);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(output);
        }
        DeviceManagerLog.Write($"Extracted embedded driver package to {_driverRoot}.");
    }

    public void Dispose()
    {
        // devcon has exited before the service is disposed. Only delete the
        // exact files extracted by this application; leave unknown files alone.
        bool cleanupSucceeded = true;
        foreach (string directory in new[] { _driverRoot, _packageRoot })
        {
            foreach (string fileName in PackageFiles)
            {
                string path = Path.Combine(directory, fileName);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex)
                {
                    cleanupSucceeded = false;
                    DeviceManagerLog.Write($"Could not clean {path}: {ex.Message}");
                }
            }
            try { if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (IOException)
            {
                if (directory == _driverRoot) cleanupSucceeded = false;
            }
            catch (UnauthorizedAccessException ex)
            {
                cleanupSucceeded = false;
                DeviceManagerLog.Write($"Could not clean {directory}: {ex.Message}");
            }
        }
        if (_removeManagerLogAfterDispose && cleanupSucceeded)
            DeviceManagerLog.DeleteAfterUninstall();
    }

    private void ImportTestCertificateIfPresent()
    {
        if (!File.Exists(_certificatePath))
        {
            DeviceManagerLog.Write("No test certificate found; assuming production-signed driver package.");
            return;
        }

        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(_certificatePath);
            AddToStore(certificate, StoreName.Root);
            AddToStore(certificate, StoreName.TrustedPublisher);
            DeviceManagerLog.Write($"Trusted test certificate {certificate.Thumbprint}.");
        }
        catch (Exception ex)
        {
            throw new DeviceManagerException(
                ExitCodes.CertificateInstallFailed,
                $"Failed to trust test certificate: {ex.Message}");
        }
    }

    private static void AddToStore(X509Certificate2 certificate, StoreName storeName)
    {
        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        bool exists = store.Certificates.Find(
            X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
        if (!exists) store.Add(certificate);
    }

    private static void ValidateHardwareId(string instanceId)
    {
        string keyPath = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key is null)
            throw new DeviceManagerException(ExitCodes.DeviceNotFound, $"Device not found: {instanceId}");

        string[] hardwareIds = key.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
        if (!hardwareIds.Contains(HardwareId, StringComparer.OrdinalIgnoreCase))
            throw new DeviceManagerException(
                ExitCodes.HardwareIdMismatch,
                $"Refusing to remove non-simulator device {instanceId}.");
    }

    private DevconResult RunDevcon(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(_devconPath)
        {
            WorkingDirectory = _appRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        DeviceManagerLog.Write($"Running: devcon {string.Join(' ', arguments)}");
        using Process process = Process.Start(startInfo) ??
            throw new DeviceManagerException(
                ExitCodes.UnexpectedFailure, "Could not start devcon.exe.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(output)) DeviceManagerLog.Write(output.Trim());
        if (!string.IsNullOrWhiteSpace(error)) DeviceManagerLog.Write(error.Trim());
        return new DevconResult(process.ExitCode);
    }

    private readonly record struct DevconResult(int ExitCode);
}

internal sealed class DeviceManagerException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal static class DeviceManagerLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SerialPortSimulator",
        "device-manager.log");

    public static void Write(string message)
    {
        try
        {
            BoundedLogFile.Append(
                LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");
        }
        catch
        {
            // Logging must not hide the result of a privileged device operation.
        }
    }

    public static void DeleteAfterUninstall()
    {
        try
        {
            if (File.Exists(LogPath)) File.Delete(LogPath);
            string directory = Path.GetDirectoryName(LogPath)!;
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
