using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using SerialPortSimulator.Services;

namespace SerialPortSimulator.DeviceManager;

internal sealed class DeviceManagerService : IDisposable
{
    private const string HardwareId = @"ROOT\SerialPortSimulator";
    private const string ResourcePrefix = "SerialPortSimulator.DeviceManager.DriverResources.";
    private static readonly Guid PortsClassGuid = new("4D36E978-E325-11CE-BFC1-08002BE10318");
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint DicdGenerateId = 0x00000001;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint DifRemove = 0x00000005;
    private const uint DiRemoveDeviceGlobal = 0x00000001;
    private const uint InstallFlagForce = 0x00000001;
    private const uint SpostPath = 0x00000001;
    private const uint CmReenumerateSynchronous = 0x00000001;
    private const uint DnStarted = 0x00000008;
    private const uint CmProbNeedRestart = 0x0000000E;
    private const uint SetupApiErrorPrefix = 0xE0000000;
    private static readonly string[] PackageFiles =
    [
        "VirtualSerial2um.dll", "virtualserial2um.inf", "wudf.cat",
        "VirtualSerial2um.cer"
    ];
    private readonly string _packageRoot;
    private readonly string _driverRoot;
    private readonly string _infPath;
    private readonly string _catalogPath;
    private readonly string _certificatePath;
    private bool _removeManagerLogAfterDispose;

