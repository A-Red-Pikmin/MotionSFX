using Google.FlatBuffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;

namespace MotionSFX.Core;

public readonly record struct SlimeVrTrackerFeedSnapshot(
    bool Connected,
    bool HasDeviceStatus,
    bool HasPhysicalHipTracker,
    DateTime LastDeviceUpdateUtc)
{
    public bool IsPhysicalHipAvailable(DateTime utcNow)
    {
        return Connected && HasDeviceStatus && HasPhysicalHipTracker &&
            LastDeviceUpdateUtc != DateTime.MinValue &&
            (utcNow - LastDeviceUpdateUtc).TotalSeconds <= 3.0;
    }
}

/// <summary>
/// Reads SlimeVR's local SolarXR data feed so MotionSFX can distinguish physical
/// hardware trackers from computed skeleton trackers present in VMC output.
/// The SlimeVR GUI uses this same WebSocket endpoint and FlatBuffer MessageBundle.
/// </summary>
public sealed class SlimeVrTrackerFeedClient : IDisposable
{
    private static readonly Uri Endpoint = new("ws://127.0.0.1:21110/");
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _worker;
    private ClientWebSocket _activeSocket;
    private SlimeVrTrackerFeedSnapshot _snapshot;
    private int _started;

    public SlimeVrTrackerFeedSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _worker = Task.Run(RunAsync);
    }

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { _activeSocket?.Abort(); } catch { }
        lock (_gate)
            _snapshot = new SlimeVrTrackerFeedSnapshot(false, false, false, DateTime.MinValue);
    }

    private async Task RunAsync()
    {
        CancellationToken token = _stop.Token;
        byte[] request;
        try { request = CreateStartDataFeedRequest(); }
        catch { return; }

        while (!token.IsCancellationRequested)
        {
            using ClientWebSocket socket = new();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            _activeSocket = socket;
            try
            {
                await socket.ConnectAsync(Endpoint, token).ConfigureAwait(false);
                lock (_gate)
                    _snapshot = new SlimeVrTrackerFeedSnapshot(true, false, false, DateTime.MinValue);

                await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Binary,
                    true, token).ConfigureAwait(false);

                byte[] receiveBuffer = new byte[32 * 1024];
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using MemoryStream message = new();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token)
                            .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType != WebSocketMessageType.Binary)
                        {
                            if (result.EndOfMessage) break;
                            continue;
                        }
                        message.Write(receiveBuffer, 0, result.Count);
                        if (message.Length > 1024 * 1024)
                            throw new InvalidDataException("SlimeVR data-feed message exceeded 1 MiB.");
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Binary || message.Length == 0) continue;

                    if (TryReadPhysicalHipAvailability(message.GetBuffer(), (int)message.Length,
                        out bool physicalHipAvailable))
                    {
                        lock (_gate)
                            _snapshot = new SlimeVrTrackerFeedSnapshot(true, true,
                                physicalHipAvailable, DateTime.UtcNow);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // SlimeVR may start after MotionSFX. Reconnect quietly; the Hip preset
                // remains disabled until a fresh physical tracker record is received.
            }
            finally
            {
                lock (_gate)
                    _snapshot = new SlimeVrTrackerFeedSnapshot(false, false, false, DateTime.MinValue);
                if (ReferenceEquals(_activeSocket, socket)) _activeSocket = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static byte[] CreateStartDataFeedRequest()
    {
        // SolarXR schema: TrackerDataMask(info,status) -> DeviceDataMask(tracker_data,
        // device_data) -> DataFeedConfig(data_mask) -> StartDataFeed -> MessageBundle.
        FlatBufferBuilder builder = new(256);

        builder.StartTable(13); // TrackerDataMask
        builder.AddBool(0, true, false); // info
        builder.AddBool(1, true, false); // status
        // Request a small, changing field so the server refreshes the feed while
        // devices are connected; otherwise an info/status-only feed may go quiet
        // and the physical tracker confirmation would expire despite a live device.
        builder.AddBool(2, true, false); // rotation
        int trackerDataMask = builder.EndTable();

        builder.StartTable(2); // DeviceDataMask
        builder.AddOffset(0, trackerDataMask, 0); // tracker_data
        builder.AddBool(1, true, false); // device_data
        int deviceDataMask = builder.EndTable();

        builder.StartTable(6); // DataFeedConfig
        builder.AddShort(0, 250, 0); // minimum_time_since_last (ms)
        builder.AddOffset(1, deviceDataMask, 0); // data_mask
        int dataFeedConfig = builder.EndTable();

        builder.StartVector(sizeof(int), 1, sizeof(int));
        builder.AddOffset(dataFeedConfig);
        VectorOffset dataFeedConfigs = builder.EndVector();

        builder.StartTable(1); // StartDataFeed
        builder.AddOffset(0, dataFeedConfigs.Value, 0);
        int startDataFeed = builder.EndTable();

        builder.StartTable(2); // DataFeedMessageHeader: union type + payload
        builder.AddByte(0, 2, 0); // DataFeedMessage.StartDataFeed = 2
        builder.AddOffset(1, startDataFeed, 0);
        int dataFeedHeader = builder.EndTable();

        builder.StartVector(sizeof(int), 1, sizeof(int));
        builder.AddOffset(dataFeedHeader);
        VectorOffset dataFeedMessages = builder.EndVector();

        builder.StartTable(3); // MessageBundle
        builder.AddOffset(0, dataFeedMessages.Value, 0); // data_feed_msgs
        int messageBundle = builder.EndTable();

        builder.Finish(messageBundle);
        return builder.SizedByteArray();
    }

    private static bool TryReadPhysicalHipAvailability(byte[] payload, int length, out bool available)
    {
        available = false;
        if (payload == null || length < 0 || length > payload.Length) return false;
        ReadOnlySpan<byte> data = payload.AsSpan(0, length);
        try
        {
            if (data.Length < 8) return false;
            int root = ReadInt32(data, 0);
            if (!IsValidTable(data, root)) return false;
            int messagesField = GetFieldPosition(data, root, 0);
            if (messagesField == 0) return false;
            int messages = GetOffsetTarget(data, messagesField);
            if (!IsValidVector(data, messages)) return false;

            bool sawDeviceUpdate = false;
            int messageCount = ReadInt32(data, messages);
            for (int messageIndex = 0; messageIndex < messageCount; messageIndex++)
            {
                int headerElement = checked(messages + 4 + messageIndex * 4);
                int header = GetOffsetTarget(data, headerElement);
                if (!IsValidTable(data, header)) continue;
                int messageTypeField = GetFieldPosition(data, header, 0);
                int payloadField = GetFieldPosition(data, header, 1);
                if (messageTypeField == 0 || payloadField == 0 || data[messageTypeField] != 3) continue;
                int update = GetOffsetTarget(data, payloadField); // DataFeedUpdate = 3
                if (!IsValidTable(data, update)) continue;

                int devicesField = GetFieldPosition(data, update, 0);
                if (devicesField == 0) continue;
                int devices = GetOffsetTarget(data, devicesField);
                if (!IsValidVector(data, devices)) continue;
                sawDeviceUpdate = true;
                int deviceCount = ReadInt32(data, devices);
                for (int deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    int deviceElement = checked(devices + 4 + deviceIndex * 4);
                    int device = GetOffsetTarget(data, deviceElement);
                    if (!IsValidTable(data, device)) continue;
                    int trackersField = GetFieldPosition(data, device, 4);
                    if (trackersField == 0) continue;
                    int trackers = GetOffsetTarget(data, trackersField);
                    if (!IsValidVector(data, trackers)) continue;
                    int trackerCount = ReadInt32(data, trackers);
                    for (int trackerIndex = 0; trackerIndex < trackerCount; trackerIndex++)
                    {
                        int trackerElement = checked(trackers + 4 + trackerIndex * 4);
                        int tracker = GetOffsetTarget(data, trackerElement);
                        if (!IsValidTable(data, tracker)) continue;
                        int infoField = GetFieldPosition(data, tracker, 1);
                        int statusField = GetFieldPosition(data, tracker, 2);
                        if (infoField == 0 || statusField == 0) continue;
                        int info = GetOffsetTarget(data, infoField);
                        if (!IsValidTable(data, info)) continue;
                        int bodyPartField = GetFieldPosition(data, info, 1);
                        int computedField = GetFieldPosition(data, info, 5);
                        if (bodyPartField == 0) continue;

                        byte bodyPart = data[bodyPartField];
                        // FlatBuffers omits scalar fields equal to their default. The
                        // default for is_computed is false, so an absent field denotes
                        // a physical (non-computed) tracker rather than unknown data.
                        bool isComputed = computedField != 0 && data[computedField] != 0;
                        byte status = data[statusField];
                        if (PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(
                            isComputed, bodyPart, status))
                        {
                            available = true;
                            break;
                        }
                    }
                    if (available) break;
                }
            }
            return sawDeviceUpdate;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is OverflowException ||
            ex is IndexOutOfRangeException || ex is InvalidDataException)
        {
            available = false;
            return false;
        }
    }

    private static bool IsValidTable(ReadOnlySpan<byte> data, int table)
    {
        return table >= 4 && table <= data.Length - 4;
    }

    private static bool IsValidVector(ReadOnlySpan<byte> data, int vector)
    {
        if (vector < 4 || vector > data.Length - 4) return false;
        int count = ReadInt32(data, vector);
        return count >= 0 && count <= (data.Length - vector - 4) / 4;
    }

    private static int GetFieldPosition(ReadOnlySpan<byte> data, int table, int fieldIndex)
    {
        if (!IsValidTable(data, table) || fieldIndex < 0) return 0;
        int vtable = checked(table - ReadInt32(data, table));
        if (vtable < 0 || vtable > data.Length - 4) return 0;
        int vtableLength = ReadUInt16(data, vtable);
        int entry = checked(4 + fieldIndex * 2);
        if (entry + 2 > vtableLength || vtable + entry + 2 > data.Length) return 0;
        int fieldOffset = ReadUInt16(data, vtable + entry);
        if (fieldOffset == 0) return 0;
        int position = checked(table + fieldOffset);
        return position >= 0 && position < data.Length ? position : 0;
    }

    private static int GetOffsetTarget(ReadOnlySpan<byte> data, int offsetPosition)
    {
        if (offsetPosition < 0 || offsetPosition > data.Length - 4)
            throw new InvalidDataException("Invalid FlatBuffer offset position.");
        return checked(offsetPosition + ReadInt32(data, offsetPosition));
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int position)
    {
        if (position < 0 || position > data.Length - 4)
            throw new InvalidDataException("FlatBuffer int32 is out of range.");
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position, 4));
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, int position)
    {
        if (position < 0 || position > data.Length - 2)
            throw new InvalidDataException("FlatBuffer uint16 is out of range.");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position, 2));
    }
}
