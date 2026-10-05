// SPDX-License-Identifier: AGPL-3.0-or-later
#if ARCANUM_EOS
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

namespace Arcanum.Client.Eos;

/// <summary>
/// Internet lobbies and connections through Epic Online Services: players sign in with a device account (no Epic account),
/// hosted lobbies are listed in the service's lobby directory, and players reach the host with peer-to-peer connections that
/// fall back to the service's relays when a direct path isn't possible. Every SDK call runs on one thread of its own.
/// </summary>
internal sealed class EosOnlineServices : IOnlineServices, IPacketLink
{
    private const string SocketName = "ArcanumGame";
    private const string BucketPrefix = "arcanum:";

    // Lobby attributes.
    private const string AttrId = "ARCANUM_ID", AttrHost = "HOST", AttrFormat = "FORMAT", AttrCommander = "COMMANDER",
        AttrSeats = "SEATS", AttrOpen = "OPEN", AttrVersion = "VERSION", AttrContent = "CONTENT", AttrCode = "CODE",
        AttrListed = "LISTED", AttrEvent = "EVENT";

    private readonly EosKeys _keys;
    private readonly string _productVersion;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ConcurrentQueue<(string Peer, byte[] Packet)> _outgoing = new();
    private readonly ConcurrentQueue<(string Peer, byte[] Packet)> _incoming = new();
    private readonly ConcurrentQueue<string> _lost = new();
    private readonly Thread _thread;
    private readonly PacketRelay _relay;
    private volatile bool _stop;
    private volatile string? _failure;
    private volatile ProductUserId? _user;
    private Task<OnlineIdentity>? _signIn;
    private bool _deviceReplaced;

    // EOS thread only.
    private PlatformInterface? _platform;
    private readonly Dictionary<string, string> _eosLobbies = new(); // our lobby id → the service's
    private readonly Dictionary<string, ProductUserId> _peers = new();
    private readonly SocketId _socket = new() { SocketName = SocketName };
    private byte[] _buffer = new byte[P2PInterface.MAX_PACKET_SIZE];

    /// <summary>Diagnostics from the SDK and this service.</summary>
    public event Action<string>? Log;

    public EosOnlineServices(EosKeys keys, string productVersion)
    {
        _keys = keys;
        _productVersion = productVersion;
        _relay = new PacketRelay(this);
        Lobbies = new Directory(this);
        Network = new Relay(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "Online services" };
        _thread.Start();
    }

    public string Name => "Internet";
    public bool IsAvailable => _user is not null;
    public ILobbyDirectory Lobbies { get; }
    public IRelayNetwork Network { get; }

    /// <summary>Why the service can't be used, once that's known.</summary>
    public string? Failure => _failure;

