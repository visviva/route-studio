using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MapConverter;

internal sealed class Casc : IDisposable
{
    private readonly nint library;
    private readonly nint storage;
    private readonly OpenFile openFile;
    private readonly GetFileSize getFileSize;
    private readonly ReadFile readFile;
    private readonly CloseHandle closeFile;
    private readonly CloseHandle closeStorage;
    private readonly object gate = new();

    public Casc(string dll, string install, string build)
    {
        string buildInfo = Path.Combine(install, ".build.info");
        var rows = File.ReadAllLines(buildInfo).Select(line => line.Split('|')).ToArray();
        int Column(string name) => Array.FindIndex(rows[0], column => column.Split('!')[0] == name);
        int active = Column("Active"), version = Column("Version"), product = Column("Product");
        if (active < 0 || version < 0 || product < 0 ||
            !rows.Skip(1).Any(row => row[active] == "1" && row[product] == "wow" && row[version] == build))
            throw new InvalidDataException($"Active Retail build in {buildInfo} does not match {build}.");

        library = NativeLibrary.Load(Path.GetFullPath(dll));
        try
        {
            var openStorage = Bind<OpenStorage>("CascOpenStorage");
            openFile = Bind<OpenFile>("CascOpenFile");
            getFileSize = Bind<GetFileSize>("CascGetFileSize64");
            readFile = Bind<ReadFile>("CascReadFile");
            closeFile = Bind<CloseHandle>("CascCloseFile");
            closeStorage = Bind<CloseHandle>("CascCloseStorage");

            // The Windows library is built with CASC_UNICODE. The product specifier selects Retail.
            nint path = OperatingSystem.IsWindows()
                ? Marshal.StringToCoTaskMemUni(Path.GetFullPath(install) + "*wow")
                : Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(install) + "*wow");
            try
            {
                if (!openStorage(path, 0, out storage))
                    throw new IOException($"CascOpenStorage: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public byte[] Read(int fileId)
    {
        lock (gate)
        {
            if (!openFile(storage, (nint)fileId, 0, 3, out var file))
                throw Error(fileId, "open");
            try
            {
                if (!getFileSize(file, out var size)) throw Error(fileId, "size");
                if (size is 0 or > 64 * 1024 * 1024) throw new InvalidDataException($"FileDataID {fileId}: unexpected size {size}.");
                var bytes = new byte[(int)size];
                if (!readFile(file, bytes, (uint)size, out var read) || read != size)
                    throw Error(fileId, $"read ({read}/{size})");
                return bytes;
            }
            finally { closeFile(file); }
        }
    }

    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static IOException Error(int id, string operation) =>
        new($"FileDataID {id}: CASC {operation} failed ({Marshal.GetLastPInvokeError()}).");

    public void Dispose()
    {
        closeStorage(storage);
        NativeLibrary.Free(library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool OpenStorage(nint path, uint locale, out nint storage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool OpenFile(nint storage, nint fileId, uint locale, uint flags, out nint file);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetFileSize(nint file, out ulong size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ReadFile(nint file, [Out] byte[] buffer, uint requested, out uint read);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool CloseHandle(nint handle);
}

internal sealed class TileSource(Casc? casc, HttpClient? wago, string build, string cache)
{
    private long local, remote;
    public long LocalFiles => Interlocked.Read(ref local);
    public long RemoteFiles => Interlocked.Read(ref remote);

    public async ValueTask<byte[]> Read(int fileId, CancellationToken cancellation)
    {
        string? localError = null;
        if (casc is not null)
        {
            try
            {
                byte[] bytes = casc.Read(fileId);
                Interlocked.Increment(ref local);
                return bytes;
            }
            catch (IOException error) when (wago is not null) { localError = error.Message; }
        }
        if (wago is null) throw new InvalidOperationException("No tile source configured.");

        string path = Path.Combine(cache, build, (fileId / 1000).ToString(), fileId.ToString());
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, cancellation);
        try
        {
            var bytes = await Downloads.Get(wago, $"https://wago.tools/api/casc/{fileId}?build={build}", cancellation);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Output.Write(path, temporary => File.WriteAllBytes(temporary, bytes));
            Interlocked.Increment(ref remote);
            return bytes;
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"FileDataID {fileId}: {(localError is null ? "" : localError + " ")}Wago Tools: {error.Message}", error);
        }
    }
}

internal static class Downloads
{
    public static async Task<byte[]> Get(HttpClient http, string url, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, cancellation);
                if (attempt < 2 && ((int)response.StatusCode is 429 or 502 or 503 or 504))
                {
                    var pause = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pause.TotalSeconds, 1, 30)), cancellation);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellation);
            }
            catch (HttpRequestException error) when (attempt < 2 && error.StatusCode is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellation);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                if (attempt >= 2) throw new HttpRequestException("Request timed out.");
            }
        }
    }
}
