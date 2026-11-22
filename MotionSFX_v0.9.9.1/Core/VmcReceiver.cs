namespace MotionSFX.Core
{
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class MotionSfxVmcSample
{
    public readonly string Id;
    public readonly string VmcLabel;
    public readonly string Kind;
    public readonly string SourceAddress;
    public readonly bool IsLocal;
    public readonly double X, Y, Z, QX, QY, QZ, QW;
    public readonly DateTime ReceivedUtc;
    public readonly string SenderEndpoint;
    public readonly long PacketCount;

    public MotionSfxVmcSample(string id, string vmcLabel, string kind, string sourceAddress,
        bool isLocal, double x, double y, double z, double qx, double qy, double qz, double qw,
        DateTime receivedUtc, string senderEndpoint, long packetCount)
    {
        Id = id; VmcLabel = vmcLabel; Kind = kind; SourceAddress = sourceAddress;
        IsLocal = isLocal; X = x; Y = y; Z = z; QX = qx; QY = qy; QZ = qz; QW = qw;
        ReceivedUtc = receivedUtc; SenderEndpoint = senderEndpoint; PacketCount = packetCount;
    }
}

public sealed class MotionSfxVmcReceiver : IDisposable
{
    private sealed class Counter { public long Value; }

    // SlimeVR emits every Unity/HumanBodyBones transform, including fingers.
    // MotionSFX only needs the small set used by its seven movement presets.
    // Discarding all other bones follows the VMC spec's guidance to ignore
    // messages the receiver does not need and keeps packet work lightweight.
    private static readonly HashSet<string> RelevantBoneNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "LeftFoot", "RightFoot", "LeftLowerLeg", "RightLowerLeg", "LeftUpperLeg", "RightUpperLeg",
        "Head", "Neck", "LeftHand", "RightHand", "LeftLowerArm", "RightLowerArm",
        "LeftUpperArm", "RightUpperArm", "LeftShoulder", "RightShoulder"
    };

    private readonly UdpClient _client;
    private readonly ConcurrentDictionary<string, MotionSfxVmcSample> _latest;
    private readonly ConcurrentDictionary<string, Counter> _deviceCounts;
    private readonly ConcurrentQueue<MotionSfxVmcSample> _motionQueue;
    private const int MaxMotionQueueSamples = 8192;

    // Movement detection does not need every packet. Each transform is limited to about
    // 30 queued updates per second, and the duplicate "local" copy of a transform is ignored
    // while its "global" copy is arriving (the app already ignores it in that case).
    private const long MinMotionIntervalTicks = 330000L;   // 33 ms
    private const long GlobalFreshTicks = 15000000L;       // 1.5 s
    private readonly ConcurrentDictionary<string, long> _lastQueuedTicks;
    private readonly ConcurrentDictionary<string, long> _lastGlobalTicks;
    private readonly object _errorLock = new object();
    private Thread _thread;
    private int _stopRequested;
    private int _started;
    private long _packetCount;
    private long _transformCount;
    private long _malformedCount;
    private long _motionQueueDroppedCount;
    private int _motionQueueCount;
    private string _lastError = String.Empty;

    public long PacketCount { get { return Interlocked.Read(ref _packetCount); } }
    public long TransformCount { get { return Interlocked.Read(ref _transformCount); } }
    public long MalformedCount { get { return Interlocked.Read(ref _malformedCount); } }
    public long MotionQueueDroppedCount { get { return Interlocked.Read(ref _motionQueueDroppedCount); } }
    public string LastError { get { lock (_errorLock) { return _lastError; } } }
    public bool IsRunning
    {
        get
        {
            Thread t = _thread;
            return t != null && t.IsAlive && Volatile.Read(ref _stopRequested) == 0;
        }
    }

    public MotionSfxVmcReceiver(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
        _latest = new ConcurrentDictionary<string, MotionSfxVmcSample>(StringComparer.Ordinal);
        _deviceCounts = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        _motionQueue = new ConcurrentQueue<MotionSfxVmcSample>();
        _lastQueuedTicks = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        _lastGlobalTicks = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _client.Client.ReceiveTimeout = 500;
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("This VMC receiver instance has already been started.");
        _thread = new Thread(ReceiveLoop);
        _thread.IsBackground = true;
        _thread.Priority = ThreadPriority.BelowNormal;
        _thread.Name = "MotionSFX VMC UDP Receiver";
        _thread.Start();
    }

    public MotionSfxVmcSample[] DrainLatest()
    {
        List<MotionSfxVmcSample> result = new List<MotionSfxVmcSample>();
        foreach (KeyValuePair<string, MotionSfxVmcSample> pair in _latest)
        {
            MotionSfxVmcSample sample;
            if (_latest.TryRemove(pair.Key, out sample)) result.Add(sample);
        }
        return result.ToArray();
    }

    // Movement recognition needs the incoming position history, not only the
    // latest coalesced table value. Bound the queue so a stalled UI cannot
    // consume unbounded memory; dropping the oldest data keeps detection live.
    public MotionSfxVmcSample[] DrainMotionSamples()
    {
        List<MotionSfxVmcSample> result = new List<MotionSfxVmcSample>();
        MotionSfxVmcSample sample;
        while (_motionQueue.TryDequeue(out sample))
        {
            Interlocked.Decrement(ref _motionQueueCount);
            result.Add(sample);
        }
        return result.ToArray();
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        try { _client.Close(); } catch { }
        Thread t = _thread;
        if (t != null && t.IsAlive && Thread.CurrentThread != t) t.Join(1000);
    }

    public void Dispose()
    {
        Stop();
        try { _client.Dispose(); } catch { }
    }

    private void SetError(string message)
    {
        lock (_errorLock) { _lastError = message ?? "Unknown receiver error."; }
    }

    private void ReceiveLoop()
    {
        // Reuse the endpoint object rather than allocating one for every UDP datagram.
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (Volatile.Read(ref _stopRequested) == 0)
        {
            try
            {
                byte[] data = _client.Receive(ref remote);
                if (data == null || data.Length == 0) continue;
                Interlocked.Increment(ref _packetCount);
                try
                {
                    List<MotionSfxVmcSample> samples = ParseDatagram(data, DateTime.UtcNow,
                        remote.Address.ToString() + ":" + remote.Port);
                    for (int i = 0; i < samples.Count; i++)
                    {
                        MotionSfxVmcSample sample = samples[i];
                        Counter counter = _deviceCounts.GetOrAdd(sample.Id, delegate(string key) { return new Counter(); });
                        long count = Interlocked.Increment(ref counter.Value);
                        MotionSfxVmcSample counted = new MotionSfxVmcSample(sample.Id, sample.VmcLabel,
                            sample.Kind, sample.SourceAddress, sample.IsLocal, sample.X, sample.Y, sample.Z,
                            sample.QX, sample.QY, sample.QZ, sample.QW, sample.ReceivedUtc,
                            sample.SenderEndpoint, count);
                        // The protocol name identifies a device transform. Address
                        // category (Hmd/Con/Tra) and coordinate space (Pos/Local)
                        // describe that transform; neither creates a new identity.
                        // Keep category/space variants long enough for the UI to
                        // merge their metadata into one identity row.
                        string coordinateSpace = sample.IsLocal ? "LOCAL" : "GLOBAL";
                        _latest[sample.Id + "|" + sample.Kind + "|" + coordinateSpace] = counted;
                        if (sample.Kind != "BONE" && ShouldQueueForMotion(counted, coordinateSpace))
                        {
                            _motionQueue.Enqueue(counted);
                            int queueCount = Interlocked.Increment(ref _motionQueueCount);
                            while (queueCount > MaxMotionQueueSamples)
                            {
                                MotionSfxVmcSample dropped;
                                if (!_motionQueue.TryDequeue(out dropped)) break;
                                Interlocked.Decrement(ref _motionQueueCount);
                                Interlocked.Increment(ref _motionQueueDroppedCount);
                                queueCount = Volatile.Read(ref _motionQueueCount);
                            }
                        }
                        Interlocked.Increment(ref _transformCount);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _malformedCount);
                }
            }
            catch (SocketException ex)
            {
                if (Volatile.Read(ref _stopRequested) != 0) break;
                if (ex.SocketErrorCode == SocketError.TimedOut || ex.ErrorCode == 10060) continue;
                SetError(ex.Message);
                Interlocked.Exchange(ref _stopRequested, 1);
                break;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _stopRequested) == 0) SetError(ex.Message);
                Interlocked.Exchange(ref _stopRequested, 1);
                break;
            }
        }
    }

    private bool ShouldQueueForMotion(MotionSfxVmcSample sample, string coordinateSpace)
    {
        long nowTicks = sample.ReceivedUtc.Ticks;
        if (!sample.IsLocal)
        {
            _lastGlobalTicks[sample.Id] = nowTicks;
        }
        else
        {
            long lastGlobal;
            if (_lastGlobalTicks.TryGetValue(sample.Id, out lastGlobal) && nowTicks - lastGlobal < GlobalFreshTicks)
            {
                return false;
            }
        }
        string key = sample.Id + "|" + coordinateSpace;
        long lastQueued;
        if (_lastQueuedTicks.TryGetValue(key, out lastQueued) && nowTicks - lastQueued < MinMotionIntervalTicks)
        {
            return false;
        }
        _lastQueuedTicks[key] = nowTicks;
        return true;
    }

    private static List<MotionSfxVmcSample> ParseDatagram(byte[] data, DateTime receivedUtc, string sender)
    {
        if (data == null || data.Length < 4 || data.Length > 1048576)
            throw new FormatException("OSC packet size is invalid.");
        List<MotionSfxVmcSample> samples = new List<MotionSfxVmcSample>();
        ParsePacketInto(data, 0, data.Length, 0, receivedUtc, sender, samples);
        return samples;
    }

    private static void ParsePacketInto(byte[] data, int start, int length, int depth,
        DateTime receivedUtc, string sender, List<MotionSfxVmcSample> samples)
    {
        if (depth > 8 || length < 4 || start < 0 || start + length > data.Length)
            throw new FormatException("OSC packet bounds or bundle nesting are invalid.");
        bool bundle = length >= 16 && data[start] == (byte)'#' &&
            data[start + 1] == (byte)'b' && data[start + 2] == (byte)'u' &&
            data[start + 3] == (byte)'n' && data[start + 4] == (byte)'d' &&
            data[start + 5] == (byte)'l' && data[start + 6] == (byte)'e' && data[start + 7] == 0;
        if (bundle)
        {
            int offset = start + 16;
            int end = start + length;
            while (offset < end)
            {
                int elementLength = ReadInt32(data, ref offset, end);
                if (elementLength <= 0 || offset + elementLength > end)
                    throw new FormatException("OSC bundle element length is invalid.");
                ParsePacketInto(data, offset, elementLength, depth + 1, receivedUtc, sender, samples);
                offset += elementLength;
            }
        }
        else
        {
            ParseDeviceMessage(data, start, length, receivedUtc, sender, samples);
        }
    }

    private static void ParseDeviceMessage(byte[] data, int start, int length,
        DateTime receivedUtc, string sender, List<MotionSfxVmcSample> samples)
    {
        int offset = start;
        int end = start + length;
        string address = ReadOscString(data, ref offset, end);
        string kind;
        bool local;
        if (address == "/VMC/Ext/Bone/Pos") { kind = "BONE"; local = false; }
        else if (address == "/VMC/Ext/Hmd/Pos") { kind = "HMD"; local = false; }
        else if (address == "/VMC/Ext/Con/Pos") { kind = "CON"; local = false; }
        else if (address == "/VMC/Ext/Tra/Pos") { kind = "TRA"; local = false; }
        else if (address == "/VMC/Ext/Hmd/Pos/Local") { kind = "HMD"; local = true; }
        else if (address == "/VMC/Ext/Con/Pos/Local") { kind = "CON"; local = true; }
        else if (address == "/VMC/Ext/Tra/Pos/Local") { kind = "TRA"; local = true; }
        else return; // Ignore non-target VMC messages without decoding their arguments.

        string tags = ReadOscString(data, ref offset, end);
        if (tags != ",sfffffff") return;
        string vmcLabel = ReadOscString(data, ref offset, end);
        if (String.IsNullOrWhiteSpace(vmcLabel)) return;
        if (kind == "BONE" && !RelevantBoneNames.Contains(vmcLabel)) return;
        // Read directly into locals: allocating a seven-float array for every
        // tracker transform creates avoidable GC pressure at high VMC rates.
        float x = ReadFloat32(data, ref offset, end);
        float y = ReadFloat32(data, ref offset, end);
        float z = ReadFloat32(data, ref offset, end);
        float qx = ReadFloat32(data, ref offset, end);
        float qy = ReadFloat32(data, ref offset, end);
        float qz = ReadFloat32(data, ref offset, end);
        float qw = ReadFloat32(data, ref offset, end);
        if (offset != end) throw new FormatException("VMC transform contains unexpected trailing bytes.");
        if (Single.IsNaN(x) || Single.IsInfinity(x) || Single.IsNaN(y) || Single.IsInfinity(y) ||
            Single.IsNaN(z) || Single.IsInfinity(z) || Single.IsNaN(qx) || Single.IsInfinity(qx) ||
            Single.IsNaN(qy) || Single.IsInfinity(qy) || Single.IsNaN(qz) || Single.IsInfinity(qz) ||
            Single.IsNaN(qw) || Single.IsInfinity(qw)) return;

        // The first VMC string is the raw protocol identity (serial). SlimeVR
        // currently writes tracker.name there. Do not use Hmd/Con/Tra as part
        // of the identity: those are address categories for the same output.
        // Bone names live in a separate namespace and are never tracker rows.
        string id = (kind == "BONE" ? "BONE|" : "VMC|") + vmcLabel;
        samples.Add(new MotionSfxVmcSample(id, vmcLabel, kind, address, local,
            x, y, z, qx, qy, qz, qw, receivedUtc, sender, 0));
    }

    private static string ReadOscString(byte[] data, ref int offset, int end)
    {
        if (offset >= end) throw new FormatException("OSC string starts beyond packet boundary.");
        int zero = -1;
        for (int i = offset; i < end; i++) { if (data[i] == 0) { zero = i; break; } }
        if (zero < 0) throw new FormatException("OSC string is missing its null terminator.");
        string value = Encoding.UTF8.GetString(data, offset, zero - offset);
        int next = zero + 1;
        next += (4 - (next % 4)) % 4;
        if (next > end) throw new FormatException("OSC string padding exceeds packet boundary.");
        offset = next;
        return value;
    }

    private static int ReadInt32(byte[] data, ref int offset, int end)
    {
        if (offset + 4 > end) throw new FormatException("OSC int32 is truncated.");
        int value = (data[offset] << 24) | (data[offset + 1] << 16) |
            (data[offset + 2] << 8) | data[offset + 3];
        offset += 4;
        return value;
    }

    private static float ReadFloat32(byte[] data, ref int offset, int end)
    {
        int bits = ReadInt32(data, ref offset, end);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void AddOscString(List<byte> target, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        target.AddRange(bytes);
        target.Add(0);
        while ((target.Count % 4) != 0) target.Add(0);
    }

    private static void AddNetworkInt32(List<byte> target, int value)
    {
        target.Add((byte)((value >> 24) & 255));
        target.Add((byte)((value >> 16) & 255));
        target.Add((byte)((value >> 8) & 255));
        target.Add((byte)(value & 255));
    }

    private static void AddFloat(List<byte> target, float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        target.AddRange(bytes);
    }

    private static byte[] MakeTestMessage(string address, string name, float x, float y, float z)
    {
        List<byte> message = new List<byte>();
        AddOscString(message, address);
        AddOscString(message, ",sfffffff");
        AddOscString(message, name);
        AddFloat(message, x); AddFloat(message, y); AddFloat(message, z);
        AddFloat(message, 0); AddFloat(message, 0); AddFloat(message, 0); AddFloat(message, 1);
        return message.ToArray();
    }

    public static bool RunSelfTest()
    {
        List<byte[]> messages = new List<byte[]>();
        messages.Add(MakeTestMessage("/VMC/Ext/Hmd/Pos", "Head", 1.25f, 2.5f, -3.75f));
        messages.Add(MakeTestMessage("/VMC/Ext/Con/Pos", "Left Hand", 4f, 5f, 6f));
        messages.Add(MakeTestMessage("/VMC/Ext/Tra/Pos", "Left Foot", 7f, 8f, 9f));
        messages.Add(MakeTestMessage("/VMC/Ext/Tra/Pos", "Right Foot", 10f, 11f, 12f));
        messages.Add(MakeTestMessage("/VMC/Ext/Tra/Pos/Local", "Left Foot", 0.1f, 0.2f, 0.3f));
        // Same raw identity through another category must remain one output.
        messages.Add(MakeTestMessage("/VMC/Ext/Con/Pos", "Left Foot", 0.4f, 0.5f, 0.6f));
        messages.Add(MakeTestMessage("/VMC/Ext/Bone/Pos", "LeftFoot", 1f, 2f, 3f));
        messages.Add(MakeTestMessage("/VMC/Ext/Bone/Pos", "RightFoot", 1f, 2f, 3f));
        messages.Add(MakeTestMessage("/VMC/Ext/Bone/Pos", "Head", 1f, 2f, 3f));
        messages.Add(MakeTestMessage("/VMC/Ext/Bone/Pos", "LeftHand", 1f, 2f, 3f));
        messages.Add(MakeTestMessage("/VMC/Ext/Bone/Pos", "LeftIndexProximal", 1f, 2f, 3f)); // must be filtered

        List<byte> bundle = new List<byte>();
        bundle.AddRange(Encoding.ASCII.GetBytes("#bundle\0"));
        for (int i = 0; i < 7; i++) bundle.Add(0);
        bundle.Add(1); // immediate timetag
        for (int i = 0; i < messages.Count; i++)
        {
            AddNetworkInt32(bundle, messages[i].Length);
            bundle.AddRange(messages[i]);
        }
        List<MotionSfxVmcSample> decoded = ParseDatagram(bundle.ToArray(), DateTime.UtcNow, "127.0.0.1:1");
        if (decoded.Count != 10) return false;
        bool sawHead = false, sawController = false, sawLeftFoot = false, sawRightFoot = false, sawLocal = false;
        bool sawBoneLeftFoot = false, sawBoneRightFoot = false, sawBoneHead = false, sawBoneLeftHand = false;
        HashSet<string> trackerIdentities = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> leftFootIdentityVariants = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < decoded.Count; i++)
        {
            MotionSfxVmcSample sample = decoded[i];
            if (sample.Kind != "BONE") trackerIdentities.Add(sample.Id);
            if (sample.VmcLabel == "Left Foot") leftFootIdentityVariants.Add(sample.Id);
            if (sample.Kind == "HMD" && sample.VmcLabel == "Head")
                sawHead = Math.Abs(sample.X - 1.25) < 0.0001 && Math.Abs(sample.Y - 2.5) < 0.0001 && Math.Abs(sample.Z + 3.75) < 0.0001;
            if (sample.Kind == "CON" && sample.VmcLabel == "Left Hand") sawController = true;
            if (sample.Kind == "TRA" && sample.VmcLabel == "Left Foot" && !sample.IsLocal) sawLeftFoot = true;
            if (sample.Kind == "TRA" && sample.VmcLabel == "Right Foot") sawRightFoot = true;
            if (sample.Kind == "TRA" && sample.VmcLabel == "Left Foot" && sample.IsLocal) sawLocal = true;
            if (sample.Kind == "BONE" && sample.VmcLabel == "LeftFoot") sawBoneLeftFoot = true;
            if (sample.Kind == "BONE" && sample.VmcLabel == "RightFoot") sawBoneRightFoot = true;
            if (sample.Kind == "BONE" && sample.VmcLabel == "Head") sawBoneHead = true;
            if (sample.Kind == "BONE" && sample.VmcLabel == "LeftHand") sawBoneLeftHand = true;
        }
        return trackerIdentities.Count == 4 && leftFootIdentityVariants.Count == 1 &&
            sawHead && sawController && sawLeftFoot && sawRightFoot && sawLocal &&
            sawBoneLeftFoot && sawBoneRightFoot && sawBoneHead && sawBoneLeftHand;
    }
}

}
