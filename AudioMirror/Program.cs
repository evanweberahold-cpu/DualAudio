using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("==============================================");
        Console.WriteLine("  AudioMirror - Dual Output for Windows 11");
        Console.WriteLine("==============================================");
        Console.WriteLine();

        try
        {
            var devices = AudioDeviceHelper.GetPlaybackDevices();

            if (devices.Count == 0)
            {
                Console.WriteLine("No audio playback devices found.");
                Pause(); return;
            }

            Console.WriteLine("Available playback devices:");
            for (int i = 0; i < devices.Count; i++)
                Console.WriteLine($"  [{i}] {devices[i].Name}");

            Console.WriteLine();
            Console.Write("Select PRIMARY device (e.g. your Jabra) [number]: ");
            int primary = ReadDeviceIndex(devices.Count);
            Console.Write("Select SECONDARY device (e.g. wired headphones) [number]: ");
            int secondary = ReadDeviceIndex(devices.Count);

            if (primary == secondary)
            {
                Console.WriteLine("Primary and secondary devices must be different.");
                Pause(); return;
            }

            Console.WriteLine();
            Console.WriteLine($"Primary:   {devices[primary].Name}");
            Console.WriteLine($"Secondary: {devices[secondary].Name}");
            Console.WriteLine();
            Console.WriteLine("Starting audio mirror... Press ENTER to stop.");
            Console.WriteLine();

            using var mirror = new AudioMirror(devices[primary].Id, devices[secondary].Id);
            mirror.Start();
            Console.ReadLine();
            mirror.Stop();
            Console.WriteLine("Audio mirror stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("Tip: Make sure both devices are connected and visible in Sound Settings.");
            Pause();
        }
    }

    static int ReadDeviceIndex(int max)
    {
        while (true)
        {
            var input = Console.ReadLine();
            if (int.TryParse(input, out int idx) && idx >= 0 && idx < max) return idx;
            Console.Write($"Please enter a number between 0 and {max - 1}: ");
        }
    }

    static void Pause() { Console.WriteLine("Press any key to exit..."); Console.ReadKey(); }
}

class AudioDeviceInfo { public string Id; public string Name; }

class AudioDeviceHelper
{
    public static List<AudioDeviceInfo> GetPlaybackDevices()
    {
        var result = new List<AudioDeviceInfo>();
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE, out IMMDeviceCollection collection);
        collection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out IMMDevice device);
            device.GetId(out string id);
            device.OpenPropertyStore(0, out IPropertyStore store);
            var key = PropertyKeys.PKEY_Device_FriendlyName;
            store.GetValue(ref key, out PropVariant prop);
            result.Add(new AudioDeviceInfo { Id = id, Name = prop.Data ?? $"Device {i}" });
            Marshal.ReleaseComObject(store);
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(collection);
        Marshal.ReleaseComObject(enumerator);
        return result;
    }
}

class AudioMirror : IDisposable
{
    private readonly string _sourceId, _targetId;
    private Thread _thread;
    private volatile bool _running;

    public AudioMirror(string sourceId, string targetId) { _sourceId = sourceId; _targetId = targetId; }