    public DeviceManagerService()
    {
        _packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SerialPortSimulator",
            "DevicePackage");
        _driverRoot = Path.Combine(_packageRoot, Guid.NewGuid().ToString("N"));
        _infPath = Path.Combine(_driverRoot, "virtualserial2um.inf");
        _catalogPath = Path.Combine(_driverRoot, "wudf.cat");
        _certificatePath = Path.Combine(_driverRoot, "VirtualSerial2um.cer");
    }

    public int CreateDevice()
    {
        ValidateDriverPackage();
        ImportTestCertificateIfPresent();
        RemoveFailedSimulatorDevices();
        CreateAndInstallRootDevice();

        DeviceManagerLog.Write($"Created device for {HardwareId}.");
        return ExitCodes.Success;
    }

    public int UpdateDriver()
    {
        ValidateDriverPackage();
        ImportTestCertificateIfPresent();
        InstallOrUpdateDriver(ExitCodes.DriverInstallFailed);

        DeviceManagerLog.Write($"Updated devices for {HardwareId}.");
        return ExitCodes.Success;
    }

    public int RemoveDevice(string instanceId)
    {
        if (!instanceId.StartsWith(@"ROOT\PORTS\", StringComparison.OrdinalIgnoreCase))
            throw new DeviceManagerException(ExitCodes.InvalidArguments, "Invalid device instance ID.");

        ValidateHardwareId(instanceId);
        ValidateDriverPackage();
        RemoveDeviceCore(instanceId, ExitCodes.DeviceRemovalFailed);

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
                    $"Could not remove driver package {name}: {FormatNativeError()}.");
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

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern uint CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint deviceInstance, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(
        uint deviceInstance, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Reenumerate_DevNode")]
    private static extern uint CM_Reenumerate_DevNode(uint deviceInstance, uint flags);

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
                     _catalogPath,
                     Path.Combine(_driverRoot, "VirtualSerial2um.dll")
                 })
        {
            if (!File.Exists(path))
                throw new DeviceManagerException(
                    ExitCodes.DriverPackageMissing,
                    $"Required driver package file is missing: {path}");
        }
        ValidateCatalogSignature();
    }

    private void ValidateCatalogSignature()
    {
        X509Certificate signer;
        try
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader has no signed-file overload.
            signer = X509Certificate.CreateFromSignedFile(_catalogPath);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException ex)
        {
            throw new DeviceManagerException(
                ExitCodes.DriverPackageUnsigned,
                $"Driver catalog is not signed: {ex.Message}");
        }

        using (signer)
        {
            if (!File.Exists(_certificatePath)) return;
            using X509Certificate2 expected =
                X509CertificateLoader.LoadCertificateFromFile(_certificatePath);
            if (!string.Equals(signer.GetCertHashString(), expected.Thumbprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new DeviceManagerException(
                    ExitCodes.DriverPackageUnsigned,
                    "Embedded driver certificate does not match the catalog signer.");
        }
    }

    private void RemoveFailedSimulatorDevices()
    {
        const uint deviceStarted = 0x00000008;
        foreach (string instanceId in EnumerateSimulatorDeviceInstanceIds())
        {
            if (CM_Locate_DevNode(out uint deviceInstance, instanceId, 0) != 0) continue;
            if (CM_Get_DevNode_Status(out uint status, out _, deviceInstance, 0) != 0) continue;
            if ((status & deviceStarted) != 0) continue;

            try
            {
                RemoveDeviceCore(instanceId, ExitCodes.DeviceCreationFailed);
                DeviceManagerLog.Write(
                    $"Removed failed device {instanceId} before creating a new device.");
            }
            catch (DeviceManagerException ex)
            {
                // Cleanup is best-effort. The device can disappear between
                // enumeration and SetupAPI opening it, and a stale failed
                // node must not prevent creation of a fresh device.
                DeviceManagerLog.Write(
                    $"Could not remove failed device {instanceId}; " +
                    $"continuing with device creation: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> EnumerateSimulatorDeviceInstanceIds()
    {
        using RegistryKey? ports = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\ROOT\PORTS");
        if (ports is null) yield break;

        foreach (string name in ports.GetSubKeyNames())
        {
            using RegistryKey? device = ports.OpenSubKey(name);
            string[] hardwareIds = device?.GetValue("HardwareID") as string[] ?? [];
            if (hardwareIds.Contains(HardwareId, StringComparer.OrdinalIgnoreCase))
                yield return $@"ROOT\PORTS\{name}";
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
            ("VirtualSerial2um.cer", "VirtualSerial2um.cer", false)
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
        // Only delete the exact files extracted by this application; leave
        // unknown files in the shared package directory alone.
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

    private void CreateAndInstallRootDevice()
    {
        var className = new char[256];
        if (!SetupDiGetINFClass(_infPath, out Guid classGuid, className,
                (uint)className.Length, IntPtr.Zero))
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not read the driver class from the INF: {FormatNativeError()}.");

        string classNameText = new string(className).TrimEnd('\0');
        if (string.IsNullOrEmpty(classNameText))
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                "The INF did not declare a device class name.");

        IntPtr deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not create a device information set: {FormatNativeError()}.");

        try
        {
            var deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            if (!SetupDiCreateDeviceInfo(deviceInfoSet, classNameText, ref classGuid,
                    null, IntPtr.Zero, DicdGenerateId, ref deviceInfoData))
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not create the device node: {FormatNativeError()}.");

            byte[] hardwareIdList = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData,
                    SpdrpHardwareId, hardwareIdList, (uint)hardwareIdList.Length))
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not set the device hardware ID: {FormatNativeError()}.");

            if (!SetupDiRegisterDeviceInfo(deviceInfoSet, ref deviceInfoData, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not register the device node: {FormatNativeError()}.");

            var instanceId = new char[256];
            uint getIdResult = CM_Get_Device_ID(deviceInfoData.DevInst, instanceId,
                (uint)instanceId.Length, 0);
            if (getIdResult != 0)
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not read the new device instance ID: CONFIGRET {getIdResult}.");
            string instanceIdText = new string(instanceId).TrimEnd('\0');
            DeviceManagerLog.Write($"Registered device node {instanceIdText}.");

            StageDriverPackage();
            ReenumerateRootDevices();
            InstallDriverOnDevice(instanceIdText);
            if (CM_Locate_DevNode(out uint installedInstance, instanceIdText, 0) != 0)
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not locate the new device {instanceIdText} after re-enumeration.");
            EnsureDeviceInstalled(installedInstance);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private void InstallOrUpdateDriver(int failureExitCode)
    {
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, _infPath,
                InstallFlagForce, out bool rebootRequired))
            throw new DeviceManagerException(
                failureExitCode,
                $"Driver installation failed: {FormatNativeError()}.");

        if (rebootRequired)
            DeviceManagerLog.Write("A system restart is required to complete driver installation.");
    }

    private static void ReenumerateRootDevices()
    {
        if (CM_Locate_DevNode(out uint rootDevice, @"HTREE\ROOT\0", 0) != 0)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                "Could not locate the root device node.");

        uint result = CM_Reenumerate_DevNode(rootDevice, CmReenumerateSynchronous);
        if (result != 0)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not re-enumerate the root bus: CONFIGRET {result}.");
    }

    private static void InstallDriverOnDevice(string instanceId)
    {
        Guid classGuid = PortsClassGuid;
        IntPtr deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not create a device information set: {FormatNativeError()}.");

        try
        {
            var deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            if (!SetupDiOpenDeviceInfo(deviceInfoSet, instanceId, IntPtr.Zero, 0, ref deviceInfoData))
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not open the new device {instanceId}: {FormatNativeError()}.");

            if (!DiInstallDevice(IntPtr.Zero, deviceInfoSet, ref deviceInfoData,
                    IntPtr.Zero, 0, out bool rebootRequired))
                throw new DeviceManagerException(
                    ExitCodes.DeviceCreationFailed,
                    $"Could not install the driver on {instanceId}: {FormatNativeError()}.");

            if (rebootRequired)
                DeviceManagerLog.Write(
                    "A system restart is required to complete driver installation.");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static void EnsureDeviceInstalled(uint deviceInstance)
    {
        uint result = CM_Get_DevNode_Status(out uint status, out uint problem,
            deviceInstance, 0);
        if (result != 0)
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not query the new device status: CONFIGRET {result}.");
        if ((status & DnStarted) != 0) return;
        if (problem == CmProbNeedRestart)
        {
            DeviceManagerLog.Write(
                "A system restart is required to complete driver installation.");
            return;
        }
        throw new DeviceManagerException(
            ExitCodes.DeviceCreationFailed,
            $"The new device did not start (problem {problem}).");
    }

    private void StageDriverPackage()
    {
        var destinationInf = new StringBuilder(260);
        if (!SetupCopyOEMInf(_infPath, null, SpostPath, 0, destinationInf,
                (uint)destinationInf.Capacity, IntPtr.Zero, IntPtr.Zero))
            throw new DeviceManagerException(
                ExitCodes.DeviceCreationFailed,
                $"Could not stage the driver package: {FormatNativeError()}.");
    }

    private static void RemoveDeviceCore(string instanceId, int failureExitCode)
    {
        IntPtr deviceInfoSet = SetupDiCreateDeviceInfoListWithoutClass(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == InvalidHandleValue)
            throw new DeviceManagerException(
                failureExitCode,
                $"Could not create a device information set: {FormatNativeError()}.");

        try
        {
            var deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            if (!SetupDiOpenDeviceInfo(deviceInfoSet, instanceId, IntPtr.Zero, 0, ref deviceInfoData))
                throw new DeviceManagerException(
                    failureExitCode,
                    $"Could not open device {instanceId}: {FormatNativeError()}.");

            var removeParams = new SP_REMOVEDEVICE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = DifRemove
                },
                Scope = DiRemoveDeviceGlobal,
                HwProfile = 0
            };
            if (!SetupDiSetClassInstallParams(deviceInfoSet, ref deviceInfoData,
                    ref removeParams.ClassInstallHeader,
                    (uint)Marshal.SizeOf<SP_REMOVEDEVICE_PARAMS>()) ||
                !SetupDiCallClassInstaller(DifRemove, deviceInfoSet, ref deviceInfoData))
                throw new DeviceManagerException(
                    failureExitCode,
                    $"Could not remove device {instanceId}: {FormatNativeError()}.");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string FormatNativeError()
    {
        return FormatNativeError(Marshal.GetLastWin32Error());
    }

    private static string FormatNativeError(int error)
    {
        uint code = unchecked((uint)error);
        if ((code & 0xFFFF0000) == SetupApiErrorPrefix)
        {
            string description = (code & 0x0000FFFF) switch
            {
                0x201 => "ERROR_CLASS_MISMATCH",
                0x20B => "ERROR_NO_SUCH_DEVINST",
                0x225 => "ERROR_NO_SUCH_DEVICE_INTERFACE",
                _ => "SetupAPI error"
            };
            return $"SetupAPI 0x{code:X8} ({description})";
        }

        return $"Win32 {error} (0x{code:X8})";
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupDiGetINFClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetINFClass(
        string infName, out Guid classGuid, [Out] char[] className,
        uint classNameSize, IntPtr requiredSize);

    [DllImport("setupapi.dll", SetLastError = true,
        EntryPoint = "SetupDiCreateDeviceInfoList")]
    private static extern IntPtr SetupDiCreateDeviceInfoList(
        ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true,
        EntryPoint = "SetupDiCreateDeviceInfoList")]
    private static extern IntPtr SetupDiCreateDeviceInfoListWithoutClass(
        IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiRegisterDeviceInfo(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint flags, IntPtr compareProc, IntPtr compareContext,
        IntPtr duplicateDeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupDiCreateDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupDiOpenDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(
        IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent,
        uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupDiSetClassInstallParamsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_CLASSINSTALL_HEADER classInstallParams, uint classInstallParamsSize);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiInstallDevice(
        IntPtr hwndParent, IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData, IntPtr driverInfoData,
        uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "SetupCopyOEMInfW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupCopyOEMInf(
        string sourceInfFileName, string? oemSourceMediaLocation,
        uint oemSourceMediaType, uint copyStyle,
        [Out] StringBuilder destinationInfFileName,
        uint destinationInfFileNameSize, IntPtr requiredSize,
        IntPtr destinationInfFileNameComponent);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_REMOVEDEVICE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint Scope;
        public uint HwProfile;
    }
}

internal sealed class DeviceManagerException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal static class DeviceManagerLog
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        "Logs",
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
