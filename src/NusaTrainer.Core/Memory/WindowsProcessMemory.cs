using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NusaTrainer.Core.Memory;

/// <summary>Akses memori proses lain di Windows lewat Win32 API.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsProcessMemory : IProcessMemory
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint SYNCHRONIZE = 0x00100000;

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint LIST_MODULES_ALL = 0x03;
    private const uint WAIT_TIMEOUT = 0x102;

    private readonly IntPtr _handle;
    private bool _disposed;

    public int ProcessId { get; }
    public bool Is64Bit { get; }

    public WindowsProcessMemory(int processId)
    {
        ProcessId = processId;
        _handle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION | SYNCHRONIZE,
            false, processId);
        if (_handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new MemoryAccessException(err == 5
                ? "Akses ditolak saat membuka proses game. Jalankan NusaTrainer sebagai Administrator."
                : $"Gagal membuka proses {processId}: {new Win32Exception(err).Message}");
        }

        Is64Bit = Environment.Is64BitOperatingSystem && !(IsWow64Process(_handle, out bool wow64) && wow64);
    }

    public bool HasExited => _disposed || WaitForSingleObject(_handle, 0) != WAIT_TIMEOUT;

    public unsafe bool TryRead(ulong address, Span<byte> buffer)
    {
        if (buffer.IsEmpty) return true;
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(_handle, (IntPtr)(long)address, (IntPtr)p, (IntPtr)buffer.Length, out var read)
                   && (long)read == buffer.Length;
        }
    }

    public unsafe bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return true;
        fixed (byte* p = data)
        {
            if (WriteProcessMemory(_handle, (IntPtr)(long)address, (IntPtr)p, (IntPtr)data.Length, out var written)
                && (long)written == data.Length)
                return true;

            // Mungkin halaman read-only/kode: ubah proteksi sementara.
            if (!VirtualProtectEx(_handle, (IntPtr)(long)address, (IntPtr)data.Length, PAGE_EXECUTE_READWRITE, out uint old))
                return false;
            try
            {
                bool ok = WriteProcessMemory(_handle, (IntPtr)(long)address, (IntPtr)p, (IntPtr)data.Length, out written)
                          && (long)written == data.Length;
                FlushInstructionCache(_handle, (IntPtr)(long)address, (IntPtr)data.Length);
                return ok;
            }
            finally
            {
                VirtualProtectEx(_handle, (IntPtr)(long)address, (IntPtr)data.Length, old, out _);
            }
        }
    }

    public IReadOnlyList<MemoryRegion> GetRegions()
    {
        var list = new List<MemoryRegion>();
        ulong addr = 0;
        ulong max = Is64Bit ? 0x7FFF_FFFF_FFFFUL : 0xFFFF_FFFFUL;
        var size = (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < max)
        {
            if (VirtualQueryEx(_handle, (IntPtr)(long)addr, out var mbi, size) == IntPtr.Zero)
                break;
            ulong baseAddr = (ulong)(long)mbi.BaseAddress;
            ulong regionSize = (ulong)(long)mbi.RegionSize;
            if (regionSize == 0) break;

            if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & PAGE_NOACCESS) == 0)
            {
                uint prot = mbi.Protect & 0xFF;
                bool exec = prot is 0x10 or 0x20 or 0x40 or 0x80;
                bool write = prot is 0x04 or 0x08 or 0x40 or 0x80;
                list.Add(new MemoryRegion(baseAddr, regionSize, true, write, exec));
            }
            addr = baseAddr + regionSize;
        }
        return list;
    }

    public IReadOnlyList<ModuleInfo> GetModules()
    {
        var result = new List<ModuleInfo>();
        var modules = new IntPtr[1024];
        uint cb = (uint)(IntPtr.Size * modules.Length);
        if (!EnumProcessModulesEx(_handle, modules, cb, out uint needed, LIST_MODULES_ALL))
            return result;
        int count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)modules.Length);
        var name = new StringBuilder(260);
        var path = new StringBuilder(1024);
        for (int i = 0; i < count; i++)
        {
            name.Clear(); path.Clear();
            if (GetModuleBaseNameW(_handle, modules[i], name, (uint)name.Capacity) == 0) continue;
            GetModuleFileNameExW(_handle, modules[i], path, (uint)path.Capacity);
            if (!GetModuleInformation(_handle, modules[i], out var info, (uint)Marshal.SizeOf<MODULEINFO>())) continue;
            result.Add(new ModuleInfo(name.ToString(), (ulong)(long)info.lpBaseOfDll, info.SizeOfImage, path.ToString()));
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr h, IntPtr addr, IntPtr buffer, IntPtr size, out IntPtr read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr h, IntPtr addr, IntPtr buffer, IntPtr size, out IntPtr written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(IntPtr h, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr h, uint ms);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] modules, uint cb, out uint needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(IntPtr h, IntPtr module, StringBuilder name, uint size);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameExW(IntPtr h, IntPtr module, StringBuilder name, uint size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleInformation(IntPtr h, IntPtr module, out MODULEINFO info, uint cb);
}
