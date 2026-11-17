using SerialPortSimulator.DeviceManager;

return ProgramEntry.Run(args);

internal static class ProgramEntry
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
                throw new DeviceManagerException(ExitCodes.InvalidArguments, "Missing device manager action.");

            using var service = new DeviceManagerService();
            string action = args[0].ToLowerInvariant();

            int result = action switch
            {
                "create" => service.CreateDevice(),
                "update" => service.UpdateDriver(),
                "remove" => service.RemoveDevice(
                    GetOption(args, "--instance-id") ??
                    throw new DeviceManagerException(
                        ExitCodes.InvalidArguments, "Missing --instance-id for remove.")),
                _ => throw new DeviceManagerException(
                    ExitCodes.InvalidArguments, $"Unknown action: {args[0]}")
            };

            DeviceManagerLog.Write($"Completed action '{action}'.");
            return result;
        }
        catch (DeviceManagerException ex)
        {
            DeviceManagerLog.Write($"FAILED: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            DeviceManagerLog.Write($"UNEXPECTED: {ex}");
            return ExitCodes.UnexpectedFailure;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 10;
    public const int DriverPackageMissing = 11;
    public const int CertificateInstallFailed = 12;
    public const int DeviceCreationFailed = 13;
    public const int DriverInstallFailed = 14;
    public const int DeviceNotFound = 15;
    public const int HardwareIdMismatch = 16;
    public const int DeviceRemovalFailed = 17;
    public const int DriverCleanupFailed = 18;
    public const int DriverPackageUnsigned = 19;
    public const int UnexpectedFailure = 99;
}
