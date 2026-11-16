namespace NusaTrainer.Core.Memory;

public static class ProcessMemoryFactory
{
    public static IProcessMemory Open(int pid)
    {
        if (OperatingSystem.IsWindows()) return new WindowsProcessMemory(pid);
        if (OperatingSystem.IsLinux()) return new LinuxProcessMemory(pid);
        throw new PlatformNotSupportedException("Sistem operasi tidak didukung.");
    }
}