    public Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default)
    {
        lock (_work)
        {
            if (_signIn is { IsFaulted: false, IsCanceled: false }) return _signIn;
            return _signIn = OnEos<OnlineIdentity>(done => CreateDeviceId(displayName, done));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    // ------------------------------------------------------------------ the SDK's thread

    private Task<T> OnEos<T>(Action<TaskCompletionSource<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            if (_platform is null) done.TrySetException(new InvalidOperationException(_failure ?? "Online services aren't running."));
            else
            {
                try { work(done); }
                catch (Exception e) { done.TrySetException(e); }
            }
        });
        return done.Task;
    }

    private void Run()
    {
        try { Start(); }
        catch (Exception e) { _failure = $"Online services couldn't start: {e.Message}"; }
        while (!_stop)
        {
            while (_work.TryDequeue(out var work)) work();
            if (_platform is not null)
            {
                _platform.Tick();
                if (_user is not null) PumpPackets();
            }
            Thread.Sleep(_outgoing.IsEmpty ? 8 : 1);
        }
        Stop();
    }

    private static int _resolverSet;

    /// <summary>
    /// The SDK's native library sits next to its assembly (copied by the build and the export); under Godot the runtime doesn't
    /// look there by itself.
    /// </summary>
    private static void FindNativeLibrary()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(Common).Assembly, (name, assembly, path) =>
        {
            if (!name.Contains("EOSSDK")) return IntPtr.Zero;
            var places = new[] { Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) };
            foreach (var place in places.Where(p => !string.IsNullOrEmpty(p)))
            {
                var file = Path.Combine(place!, name);
                if (File.Exists(file) && NativeLibrary.TryLoad(file, out var handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    private void Start()
    {
        FindNativeLibrary();
        // ARCANUM_EOS_LOG=1 shows the SDK's detailed log.
        LoggingInterface.SetLogLevel(LogCategory.AllCategories, Environment.GetEnvironmentVariable("ARCANUM_EOS_LOG") == "1" ? LogLevel.Verbose : LogLevel.Warning);
        LoggingInterface.SetCallback((ref LogMessage message) => Log?.Invoke($"EOS {message.Category}: {message.Message}"));
        var init = new InitializeOptions { ProductName = "Arcanum", ProductVersion = _productVersion };
        var result = PlatformInterface.Initialize(ref init);
        if (result != Result.Success && result != Result.AlreadyConfigured) throw new InvalidOperationException(result.ToString());
        var options = new Options
        {
            ProductId = _keys.ProductId,
            SandboxId = _keys.SandboxId,
            DeploymentId = _keys.DeploymentId,
            ClientCredentials = new ClientCredentials { ClientId = _keys.ClientId, ClientSecret = _keys.ClientSecret },
            IsServer = false,
            CacheDirectory = Path.Combine(Path.GetTempPath(), "arcanum-eos"),
            Flags = PlatformFlags.DisableOverlay,
        };
        System.IO.Directory.CreateDirectory(options.CacheDirectory);
        _platform = PlatformInterface.Create(ref options) ?? throw new InvalidOperationException("the platform couldn't be created (check the product keys)");
    }

    private void Stop()
    {
        if (_platform is null) return;
        foreach (var lobby in _eosLobbies.Values)
        {
            var destroy = new DestroyLobbyOptions { LocalUserId = _user, LobbyId = lobby };
            _platform.GetLobbyInterface().DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo _) => { });
        }
        for (int i = 0; i < 20 && _eosLobbies.Count > 0; i++) { _platform.Tick(); Thread.Sleep(10); }
        _platform.Release();
        _platform = null;
        PlatformInterface.Shutdown();
    }

    // ------------------------------------------------------------------ sign-in (device account)

    private void CreateDeviceId(string displayName, TaskCompletionSource<OnlineIdentity> done)
    {
        var connect = _platform!.GetConnectInterface();
        var create = new CreateDeviceIdOptions { DeviceModel = Environment.MachineName };
        if (Environment.GetEnvironmentVariable("ARCANUM_EOS_NEW_DEVICE") == "1" && !_deviceReplaced)
        {
            // Testing several players on one computer: a new device account (players already signed in stay signed in).
            _deviceReplaced = true;
            var delete = new DeleteDeviceIdOptions();
            connect.DeleteDeviceId(ref delete, null, (ref DeleteDeviceIdCallbackInfo _) => CreateDeviceId(displayName, done));
            return;
        }
        connect.CreateDeviceId(ref create, null, (ref CreateDeviceIdCallbackInfo info) =>
        {
            if (info.ResultCode is Result.Success or Result.DuplicateNotAllowed) Login(displayName, done);
            else Fail(done, "creating the device account", info.ResultCode);
        });
    }

    private void Login(string displayName, TaskCompletionSource<OnlineIdentity> done)
    {
        var connect = _platform!.GetConnectInterface();
        string name = displayName.Length > 32 ? displayName[..32] : displayName.Length == 0 ? "Player" : displayName;
        var login = new LoginOptions
        {
            Credentials = new Epic.OnlineServices.Connect.Credentials { Type = ExternalCredentialType.DeviceidAccessToken },
            UserLoginInfo = new UserLoginInfo { DisplayName = name },
        };
        connect.Login(ref login, null, (ref LoginCallbackInfo info) =>
        {
            if (info.ResultCode == Result.Success) SignedIn(info.LocalUserId, name, done);
            else if (info.ResultCode == Result.InvalidUser)
            {
                var create = new CreateUserOptions { ContinuanceToken = info.ContinuanceToken };
                connect.CreateUser(ref create, null, (ref CreateUserCallbackInfo created) =>
                {
                    if (created.ResultCode == Result.Success) SignedIn(created.LocalUserId, name, done);
                    else Fail(done, "creating the player", created.ResultCode);
                });
            }
            else Fail(done, "signing in", info.ResultCode);
        });
    }

    private void SignedIn(ProductUserId user, string name, TaskCompletionSource<OnlineIdentity> done)
    {
        var p2p = _platform!.GetP2PInterface();
        var requests = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = _socket };
        p2p.AddNotifyPeerConnectionRequest(ref requests, null, (ref OnIncomingConnectionRequestInfo info) =>
        {
            if (info.SocketId?.SocketName != SocketName) return;
            var accept = new AcceptConnectionOptions { LocalUserId = info.LocalUserId, RemoteUserId = info.RemoteUserId, SocketId = _socket };
            p2p.AcceptConnection(ref accept);
        });
        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user, SocketId = _socket };
        p2p.AddNotifyPeerConnectionClosed(ref closed, null, (ref OnRemoteConnectionClosedInfo info) =>
        {
            if (info.RemoteUserId?.ToString() is { } peer) _lost.Enqueue(peer);
        });
        _user = user;
        done.TrySetResult(new OnlineIdentity(user.ToString(), name));
    }

    private static void Fail<T>(TaskCompletionSource<T> done, string what, Result result) =>
        done.TrySetException(new InvalidOperationException($"Online services failed {what}: {result}."));

    // ------------------------------------------------------------------ packets

    int IPacketLink.MaxPacketSize => P2PInterface.MAX_PACKET_SIZE;

    void IPacketLink.Send(string peer, byte[] packet) => _outgoing.Enqueue((peer, packet));

    bool IPacketLink.TryReceive([NotNullWhen(true)] out string? peer, [NotNullWhen(true)] out byte[]? packet)
    {
        if (_incoming.TryDequeue(out var item))
        {
            (peer, packet) = item;
            return true;
        }
        peer = null;
        packet = null;
        return false;
    }

    bool IPacketLink.TryTakeClosedPeer([NotNullWhen(true)] out string? peer) => _lost.TryDequeue(out peer);

    private ProductUserId Peer(string id)
    {
        if (!_peers.TryGetValue(id, out var user)) _peers[id] = user = ProductUserId.FromString(id);
        return user;
    }

    private void PumpPackets()
    {
        var p2p = _platform!.GetP2PInterface();
        while (_outgoing.TryDequeue(out var item))
        {
            var send = new SendPacketOptions
            {
                LocalUserId = _user,
                RemoteUserId = Peer(item.Peer),
                SocketId = _socket,
                Channel = 0,
                Data = new ArraySegment<byte>(item.Packet),
                AllowDelayedDelivery = true,
                Reliability = PacketReliability.ReliableOrdered,
            };
            var result = p2p.SendPacket(ref send);
            if (result != Result.Success)
            {
                Log?.Invoke($"Sending to {item.Peer} failed: {result}");
                _lost.Enqueue(item.Peer);
            }
        }
        var sizeOptions = new GetNextReceivedPacketSizeOptions { LocalUserId = _user };
        while (p2p.GetNextReceivedPacketSize(ref sizeOptions, out uint size) == Result.Success)
        {
            if (size > _buffer.Length) _buffer = new byte[size];
            var receive = new ReceivePacketOptions { LocalUserId = _user, MaxDataSizeBytes = (uint)_buffer.Length };
            var from = new ProductUserId();
            var socket = new SocketId();
            if (p2p.ReceivePacket(ref receive, ref from, ref socket, out _, new ArraySegment<byte>(_buffer), out uint written) != Result.Success) break;
            if (socket.SocketName != SocketName) continue;
            _incoming.Enqueue((from.ToString(), _buffer.AsSpan(0, (int)written).ToArray()));
        }
    }

    // ------------------------------------------------------------------ lobbies

    private static string Bucket(string version) => BucketPrefix + version;

    private static void AddAttributes(LobbyModification modification, LobbyListing listing)
    {
        void Add(string key, AttributeDataValue value)
        {
            var add = new LobbyModificationAddAttributeOptions
            {
                Attribute = new AttributeData { Key = key, Value = value },
                Visibility = LobbyAttributeVisibility.Public,
            };
            modification.AddAttribute(ref add);
        }
        Add(AttrId, listing.LobbyId);
        Add(AttrHost, listing.HostName);
        Add(AttrFormat, listing.Format);
        Add(AttrCommander, (long?)(listing.Commander ? 1 : 0));
        Add(AttrSeats, (long?)listing.Seats);
        Add(AttrOpen, (long?)listing.OpenSeats);
        Add(AttrVersion, listing.Version);
        Add(AttrContent, listing.Content);
        Add(AttrCode, listing.InviteCode);
        Add(AttrListed, (long?)(listing.Listed ? 1 : 0));
        Add(AttrEvent, listing.Event ?? "");
    }

    private void SetAttributes(string eosLobby, LobbyListing listing, Action<Result> done)
    {
        var lobbies = _platform!.GetLobbyInterface();
        var modify = new UpdateLobbyModificationOptions { LocalUserId = _user, LobbyId = eosLobby };
        var result = lobbies.UpdateLobbyModification(ref modify, out var modification);
        if (result != Result.Success || modification is null)
        {
            done(result);
            return;
        }
        AddAttributes(modification, listing);
        var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
        lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo info) => done(info.ResultCode));
        modification.Release();
    }

    private static string? Text(LobbyDetails details, string key)
    {
        var options = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
        return details.CopyAttributeByKey(ref options, out var attribute) == Result.Success ? attribute?.Data?.Value.AsUtf8?.ToString() : null;
    }

    private static long Number(LobbyDetails details, string key)
    {
        var options = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
        return details.CopyAttributeByKey(ref options, out var attribute) == Result.Success ? attribute?.Data?.Value.AsInt64 ?? 0 : 0;
    }

    private static LobbyListing? ListingOf(LobbyDetails details)
    {
        var info = new LobbyDetailsCopyInfoOptions();
        if (details.CopyInfo(ref info, out var lobby) != Result.Success || lobby is not { } l) return null;
        if (Text(details, AttrId) is not { Length: > 0 } id) return null;
        string? ev = Text(details, AttrEvent);
        return new LobbyListing(id, Text(details, AttrHost) ?? "Host", Text(details, AttrFormat) ?? "", Number(details, AttrCommander) != 0,
            (int)Number(details, AttrSeats), (int)Number(details, AttrOpen), Text(details, AttrVersion) ?? "", Text(details, AttrContent) ?? "",
            Text(details, AttrCode) ?? "", Number(details, AttrListed) != 0, ev is { Length: > 0 } ? ev : null)
        {
            Address = l.LobbyOwnerUserId?.ToString(),
        };
    }

    private void Search(Action<LobbySearch> parameters, TaskCompletionSource<IReadOnlyList<LobbyListing>> done)
    {
        var lobbies = _platform!.GetLobbyInterface();
        var create = new CreateLobbySearchOptions { MaxResults = 100 };
        var result = lobbies.CreateLobbySearch(ref create, out var search);
        if (result != Result.Success || search is null)
        {
            Fail(done, "searching lobbies", result);
            return;
        }
        parameters(search);
        var find = new LobbySearchFindOptions { LocalUserId = _user };
        search.Find(ref find, null, (ref LobbySearchFindCallbackInfo info) =>
        {
            var found = new List<LobbyListing>();
            if (info.ResultCode == Result.Success)
            {
                var count = new LobbySearchGetSearchResultCountOptions();
                for (uint i = 0, n = search.GetSearchResultCount(ref count); i < n; i++)
                {
                    var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                    if (search.CopySearchResultByIndex(ref copy, out var details) != Result.Success || details is null) continue;
                    if (ListingOf(details) is { } listing) found.Add(listing);
                    details.Release();
                }
            }
            search.Release();
            if (info.ResultCode is Result.Success or Result.NotFound) done.TrySetResult(found);
            else Fail(done, "searching lobbies", info.ResultCode);
        });
    }

    private static void Where(LobbySearch search, string key, AttributeDataValue value, ComparisonOp op = ComparisonOp.Equal)
    {
        var parameter = new LobbySearchSetParameterOptions { Parameter = new AttributeData { Key = key, Value = value }, ComparisonOp = op };
        search.SetParameter(ref parameter);
    }

    private sealed class Directory(EosOnlineServices eos) : ILobbyDirectory
    {
        public Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            listing = listing with { Address = eos._user?.ToString(), Port = 0 };
            return eos.OnEos<LobbyListing>(done =>
            {
                var create = new CreateLobbyOptions
                {
                    LocalUserId = eos._user,
                    MaxLobbyMembers = (uint)Math.Clamp(listing.Seats, 1, LobbyInterface.MAX_LOBBY_MEMBERS),
                    PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                    PresenceEnabled = false,
                    AllowInvites = false,
                    BucketId = Bucket(listing.Version),
                    DisableHostMigration = true,
                    EnableRTCRoom = false,
                    EnableJoinById = false,
                };
                eos._platform!.GetLobbyInterface().CreateLobby(ref create, null, (ref CreateLobbyCallbackInfo info) =>
                {
                    if (info.ResultCode != Result.Success)
                    {
                        Fail(done, "creating the lobby", info.ResultCode);
                        return;
                    }
                    string eosLobby = info.LobbyId;
                    eos._eosLobbies[listing.LobbyId] = eosLobby;
                    eos.SetAttributes(eosLobby, listing, result =>
                    {
                        if (result == Result.Success) done.TrySetResult(listing);
                        else Fail(done, "listing the lobby", result);
                    });
                });
            });
        }

        public Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default) =>
            eos.OnEos<bool>(done =>
            {
                if (!eos._eosLobbies.TryGetValue(listing.LobbyId, out var eosLobby)) done.TrySetResult(false);
                else eos.SetAttributes(eosLobby, listing with { Address = eos._user?.ToString() }, result => done.TrySetResult(result == Result.Success));
            });

        public Task RemoveAsync(string lobbyId, CancellationToken cancel = default) =>
            eos.OnEos<bool>(done =>
            {
                if (!eos._eosLobbies.Remove(lobbyId, out var eosLobby))
                {
                    done.TrySetResult(false);
                    return;
                }
                var destroy = new DestroyLobbyOptions { LocalUserId = eos._user, LobbyId = eosLobby };
                eos._platform!.GetLobbyInterface().DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => done.TrySetResult(info.ResultCode == Result.Success));
            });

        public Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default) =>
            eos.OnEos<IReadOnlyList<LobbyListing>>(done => eos.Search(search =>
            {
                Where(search, LobbyInterface.SEARCH_BUCKET_ID, Bucket(query.Version));
                Where(search, AttrContent, query.Content);
                Where(search, AttrListed, (long?)1);
                if (query.OnlyWithOpenSeats) Where(search, AttrOpen, (long?)0, ComparisonOp.Greaterthan);
            }, done));

        public async Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default)
        {
            if (!InviteCode.TryParse(inviteCode, out var code)) return null;
            var found = await eos.OnEos<IReadOnlyList<LobbyListing>>(done => eos.Search(search => Where(search, AttrCode, code), done));
            return found.FirstOrDefault();
        }
    }

    private sealed class Relay(EosOnlineServices eos) : IRelayNetwork
    {
        public IConnectionListener Listen(string lobbyId) => eos._relay.Listen();

        public async Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default)
        {
            if (lobby.Address is not { Length: > 0 } host) throw new InvalidOperationException("That lobby has no host to reach.");
            return await eos._relay.ConnectAsync(host, TimeSpan.FromSeconds(20), cancel);
        }
    }
}
#endif