    public void Start()
    {
        _running = true;
        _thread = new Thread(MirrorLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop() { _running = false; _thread?.Join(3000); }

    private void MirrorLoop()
    {
        IMMDevice srcDev = null, tgtDev = null;
        IAudioClient srcClient = null, tgtClient = null;
        IAudioCaptureClient captureClient = null;
        IAudioRenderClient renderClient = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
            enumerator.GetDevice(_sourceId, out srcDev);
            enumerator.GetDevice(_targetId, out tgtDev);
            Marshal.ReleaseComObject(enumerator);

            var audioClientGuid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            srcDev.Activate(ref audioClientGuid, 0, IntPtr.Zero, out object srcObj);
            tgtDev.Activate(ref audioClientGuid, 0, IntPtr.Zero, out object tgtObj);
            srcClient = (IAudioClient)srcObj;
            tgtClient = (IAudioClient)tgtObj;

            srcClient.GetMixFormat(out IntPtr fmtPtr);
            WaveFormatEx fmt = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            long buf = 10000000;

            srcClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, buf, 0, fmtPtr, Guid.Empty);
            tgtClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, buf, 0, fmtPtr, Guid.Empty);
            Marshal.FreeCoTaskMem(fmtPtr);

            var captureGuid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            var renderGuid  = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            srcClient.GetService(ref captureGuid, out object capObj);
            tgtClient.GetService(ref renderGuid,  out object renObj);
            captureClient = (IAudioCaptureClient)capObj;
            renderClient  = (IAudioRenderClient)renObj;

            srcClient.Start();
            tgtClient.Start();
            Console.WriteLine("Mirror active! Audio is now playing on both devices.");

            while (_running)
            {
                Thread.Sleep(10);
                captureClient.GetNextPacketSize(out uint packetSize);
                while (packetSize > 0)
                {
                    captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out AudioClientBufferFlags flags, out _, out _);
                    renderClient.GetBuffer(numFrames, out IntPtr renderPtr);
                    int bytes = (int)(numFrames * fmt.nBlockAlign);
                    byte[] buf2 = new byte[bytes];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                        Marshal.Copy(dataPtr, buf2, 0, bytes);
                    Marshal.Copy(buf2, 0, renderPtr, bytes);
                    renderClient.ReleaseBuffer(numFrames, AudioClientBufferFlags.None);
                    captureClient.ReleaseBuffer(numFrames);
                    captureClient.GetNextPacketSize(out packetSize);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Mirror error: {ex.Message}"); }
        finally
        {
            srcClient?.Stop(); tgtClient?.Stop();
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (renderClient  != null) Marshal.ReleaseComObject(renderClient);
            if (srcClient     != null) Marshal.ReleaseComObject(srcClient);
            if (tgtClient     != null) Marshal.ReleaseComObject(tgtClient);
            if (srcDev        != null) Marshal.ReleaseComObject(srcDev);
            if (tgtDev        != null) Marshal.ReleaseComObject(tgtDev);
        }
    }
    public void Dispose() => Stop();
}

[StructLayout(LayoutKind.Sequential)]
struct WaveFormatEx
{
    public ushort wFormatTag, nChannels;
    public uint nSamplesPerSec, nAvgBytesPerSec;
    public ushort nBlockAlign, wBitsPerSample, cbSize;
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort vt, r1, r2, r3;
    [MarshalAs(UnmanagedType.LPWStr)] public string Data;
}

static class PropertyKeys
{
    public static Guid PKEY_Device_FriendlyName = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
}

enum EDataFlow { eRender, eCapture, eAll }
[Flags] enum DeviceState : uint { ACTIVE = 1 }
enum AudioClientShareMode { Shared, Exclusive }
[Flags] enum AudioClientStreamFlags : uint { None = 0, Loopback = 0x00020000 }
[Flags] enum AudioClientBufferFlags { None = 0, Silent = 2 }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
    void GetDevice(string pwstrId, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr c);
    void UnregisterEndpointNotificationCallback(IntPtr c);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out object iface);
    void OpenPropertyStore(uint access, out IPropertyStore store);
    void GetId(out string id);
    void GetState(out uint state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint prop, out Guid key);
    void GetValue(ref Guid key, out PropVariant value);
    void SetValue(ref Guid key, ref PropVariant value);
    void Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, Guid audioSessionGuid);
    void GetBufferSize(out uint bufferSize);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint padding);
    void IsFormatSupported(AudioClientShareMode shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
    void GetMixFormat(out IntPtr ppDeviceFormat);
    void GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService(ref Guid riid, out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr ppData, out uint pNumFramesRead, out AudioClientBufferFlags pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
    void ReleaseBuffer(uint numFramesRead);
    void GetNextPacketSize(out uint pNumFramesInNextPacket);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    void GetBuffer(uint numFramesRequested, out IntPtr ppData);
    void ReleaseBuffer(uint numFramesWritten, AudioClientBufferFlags dwFlags);
}
