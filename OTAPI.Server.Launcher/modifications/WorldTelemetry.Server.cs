/*
 * OTAPI runtime telemetry plugin for dedicated Terraria/tModLoader servers.
 * Streams compact, current-state JSON snapshots; it does not modify the world
 * or write telemetry to disk.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ModFramework;
using Terraria;

namespace OTAPI.WorldTelemetry;

public static class WorldTelemetryPlugin
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DateTime _lastSnapshotUtc = DateTime.MinValue;
    private static string? _lastSnapshotJson;
    private static TcpListener? _listener;
    private static CancellationTokenSource? _shutdown;
    private static readonly ConcurrentDictionary<int, TelemetryClient> Clients = new();
    private static int _nextClientId;

    [Modification(ModType.Runtime, "Starting OTAPI world telemetry")]
    public static void OnStart()
    {
        var configuredPort = Environment.GetEnvironmentVariable("OTAPI_TELEMETRY_PORT");
        var port = int.TryParse(configuredPort, out var parsedPort) && parsedPort is >= 1 and <= 65535
            ? parsedPort
            : 38761;

        _shutdown = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            _listener.Start();
            On.Terraria.Main.Update += OnMainUpdate;
            _ = AcceptClientsAsync(_listener, _shutdown.Token);
            Console.WriteLine($"[WorldTelemetry] Streaming JSON snapshots on 127.0.0.1:{port} every second (no disk polling/writes).");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[WorldTelemetry] TCP listener unavailable; telemetry disabled: {exception.Message}");
            On.Terraria.Main.Update -= OnMainUpdate;
            _shutdown.Cancel();
            _listener?.Stop();
            _listener = null;
        }
    }

    [Modification(ModType.Shutdown, "Stopping OTAPI world telemetry")]
    public static void OnStop()
    {
        On.Terraria.Main.Update -= OnMainUpdate;
        _shutdown?.Cancel();
        _listener?.Stop();
        foreach (var client in Clients.Values)
            client.Stop();
        Clients.Clear();
    }

    private static void OnMainUpdate(On.Terraria.Main.orig_Update orig, Main self, Microsoft.Xna.Framework.GameTime gameTime)
    {
        orig(self, gameTime);

        if (Main.netMode != 2 || string.IsNullOrWhiteSpace(Main.worldName))
            return;

        var now = DateTime.UtcNow;
        if (now - _lastSnapshotUtc < SnapshotInterval)
            return;

        _lastSnapshotUtc = now;
        if (Clients.IsEmpty)
        {
            Volatile.Write(ref _lastSnapshotJson, null);
            return;
        }

        try
        {
            WriteSnapshot(now);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[WorldTelemetry] Snapshot failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void WriteSnapshot(DateTime now)
    {
        var mode = Main.GameMode;
        var players = Enumerable.Range(0, Main.player.Length)
            .Where(slot => Main.player[slot] is { active: true })
            .Select(slot =>
            {
                var player = Main.player[slot];
                var uuid = ReadClientUuid(slot);
                return new
                {
                    slot,
                    name = player.name,
                    uuid
                };
            })
            .ToArray();

        var snapshot = new
        {
            schemaVersion = 1,
            updatedAtUtc = now.ToString("O"),
            server = new
            {
                version = Main.versionNumber,
                serverAssemblyVersion = typeof(Main).Assembly.GetName().Version?.ToString(),
                otapiVersion = OTAPI.Common.VersionShort,
                dedicated = Main.netMode == 2
            },
            world = new
            {
                name = Main.worldName,
                id = Main.worldID,
                width = Main.maxTilesX,
                height = Main.maxTilesY,
                gameMode = new { id = mode, name = GameModeName(mode) },
                hardmode = Main.hardMode,
                crimson = WorldGen.crimson,
                day = Main.dayTime,
                time = Main.time,
                bloodMoon = Main.bloodMoon,
                eclipse = Main.eclipse,
                bosses = Bosses()
            },
            players = new
            {
                online = players.Length,
                list = players
            }
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
        Volatile.Write(ref _lastSnapshotJson, json);
        foreach (var client in Clients.Values)
            client.Publish(json);
    }

    private static async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient socket;
            try
            {
                socket = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            socket.NoDelay = true;
            var id = Interlocked.Increment(ref _nextClientId);
            var client = new TelemetryClient(socket, () => Clients.TryRemove(id, out _));
            Clients[id] = client;
            if (Volatile.Read(ref _lastSnapshotJson) is { } latest)
                client.Publish(latest);
            _ = client.RunAsync(cancellationToken);
            Console.WriteLine($"[WorldTelemetry] Python/dashboard client connected ({Clients.Count} active).");
        }
    }

    private sealed class TelemetryClient
    {
        private readonly TcpClient _socket;
        private readonly Action _onClosed;
        private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        public TelemetryClient(TcpClient socket, Action onClosed)
        {
            _socket = socket;
            _onClosed = onClosed;
        }

        public void Publish(string json) => _pending.Writer.TryWrite(json);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                var stream = _socket.GetStream();
                await foreach (var json in _pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            finally
            {
                Stop();
                _onClosed();
            }
        }

        public void Stop()
        {
            _pending.Writer.TryComplete();
            try { _socket.Close(); } catch { }
        }
    }

    private static object[] Bosses() => new object[]
    {
        Boss("king_slime", "King Slime", NPC.downedSlimeKing),
        Boss("eye_of_cthulhu", "Eye of Cthulhu", NPC.downedBoss1),
        Boss("eater_of_worlds_or_brain", "Eater of Worlds / Brain of Cthulhu", NPC.downedBoss2),
        Boss("queen_bee", "Queen Bee", NPC.downedQueenBee),
        Boss("skeletron", "Skeletron", NPC.downedBoss3),
        Boss("deerclops", "Deerclops", NPC.downedDeerclops),
        Boss("wall_of_flesh", "Wall of Flesh / hardmode", Main.hardMode),
        Boss("queen_slime", "Queen Slime", NPC.downedQueenSlime),
        Boss("destroyer", "The Destroyer", NPC.downedMechBoss1),
        Boss("twins", "The Twins", NPC.downedMechBoss2),
        Boss("skeletron_prime", "Skeletron Prime", NPC.downedMechBoss3),
        Boss("plantera", "Plantera", NPC.downedPlantBoss),
        Boss("golem", "Golem", NPC.downedGolemBoss),
        Boss("duke_fishron", "Duke Fishron", NPC.downedFishron),
        Boss("empress_of_light", "Empress of Light", NPC.downedEmpressOfLight),
        Boss("lunatic_cultist", "Lunatic Cultist", NPC.downedAncientCultist),
        Boss("moon_lord", "Moon Lord", NPC.downedMoonlord),
        Boss("solar_pillar", "Solar Pillar", NPC.downedTowerSolar),
        Boss("vortex_pillar", "Vortex Pillar", NPC.downedTowerVortex),
        Boss("nebula_pillar", "Nebula Pillar", NPC.downedTowerNebula),
        Boss("stardust_pillar", "Stardust Pillar", NPC.downedTowerStardust),
        Boss("goblin_army", "Goblin Army", NPC.downedGoblins),
        Boss("frost_legion", "Frost Legion", NPC.downedFrost),
        Boss("pirate_invasion", "Pirate Invasion", NPC.downedPirates),
        Boss("martian_madness", "Martian Madness", NPC.downedMartians),
        Boss("pumpkin_moon_tree", "Pumpkin Moon: Mourning Wood", NPC.downedHalloweenTree),
        Boss("pumpkin_moon_pumpking", "Pumpkin Moon: Pumpking", NPC.downedHalloweenKing),
        Boss("frost_moon_ice_queen", "Frost Moon: Ice Queen", NPC.downedChristmasIceQueen),
        Boss("frost_moon_santa_nk1", "Frost Moon: Santa-NK1", NPC.downedChristmasSantank),
        Boss("frost_moon_everscream", "Frost Moon: Everscream", NPC.downedChristmasTree),
        Boss("clown", "Clown", NPC.downedClown)
    };

    private static object Boss(string id, string name, bool defeated) => new { id, name, defeated };

    private static string GameModeName(int mode) => mode switch
    {
        0 => "classic",
        1 => "expert",
        2 => "master",
        3 => "journey",
        _ => "unknown"
    };

    private static string? ReadClientUuid(int slot)
    {
        try
        {
            var client = Netplay.Clients[slot];
            return client.GetType().GetProperty("ClientUUID", BindingFlags.Instance | BindingFlags.Public)?.GetValue(client) as string;
        }
        catch
        {
            return null;
        }
    }
}
