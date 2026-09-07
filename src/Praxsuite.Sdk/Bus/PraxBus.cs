using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// A single bus - one topic, one instance. This is the object you actually work with.
    ///
    /// Handlers registered before <see cref="JoinAsync"/> are kept, so a channel can be wired up
    /// once at start-up and joined later, and they survive a reconnect: the bus re-joins on your
    /// behalf and the same handlers keep firing.
    /// </summary>
    public sealed class PraxChannel
    {
        private readonly PraxBus _bus;
        private readonly Dictionary<string, List<Action<PraxBusEvent>>> _handlers =
            new Dictionary<string, List<Action<PraxBusEvent>>>();
        private readonly List<Action<PraxBusEvent>> _any = new List<Action<PraxBusEvent>>();
        private readonly List<Action<string>> _joined = new List<Action<string>>();
        private readonly List<Action<string>> _left = new List<Action<string>>();
        private readonly List<Action> _evicted = new List<Action>();
        private List<PraxBusPeerState> _peers = new List<PraxBusPeerState>();

        internal string Ticket;
        /// <summary>Whether the app wants to be here. Drives the re-join after a reconnect.</summary>
        internal bool Wanted;
        internal bool Joined;

        internal PraxChannel(PraxBus bus, string key)
        {
            _bus = bus;
            Key = key;
        }

        /// <summary>The normalised key, e.g. <c>office:hq</c>.</summary>
        public string Key { get; }

        /// <summary>The topic segment - everything before the first colon.</summary>
        public string Topic
        {
            get
            {
                var i = Key.IndexOf(':');
                return i < 0 ? Key : Key.Substring(0, i);
            }
        }

        /// <summary>The instance segment - everything after the first colon.</summary>
        public string Instance
        {
            get
            {
                var i = Key.IndexOf(':');
                return i < 0 ? string.Empty : Key.Substring(i + 1);
            }
        }

        /// <summary>
        /// Every peer's last retained message, as of the most recent join.
        ///
        /// This is what stops a late joiner staring at an empty room until somebody moves. It is a
        /// snapshot, not a live view - what follows arrives through <see cref="On"/>.
        /// </summary>
        public IReadOnlyList<PraxBusPeerState> Peers
        {
            get { return _peers; }
        }

        /// <summary>
        /// Joins the bus, returning the peers already present.
        ///
        /// A refused join THROWS. That is deliberately louder than a refused publish: a publish
        /// that does not land is one dropped frame, whereas a join that does not land leaves this
        /// client silently absent for the whole session.
        ///
        /// <paramref name="ticket"/> is only consulted for topics whose access mode is Ticket, and
        /// is remembered so a reconnect can re-join with it. Ticket topics are not usable yet -
        /// nothing in the platform mints a ticket - so leave it null unless told otherwise.
        /// </summary>
        public async Task<IReadOnlyList<PraxBusPeerState>> JoinAsync(
            string ticket = null, CancellationToken ct = default(CancellationToken))
        {
            Wanted = true;
            if (ticket != null) Ticket = ticket;

            var result = await _bus.InvokeAsync(
                "JoinBus", new List<object> { Key, Ticket }, ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                Wanted = false;
                throw new PraxException(
                    result.IsTransportError
                        ? "BUS_CALL_FAILED"
                        : "BUS_" + (result.Error ?? "denied").ToUpperInvariant(),
                    "Could not join \"" + Key + "\": " + PraxBusWire.DescribeError(result.Error));
            }

            Joined = true;
            _peers = result.Peers;
            return result.Peers;
        }

        /// <summary>
        /// Sends an event to every OTHER peer in the bus.
        ///
        /// Does NOT throw when the bus refuses the frame - dropping an ephemeral message is
        /// ordinary operation, and a game loop that throws on a rate limit is worse than one that
        /// skips a frame. Read the result when you care:
        ///
        /// <code>
        /// var r = await room.PublishAsync("move", new { x, y });
        /// if (!r.Ok) Debug.Log(r.Error);          // e.g. "rate_limited"
        /// if (r.Recipients == 0) { }              // it went out, and nobody was joined
        /// </code>
        ///
        /// You will not receive your own event back. Apply your own change locally.
        /// </summary>
        public Task<PraxBusResult> PublishAsync(
            string eventName, object payload = null, CancellationToken ct = default(CancellationToken))
        {
            return _bus.InvokeAsync(
                "Publish",
                new List<object> { Key, eventName, payload ?? new Dictionary<string, object>() },
                ct);
        }

        /// <summary>Leaves the bus. Idempotent, and it stops the reconnect logic re-joining.</summary>
        public async Task LeaveAsync(CancellationToken ct = default(CancellationToken))
        {
            Wanted = false;
            Joined = false;
            _peers = new List<PraxBusPeerState>();

            if (_bus.State == PraxBusState.Connected)
                await _bus.InvokeAsync("LeaveBus", new List<object> { Key }, ct).ConfigureAwait(false);
        }

        /// <summary>Subscribes to one event name. The returned action unsubscribes.</summary>
        public Action On(string eventName, Action<PraxBusEvent> handler)
        {
            List<Action<PraxBusEvent>> bucket;
            lock (_handlers)
            {
                if (!_handlers.TryGetValue(eventName, out bucket))
                    _handlers[eventName] = bucket = new List<Action<PraxBusEvent>>();
                bucket.Add(handler);
            }
            return delegate { lock (_handlers) { bucket.Remove(handler); } };
        }

        /// <summary>Subscribes to every event on this bus, whatever its name.</summary>
        public Action OnAny(Action<PraxBusEvent> handler)
        {
            lock (_any) _any.Add(handler);
            return delegate { lock (_any) { _any.Remove(handler); } };
        }

        /// <summary>Fires only when the topic has presence enabled.</summary>
        public Action OnPeerJoined(Action<string> handler)
        {
            lock (_joined) _joined.Add(handler);
            return delegate { lock (_joined) { _joined.Remove(handler); } };
        }

        public Action OnPeerLeft(Action<string> handler)
        {
            lock (_left) _left.Add(handler);
            return delegate { lock (_left) { _left.Remove(handler); } };
        }

        /// <summary>
        /// The server removed this connection from the bus, because the topic was disabled or
        /// re-scoped while the socket was open. The SDK does not re-join: that would be arguing
        /// with a decision the server has just made.
        /// </summary>
        public Action OnEvicted(Action handler)
        {
            lock (_evicted) _evicted.Add(handler);
            return delegate { lock (_evicted) { _evicted.Remove(handler); } };
        }

        internal void Dispatch(PraxBusEvent busEvent)
        {
            var targets = new List<Action<PraxBusEvent>>();
            lock (_handlers)
            {
                List<Action<PraxBusEvent>> bucket;
                if (_handlers.TryGetValue(busEvent.Event, out bucket)) targets.AddRange(bucket);
            }
            lock (_any) targets.AddRange(_any);

            foreach (var handler in targets) Safely(handler, busEvent);
        }

        internal void DispatchPeer(bool joined, string userId)
        {
            var targets = new List<Action<string>>();
            var source = joined ? _joined : _left;
            lock (source) targets.AddRange(source);

            foreach (var handler in targets) Safely(handler, userId);
        }

        internal void DispatchEvicted()
        {
            Wanted = false;
            Joined = false;

            var targets = new List<Action>();
            lock (_evicted) targets.AddRange(_evicted);

            foreach (var handler in targets)
            {
                try { handler(); }
                catch (Exception ex) { PraxLog.Error("An Event Bus handler threw: " + ex.Message); }
            }
        }

        private static void Safely<T>(Action<T> handler, T value)
        {
            try { handler(value); }
            catch (Exception ex) { PraxLog.Error("An Event Bus handler threw: " + ex.Message); }
        }
    }

    /// <summary>
    /// A topic - a namespace of buses. <c>bus.Topic("office").Channel("hq")</c> is
    /// <c>office:hq</c>.
    /// </summary>
    public sealed class PraxTopic
    {
        private readonly PraxBus _bus;

        internal PraxTopic(PraxBus bus, string key)
        {
            _bus = bus;
            Key = key;
        }

        public string Key { get; }

        /// <summary>The bus for one instance of this topic.</summary>
        public PraxChannel Channel(string instance)
        {
            return _bus.Channel(Key + ":" + instance);
        }
    }

    /// <summary>Where the connection is. Reconnecting is normal and resolves itself.</summary>
    public enum PraxBusState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    /// <summary>
    /// The Prax Event Bus - ephemeral realtime between connected clients.
    ///
    /// Live cursors, avatars, "user is typing", a multiplayer lobby: state that is CHANGING, where
    /// losing a message is fine because a newer one is 100ms behind it.
    ///
    /// NOTHING IS PERSISTED. No history, no retry, no delivery to somebody who was not connected.
    /// The test is one question: if this is lost, does it matter? Yes means it belongs in a table
    /// via <c>prax.Data</c>, or in an automation. No, because a newer one is coming, means it
    /// belongs here.
    ///
    /// PAYLOADS ARE HOSTILE. The bus relays opaque JSON between USERS and parses none of it, so
    /// every server-side sanitizer is bypassed. Treat what arrives the way you would treat a URL
    /// query string.
    ///
    /// Requires a signed-in end user: the hub authenticates with the session token, not with the
    /// workspace's publishable key.
    /// </summary>
    public sealed class PraxBus : IDisposable
    {
        private readonly PraxsuiteClient _client;
        private readonly Dictionary<string, PraxChannel> _channels = new Dictionary<string, PraxChannel>();
        private readonly Dictionary<string, TaskCompletionSource<PraxBusResult>> _pending =
            new Dictionary<string, TaskCompletionSource<PraxBusResult>>();
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _receiveCancel;
        private Task _connecting;
        private string _buffer = string.Empty;
        private int _nextInvocation;
        private bool _closedByUs;
        private bool _warnedAboutRouting;
        private int _reconnectDelayMs;

        /// <summary>Reconnect automatically and re-join every bus that was held.</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>First backoff step, doubling to <see cref="MaxReconnectDelayMs"/>.</summary>
        public int ReconnectDelayMs { get; set; } = 1000;

        public int MaxReconnectDelayMs { get; set; } = 30000;

        /// <summary>How long to wait for one invocation's completion.</summary>
        public int CallTimeoutMs { get; set; } = 30000;

        /// <summary>Raised on every transition. Reconnecting is a good moment to grey out a roster.</summary>
        public event Action<PraxBusState> StateChanged;

        public PraxBusState State { get; private set; } = PraxBusState.Disconnected;

        internal PraxBus(PraxsuiteClient client)
        {
            _client = client;
        }

        /// <summary>A topic by key. Nothing is sent until one of its channels is joined.</summary>
        public PraxTopic Topic(string key)
        {
            return new PraxTopic(this, PraxBusWire.NormalizeBusKey(key));
        }

        /// <summary>
        /// A channel by full key, <c>topic:instance</c>. Repeated calls return the SAME object, so
        /// handlers registered anywhere in the app all fire.
        /// </summary>
        public PraxChannel Channel(string busKey)
        {
            var key = PraxBusWire.RequireValidBusKey(busKey);
            lock (_gate)
            {
                PraxChannel channel;
                if (!_channels.TryGetValue(key, out channel))
                    _channels[key] = channel = new PraxChannel(this, key);
                return channel;
            }
        }

        /// <summary>
        /// The caller's own private bus.
        ///
        /// Addressed as <c>user:self</c> and resolved server-side to your id - which is what makes
        /// it the one bus needing no ticket, since it cannot name anybody else. Use it for messages
        /// aimed at one user across their open clients.
        /// </summary>
        public PraxChannel Self
        {
            get { return Channel("user:self"); }
        }

        /// <summary>
        /// Opens the connection. <see cref="PraxChannel.JoinAsync"/> calls this for you; call it
        /// directly to fail fast at start-up rather than on the first join.
        /// </summary>
        public Task ConnectAsync(CancellationToken ct = default(CancellationToken))
        {
            lock (_gate)
            {
                if (State == PraxBusState.Connected) return Task.CompletedTask;
                if (_connecting != null) return _connecting;
                _closedByUs = false;
                _connecting = OpenAsync(ct);
                return _connecting;
            }
        }

        /// <summary>Closes the connection and stops reconnecting. Channels keep their handlers.</summary>
        public async Task DisconnectAsync()
        {
            ClientWebSocket socket;
            lock (_gate)
            {
                _closedByUs = true;
                socket = _socket;
                _socket = null;
                foreach (var channel in _channels.Values)
                {
                    channel.Wanted = false;
                    channel.Joined = false;
                }
            }

            if (_receiveCancel != null) _receiveCancel.Cancel();

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // A socket that will not close politely is closing anyway.
                }
                socket.Dispose();
            }

            SetState(PraxBusState.Disconnected);
        }

        /// <summary>Sends one invocation and waits for its completion.</summary>
        internal async Task<PraxBusResult> InvokeAsync(
            string target, List<object> args, CancellationToken ct)
        {
            await ConnectAsync(ct).ConfigureAwait(false);

            ClientWebSocket socket;
            string invocationId;
            var completion = new TaskCompletionSource<PraxBusResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                socket = _socket;
                invocationId = (++_nextInvocation).ToString();
                _pending[invocationId] = completion;
            }

            if (socket == null || socket.State != WebSocketState.Open)
                throw new PraxException("BUS_NOT_CONNECTED", "The Event Bus connection is not open.");

            try
            {
                await SendAsync(socket,
                    PraxBusWire.Frame(PraxBusWire.BuildInvocation(invocationId, target, args)), ct)
                    .ConfigureAwait(false);

                var finished = await Task.WhenAny(
                    completion.Task, Task.Delay(CallTimeoutMs, ct)).ConfigureAwait(false);

                if (finished != completion.Task)
                {
                    throw new PraxException("BUS_TIMEOUT",
                        "The hub did not answer " + target + " within " + CallTimeoutMs + "ms.");
                }

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _pending.Remove(invocationId);
            }
        }

        // ───────────────────────────────────────────────────────────── internals

        private async Task OpenAsync(CancellationToken ct)
        {
            try
            {
                var session = _client.CurrentSession;
                if (session == null || string.IsNullOrEmpty(session.accessToken))
                {
                    throw new PraxException("BUS_REQUIRES_SESSION",
                        "The Event Bus needs a signed-in end user - it authenticates with the session " +
                        "token, not with the workspace key. Call Auth.LoginAsync() (or finish an OIDC " +
                        "sign-in) first.");
                }

                SetState(State == PraxBusState.Disconnected
                    ? PraxBusState.Connecting
                    : PraxBusState.Reconnecting);

                var socket = new ClientWebSocket();
                // The token rides in the query string because a browser WebSocket cannot set
                // headers, and the hub accepts access_token for exactly that reason. One
                // placement across every runtime keeps this transport identical everywhere.
                var url = new Uri(PraxBusWire.SocketUrl(_client.BaseUrl, session.accessToken));

                await socket.ConnectAsync(url, ct).ConfigureAwait(false);

                lock (_gate)
                {
                    _socket = socket;
                    _buffer = string.Empty;
                }

                await SendAsync(socket, PraxBusWire.Frame(PraxBusWire.HandshakeFrame), ct)
                    .ConfigureAwait(false);

                var handshake = await ReceiveOnceAsync(socket, ct).ConfigureAwait(false);
                string remainder;
                var frames = PraxBusWire.SplitFrames(handshake, out remainder);
                if (frames.Count == 0)
                    throw new PraxException("BUS_CONNECT_FAILED", "The hub sent no handshake response.");

                var response = PraxJson.ParseObject(frames[0]);
                object handshakeError;
                if (response != null && response.TryGetValue("error", out handshakeError) &&
                    handshakeError is string)
                {
                    throw new PraxException("BUS_HANDSHAKE_REJECTED",
                        "The hub rejected the handshake: " + handshakeError);
                }

                lock (_gate) _buffer = remainder;
                _reconnectDelayMs = 0;
                SetState(PraxBusState.Connected);

                // Frames that arrived alongside the handshake still need handling.
                for (var i = 1; i < frames.Count; i++) HandleFrame(frames[i]);

                _receiveCancel = new CancellationTokenSource();
                var receiveToken = _receiveCancel.Token;
                var _ = Task.Run(() => ReceiveLoopAsync(socket, receiveToken));

                await RejoinAllAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _connecting = null;
            }
        }

        private async Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            // One writer at a time: concurrent SendAsync calls on a ClientWebSocket corrupt the
            // stream, and every channel shares this one socket.
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                    true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private static async Task<string> ReceiveOnceAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            return text.ToString();
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var text = await ReceiveOnceAsync(socket, ct).ConfigureAwait(false);
                    if (text.Length == 0 && socket.State != WebSocketState.Open) break;

                    string remainder;
                    List<string> frames;
                    lock (_gate)
                    {
                        frames = PraxBusWire.SplitFrames(_buffer + text, out remainder);
                        _buffer = remainder;
                    }

                    foreach (var frame in frames) HandleFrame(frame);
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnect() cancelled us.
            }
            catch (Exception ex)
            {
                PraxLog.Info("Event Bus connection dropped: " + ex.Message);
            }

            OnClosed();
        }

        private void HandleFrame(string raw)
        {
            Dictionary<string, object> message;
            try
            {
                message = PraxJson.ParseObject(raw);
            }
            catch (Exception)
            {
                PraxLog.Warn("Discarded an Event Bus frame that is not JSON.");
                return;
            }
            if (message == null) return;

            var type = PraxBusWire.ToInt(PraxBusWire.Get(message, "type"));

            if (type == PraxBusWire.MessagePing) return; // keepalive - never surface it

            if (type == PraxBusWire.MessageCompletion)
            {
                var invocationId = PraxBusWire.AsString(message, "invocationId");
                TaskCompletionSource<PraxBusResult> waiter;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(invocationId, out waiter)) return;
                    _pending.Remove(invocationId);
                }
                waiter.TrySetResult(PraxBusWire.ParseBusResult(message));
                return;
            }

            if (type == PraxBusWire.MessageInvocation)
            {
                HandleServerEvent(message);
                return;
            }

            if (type == PraxBusWire.MessageClose)
            {
                var why = PraxBusWire.AsString(message, "error");
                PraxLog.Warn("The hub closed the connection: " +
                             (why.Length > 0 ? why : "no reason given"));
            }
        }

        private void HandleServerEvent(Dictionary<string, object> message)
        {
            var target = PraxBusWire.AsString(message, "target");
            var args = PraxBusWire.Get(message, "arguments") as List<object>;
            var first = (args != null && args.Count > 0
                ? args[0] as Dictionary<string, object>
                : null) ?? new Dictionary<string, object>();

            switch (target)
            {
                case "bus-event":
                    foreach (var channel in Route(first))
                    {
                        channel.Dispatch(new PraxBusEvent
                        {
                            Bus = channel.Key,
                            FromUserId = PraxBusWire.AsString(first, "fromUserId"),
                            Event = PraxBusWire.AsString(first, "event"),
                            Payload = PraxBusWire.Get(first, "payload"),
                        });
                    }
                    return;

                case "peer-joined":
                case "peer-left":
                {
                    var userId = PraxBusWire.AsString(first, "userId");
                    var joined = target == "peer-joined";
                    foreach (var channel in Route(first)) channel.DispatchPeer(joined, userId);
                    return;
                }

                case "bus-evicted":
                {
                    var key = PraxBusWire.NormalizeBusKey(PraxBusWire.AsString(first, "bus"));
                    PraxChannel channel;
                    lock (_gate) _channels.TryGetValue(key, out channel);
                    if (channel != null) channel.DispatchEvicted();
                    return;
                }

                default:
                    PraxLog.Verbose("Ignoring an unknown Event Bus message: " + target);
                    return;
            }
        }

        /// <summary>
        /// Decides which channels an inbound message belongs to.
        ///
        /// The message names its bus, and that is the whole answer. The fallback exists because a
        /// gateway older than 2026-09-07 does not send the field: one connection carries every
        /// joined bus, and SignalR reports which invocation arrived but never which group it came
        /// from, so on such a server a client holding two buses genuinely cannot tell their
        /// traffic apart.
        /// </summary>
        private List<PraxChannel> Route(Dictionary<string, object> message)
        {
            var named = PraxBusWire.NormalizeBusKey(PraxBusWire.AsString(message, "bus"));
            var matched = new List<PraxChannel>();

            lock (_gate)
            {
                if (named.Length > 0)
                {
                    PraxChannel channel;
                    if (_channels.TryGetValue(named, out channel)) matched.Add(channel);
                    return matched;
                }

                foreach (var channel in _channels.Values)
                {
                    if (channel.Joined) matched.Add(channel);
                }
            }

            if (matched.Count > 1 && !_warnedAboutRouting)
            {
                _warnedAboutRouting = true;
                PraxLog.Warn(
                    "This gateway sends bus messages without naming their bus, so events cannot be " +
                    "routed to the channel they came from. Handlers on every joined channel will see " +
                    "them. Update the gateway, or hold one bus per connection until you can.");
            }

            return matched;
        }

        private void OnClosed()
        {
            List<TaskCompletionSource<PraxBusResult>> waiting;
            lock (_gate)
            {
                _socket = null;
                waiting = new List<TaskCompletionSource<PraxBusResult>>(_pending.Values);
                _pending.Clear();
                foreach (var channel in _channels.Values) channel.Joined = false;
            }

            foreach (var waiter in waiting)
            {
                waiter.TrySetException(new PraxException("BUS_DISCONNECTED",
                    "The connection closed before the call completed."));
            }

            if (_closedByUs || !AutoReconnect)
            {
                SetState(PraxBusState.Disconnected);
                return;
            }

            SetState(PraxBusState.Reconnecting);
            _reconnectDelayMs = _reconnectDelayMs == 0
                ? ReconnectDelayMs
                : Math.Min(_reconnectDelayMs * 2, MaxReconnectDelayMs);

            var delay = _reconnectDelayMs;
            PraxLog.Info("Event Bus reconnecting in " + delay + "ms.");

            var _ = Task.Run(async delegate
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (_closedByUs) return;
                try
                {
                    await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PraxLog.Warn("Event Bus reconnect failed: " + ex.Message);
                    OnClosed();
                }
            });
        }

        /// <summary>
        /// Re-joins every bus the app still wants.
        ///
        /// Not optional bookkeeping. SignalR group membership does not survive a reconnect, so a
        /// client that reconnects and stops there is connected and in no groups - receiving
        /// nothing, reporting no error, and looking for all the world like a broken server.
        ///
        /// Re-joining calls JoinBus again, which re-runs the topic's access rule. The SDK never
        /// replays a membership list for the server to take on faith.
        /// </summary>
        private async Task RejoinAllAsync(CancellationToken ct)
        {
            List<PraxChannel> pending;
            lock (_gate) pending = new List<PraxChannel>(_channels.Values);

            foreach (var channel in pending)
            {
                if (!channel.Wanted || channel.Joined) continue;
                try
                {
                    await channel.JoinAsync(null, ct).ConfigureAwait(false);
                    PraxLog.Info("Re-joined \"" + channel.Key + "\" after reconnecting.");
                }
                catch (Exception ex)
                {
                    PraxLog.Warn("Could not re-join \"" + channel.Key + "\": " + ex.Message);
                }
            }
        }

        private void SetState(PraxBusState state)
        {
            if (State == state) return;
            State = state;

            var handler = StateChanged;
            if (handler == null) return;
            try { handler(state); }
            catch (Exception ex) { PraxLog.Error("A bus state handler threw: " + ex.Message); }
        }

        public void Dispose()
        {
            try { DisconnectAsync().GetAwaiter().GetResult(); }
            catch (Exception) { }
            _sendGate.Dispose();
            if (_receiveCancel != null) _receiveCancel.Dispose();
        }
    }
}
