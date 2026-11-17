using System.Collections.Concurrent;
using LobbyLens.Core;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace LobbyLens.App;

/// <summary>
/// Group sync over an MQTT broker (public by default, no own server needed).
/// Each member keeps one retained, encrypted message with its state at
/// lobbylens/v1/&lt;hash of code&gt;/&lt;memberId&gt;. The broker sees only ciphertext.
/// </summary>
public sealed class GroupClient : IAsyncDisposable
{
    readonly GroupKeys _keys;
    readonly string _memberId;
    readonly string _host;
    readonly int _port;
    readonly IMqttClient _client;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<string, MemberState> _members = new();
    MemberState? _last;
    Task? _loop;

    public event Action<string>? Status;
    public GroupKeys Keys => _keys;
    public bool Connected => _client.IsConnected;
    public IReadOnlyCollection<MemberState> Members => _members.Values.ToList();

    string MyTopic => _keys.TopicRoot + "/" + _memberId;

    public GroupClient(GroupKeys keys, string memberId, string host, int port)
    {
        _keys = keys;
        _memberId = memberId;
        _host = host;
        _port = port;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessage;
    }

    public void Start() => _loop ??= Task.Run(() => Loop(_cts.Token));

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    Status?.Invoke($"Подключение к {_host}:{_port}…");
                    var opts = new MqttClientOptionsBuilder()
                        .WithTcpServer(_host, _port)
                        .WithClientId("lobbylens-" + _memberId + "-" + Guid.NewGuid().ToString("N")[..6])
                        .WithCleanSession(true)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
                        .WithTimeout(TimeSpan.FromSeconds(10))
                        .WithWillTopic(MyTopic)
                        .WithWillPayload(Array.Empty<byte>())
                        .WithWillRetain(true)
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();
                    await _client.ConnectAsync(opts, ct);
                    var sub = new MqttFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(_keys.TopicRoot + "/+").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build();
                    await _client.SubscribeAsync(sub, ct);
                    Status?.Invoke("Группа: подключено");
                    if (_last is not null) await PublishCore(_last, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Status?.Invoke("Группа: нет связи (" + ex.Message + "), повтор через 5 с");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var slash = topic.LastIndexOf('/');
        if (slash < 0) return Task.CompletedTask;
        var id = topic[(slash + 1)..];
        var payload = e.ApplicationMessage.PayloadSegment;
        if (payload.Count == 0)
        {
            _members.TryRemove(id, out _);
            return Task.CompletedTask;
        }
        var plain = _keys.Decrypt(payload.AsSpan());
        if (plain is null) return Task.CompletedTask;
        var st = MemberState.FromBytes(plain);
        if (st is null || st.MemberId != id) return Task.CompletedTask;
        _members[id] = st;
        return Task.CompletedTask;
    }

    public async Task PublishAsync(MemberState state)
    {
        _last = state;
        _members[state.MemberId] = state;
        if (!_client.IsConnected) return;
        try { await PublishCore(state, _cts.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status?.Invoke("Группа: ошибка отправки (" + ex.Message + ")");
        }
    }

    Task PublishCore(MemberState state, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(MyTopic)
            .WithPayload(_keys.Encrypt(state.ToBytes()))
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        return _client.PublishAsync(msg, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_client.IsConnected)
            {
                // Remove our retained state so others don't see a ghost member.
                var clear = new MqttApplicationMessageBuilder().WithTopic(MyTopic).WithPayload(Array.Empty<byte>()).WithRetainFlag(true).Build();
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _client.PublishAsync(clear, t.Token);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), t.Token);
            }
        }
        catch (Exception) { /* best effort on exit */ }
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _client.Dispose();
        _cts.Dispose();
    }
}
