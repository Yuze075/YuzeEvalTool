#nullable enable
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YuzeToolkit.Eval.Broker;

// ConPTY belongs to the terminal task, not the user's desktop. No GUI/session activation.
internal sealed class WorkspaceWindowsPty : IDisposable
{
    private IntPtr _console;
    public Process Process { get; }
    public StreamReader Output { get; }
    public StreamWriter Input { get; }
    public WorkspaceWindowsPty(ProcessStartInfo start, short columns, short rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero, attributes = IntPtr.Zero;
        var launched = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coordinate(columns, rows), inputRead, outputWrite, 0, out _console));
            CloseHandle(inputRead); inputRead = IntPtr.Zero; CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, _console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            // Explicit null standard handles prevent Windows from duplicating the Broker's
            // redirected service/SSH handles instead of attaching the child to ConPTY.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100 }, AttributeList = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400, IntPtr.Zero, start.WorkingDirectory, ref startup, out var info)) throw new Win32Exception();
            launched = true;
            try { Process = Process.GetProcessById(info.ProcessId); }
            finally { CloseHandle(info.Process); CloseHandle(info.Thread); }
            var inputHandle = new SafeFileHandle(inputWrite, true); inputWrite = IntPtr.Zero;
            var outputHandle = new SafeFileHandle(outputRead, true); outputRead = IntPtr.Zero;
            Input = new StreamWriter(new FileStream(inputHandle, FileAccess.Write, 4096, false), new UTF8Encoding(false)) { AutoFlush = true };
            Output = new StreamReader(new FileStream(outputHandle, FileAccess.Read, 4096, false), Encoding.UTF8);
        }
        catch
        {
            CloseConsole();
            if (launched) throw new BrokerOperationException("ExecutionOutcomeUnknown", "The process was created but its PTY could not finish attaching. Its side effects are unknown.", true);
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite }) if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public void Resize(short columns, short rows) => Marshal.ThrowExceptionForHR(ResizePseudoConsole(_console, new Coordinate(columns, rows)));
    public void CloseConsole() { var console = Interlocked.Exchange(ref _console, IntPtr.Zero); if (console != IntPtr.Zero) ClosePseudoConsole(console); }
    public void Dispose() { CloseConsole(); Input.Dispose(); Output.Dispose(); }
    private static string Quote(string value)
    {
        var output = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') output.Append('\\', slashes * 2 + 1).Append('"');
            else output.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coordinate(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved; public IntPtr Desktop; public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedBytes; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coordinate size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coordinate size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
