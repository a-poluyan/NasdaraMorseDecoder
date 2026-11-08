using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NasdaraMorseDecoder;

internal static class Loopback
{
    public static void Listen(int tone, Action<AudioStatus> report, CancellationToken stop)
    {
        var devices = (IDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    CaptureDefault(devices, tone, report, stop);
                }
                catch (COMException error)
                {
                    string message = error.HResult == unchecked((int)0x80070490)
                        ? "Устройство вывода недоступно"
                        : $"Захват недоступен (0x{error.HResult:X8}). Повторное подключение…";
                    report(new(message, "", 0));
                    stop.WaitHandle.WaitOne(750);
                }
                catch (NotSupportedException error)
                {
                    report(new(error.Message, "", 0));
                    stop.WaitHandle.WaitOne(750);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(devices);
        }
    }

    private static void CaptureDefault(IDeviceEnumerator devices, int tone,
        Action<AudioStatus> report, CancellationToken stop)
    {
        IDevice device = devices.GetDefaultAudioEndpoint(0, 1);
        IAudioClient? client = null;
        ICaptureClient? capture = null;
        bool started = false;
        try
        {
            string id = device.GetId();
            string name = DeviceName(device, id);
            Guid audioClientId = typeof(IAudioClient).GUID;
            client = (IAudioClient)device.Activate(ref audioClientId, 23, IntPtr.Zero);
            IntPtr mixFormat = client.GetMixFormat();
            MixFormat format;
            try
            {
                format = MixFormat.Read(mixFormat);
                client.Initialize(0, 0x20000, 2_000_000, 0, mixFormat, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }
            Guid captureId = typeof(ICaptureClient).GUID;
            capture = (ICaptureClient)client.GetService(ref captureId);
            client.Start();
            started = true;

            Bunker? completed = null;
            void Accept(Bunker bunker) => completed = bunker;
            MorseDecoder decoder = new(tone, Accept);
            PcmFrames frames = new(format.Rate, decoder);
            long nextSample = ClockPosition(format.Rate);
            long lastSilence = nextSample;
            frames.Silence(nextSample);
            bool received = false;
            byte[] buffer = [];
            var timer = Stopwatch.StartNew();
            long nextDeviceCheck = 500, nextReport = 0;
            double peakLevel = 0;
            long lastSound = -3000;
            while (!stop.IsCancellationRequested)
            {
                while (!stop.IsCancellationRequested && capture.GetNextPacketSize() > 0)
                {
                    capture.GetBuffer(out IntPtr data, out int count, out uint flags, out _, out ulong qpc);
                    try
                    {
                        bool badTimestamp = (flags & 4) != 0;
                        long position = badTimestamp ? ClockPosition(format.Rate) - count :
                            (long)(qpc * (format.Rate / 10_000_000d));
                        if (badTimestamp || (received && (flags & 1) != 0))
                        {
                            decoder = new(tone, Accept);
                            frames = new(format.Rate, decoder);
                            lastSilence = nextSample = position;
                        }
                        else if (position > nextSample + format.Rate / 20)
                        {
                            frames.Silence(Math.Max(nextSample, lastSilence));
                            frames.Silence(position);
                        }

                        if ((flags & 2) != 0)
                        {
                            for (int i = 0; i < count; i++)
                                frames.Push(0, position + i);
                        }
                        else
                        {
                            int size = checked(count * format.BlockSize);
                            if (buffer.Length < size)
                                buffer = new byte[size];
                            Marshal.Copy(data, buffer, 0, size);
                            for (int i = 0; i < count; i++)
                                frames.Push(format.Mono(buffer, i * format.BlockSize), position + i);
                        }
                        received = true;
                        nextSample = position + count;
                        peakLevel = Math.Max(peakLevel, decoder.Level);
                        if (decoder.Level >= 0.0003)
                            lastSound = timer.ElapsedMilliseconds;
                    }
                    finally
                    {
                        capture.ReleaseBuffer(count);
                    }
                }

                // In loopback an idle endpoint can stop delivering packets altogether.
                long idlePosition = ClockPosition(format.Rate) - format.Rate / 5;
                if (idlePosition > nextSample && idlePosition > lastSilence)
                {
                    frames.Silence(Math.Max(nextSample, lastSilence));
                    frames.Silence(idlePosition);
                    lastSilence = idlePosition;
                }

                long elapsed = timer.ElapsedMilliseconds;
                if (elapsed >= nextDeviceCheck)
                {
                    IDevice current = devices.GetDefaultAudioEndpoint(0, 1);
                    try
                    {
                        if (current.GetId() != id)
                            return;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(current);
                    }
                    nextDeviceCheck = elapsed + 500;
                }
                if (elapsed >= nextReport || completed != null)
                {
                    int detected = decoder.RecentTone(ClockPosition(format.Rate) / (double)format.Rate);
                    string state = decoder.Receiving ? $"Идет распознавание на частоте {decoder.DetectedToneHz} Гц..." :
                        detected != 0 ? $"Тон {detected} Гц слышен: ожидание начала сообщения..." :
                        elapsed - lastSound < 2500 ? "Звук поступает: тон Морзе пока не обнаружен." :
                        "На устройстве вывода нет слышимого звука";
                    report(new(state, name, peakLevel, decoder.PartialDigits, completed,
                        decoder.LastDigitReceivedAt));
                    completed = null;
                    peakLevel = 0;
                    nextReport = elapsed + 150;
                }
                stop.WaitHandle.WaitOne(10);
            }
        }
        finally
        {
            if (started)
                client!.Stop();
            if (capture != null)
                Marshal.ReleaseComObject(capture);
            if (client != null)
                Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
        }
    }

    private static long ClockPosition(int rate) =>
        (long)(Stopwatch.GetTimestamp() * (rate / (double)Stopwatch.Frequency));

    private static string DeviceName(IDevice device, string fallback)
    {
        try
        {
            var store = device.OpenPropertyStore(0);
            try
            {
                var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                store.GetValue(ref key, out var value);
                try
                {
                    return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? fallback : fallback;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (COMException)
        {
            return fallback;
        }
    }

    private readonly record struct MixFormat(int Rate, int Channels, int BlockSize, int Bytes, bool IsFloat)
    {
        public static MixFormat Read(IntPtr format)
        {
            int tag = (ushort)Marshal.ReadInt16(format);
            int channels = Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4);
            int block = Marshal.ReadInt16(format, 12);
            int bits = Marshal.ReadInt16(format, 14);
            if (tag == 0xFFFE && Marshal.ReadInt16(format, 16) >= 22)
            {
                var subtype = Marshal.PtrToStructure<Guid>(format + 24);
                tag = subtype == new Guid("00000003-0000-0010-8000-00AA00389B71") ? 3 :
                    subtype == new Guid("00000001-0000-0010-8000-00AA00389B71") ? 1 : 0;
            }
            if (rate < 8000 || channels < 1 || block != channels * (bits / 8) ||
                !(tag == 3 && bits == 32 || tag == 1 && bits is 8 or 16 or 24 or 32))
                throw new NotSupportedException("Формат звука выбранного устройства не поддерживается.");
            return new(rate, channels, block, bits / 8, tag == 3);
        }

        public float Mono(byte[] buffer, int offset)
        {
            float sum = 0;
            for (int ch = 0; ch < Channels; ch++, offset += Bytes)
            {
                sum += Bytes switch
                {
                    1 => (buffer[offset] - 128) / 128f,
                    2 => BitConverter.ToInt16(buffer, offset) / 32768f,
                    3 => ((sbyte)buffer[offset + 2] * 65536 + buffer[offset + 1] * 256 +
                        buffer[offset]) / 8388608f,
                    4 when IsFloat => BitConverter.ToSingle(buffer, offset),
                    _ => BitConverter.ToInt32(buffer, offset) / 2147483648f
                };
            }
            return sum / Channels;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints();
        IDevice GetDefaultAudioEndpoint(int flow, int role);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object Activate(ref Guid iid, int context, IntPtr activationParameters);
        IPropertyStore OpenPropertyStore(int access);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetId();
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount();
        void GetAt();
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        void GetBufferSize();
        void GetStreamLatency();
        void GetCurrentPadding();
        void IsFormatSupported();
        IntPtr GetMixFormat();
        void GetDevicePeriod();
        void Start();
        [PreserveSig] int Stop();
        void Reset();
        void SetEventHandle();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetService(ref Guid iid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureClient
    {
        void GetBuffer(out IntPtr data, out int frames, out uint flags, out ulong position, out ulong qpc);
        void ReleaseBuffer(int frames);
        int GetNextPacketSize();
    }
}

internal record AudioStatus(string State, string Device, double Level, string PartialDigits = "",
    Bunker? Completed = null, DateTimeOffset? LastDigitReceivedAt = null);
