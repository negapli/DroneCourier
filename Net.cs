using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Il2CppFishNet;
using Il2CppSteamworks;
using SteamNetworkLib;
using SteamNetworkLib.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DroneCourier;

public enum Op { Sender, Receiver, Trigger, Stacks, Every, Mode, Items, Retire, Buy, Rename } // sent as numbers: only append

// Multiplayer with SteamNetworkLib (ifBars, the S1API author; what other Schedule I mods use), over the game's Steam lobby.
// The host owns the drones (save, trips, refunds).
//  - host -> everyone: lobby data "dronecourier" = the drone list (gzip+base64 JSON), republished when it changes.
//  - client -> host: one typed P2P message per edit (DroneCmdMessage).
// Every change to a drone goes through Do(): the host applies it, a client applies it locally (so the app reacts at
// once) and sends it to the host. Singleplayer = host with no lobby.
// ponytail: the host trusts clients' edits (they ran the same checks on their synced copy).
static class Net
{
    const string StateKey = "dronecourier";

    static SteamNetworkClient steam;
    public static bool IsClient => !InstanceFinder.IsServer && InstanceFinder.IsClient;
    static bool Online => steam != null && steam.IsInLobby;

    // --- setup: the library must start before the game joins a lobby (it watches Steam's lobby-entered event) ---

    static float nextTry;
    public static void Update()
    {
        if (steam == null)
        {
            if (UnityEngine.Time.time < nextTry) return;
            nextTry = UnityEngine.Time.time + 2f; // Steam may not be ready during the first frames
            var c = new SteamNetworkClient();
            if (!c.TryInitialize(out var error)) { Mod.Log($"net: SteamNetworkLib not ready ({error?.Message})"); return; }
            c.RegisterMessageHandler<DroneCmdMessage>(OnCommand);
            steam = c;
            Mod.Log("net: SteamNetworkLib ready");
        }
        steam.ProcessIncomingMessages(); // runs handlers on the main thread
    }

    public static void Shutdown() { steam?.Dispose(); steam = null; }

    // --- edits ---

    public static void Do(Drone d, Op op, string arg = "")
    {
        if (!IsClient) { Apply(d, op, arg); return; }
        if (op is not (Op.Retire or Op.Buy)) { Apply(d, op, arg); editedAt[d.Id] = UnityEngine.Time.time; } // optimistic
        if (!Online || steam.GetHostMember() is not MemberInfo host) { Mod.Log($"net: not in a lobby, {op} not sent"); return; }
        var msg = new DroneCmdMessage(new DroneCmd { Op = (int)op, DroneId = d?.Id ?? "", Arg = arg });
        steam.SendMessageToPlayerAsync(host.SteamId, msg).ContinueWith(t =>
        {
            if (t.IsFaulted || !t.Result) Mod.Log($"net: sending {op} to the host failed {t.Exception?.GetBaseException().Message}");
        });
    }

    static void Apply(Drone d, Op op, string arg)
    {
        int n = int.TryParse(arg, out var v) ? v : 0;
        switch (op)
        {
            case Op.Sender: d.Sender = arg == "" ? null : arg; break;
            case Op.Receiver: d.Receiver = arg == "" ? null : arg; break;
            case Op.Trigger: d.Trigger = (Trigger)n; break;
            case Op.Stacks: d.TriggerStacks = n; break;
            case Op.Every: d.EveryMinutes = n; break;
            case Op.Mode: d.Mode = (Mode)n; break;
            case Op.Items: d.Items = arg == "" ? new() : new List<string>(arg.Split(',')); break;
            case Op.Retire: TechNerd.Retire(d); break;              // host only (Do never applies it on a client)
            case Op.Buy: Drones.Instance.Add((DroneSize)n); break;  // host only; the buyer already paid cash
            case Op.Rename: d.CustomName = string.IsNullOrWhiteSpace(arg) ? null : arg.Trim(); break;
        }
    }

    // Host: an edit from a client
    static void OnCommand(DroneCmdMessage msg, CSteamID from)
    {
        if (!InstanceFinder.IsServer || Drones.Instance == null || msg.Payload is not DroneCmd c) return;
        var op = (Op)c.Op;
        var d = Drones.Instance.Owned.Find(x => x.Id == c.DroneId);
        if (d == null && op != Op.Buy) return; // drone gone meanwhile
        Mod.Log($"client {from.m_SteamID}: {op} {d?.Name} {c.Arg}");
        Apply(d, op, c.Arg);
    }

    // --- client side ---

    static readonly Dictionary<string, float> editedAt = new(); // drone id -> time of our last local edit
    static readonly Dictionary<string, string> statuses = new(); // drone id -> host's status line

    /// The host's status line for a drone (clients don't run the trips), or null.
    public static string Status(Drone d) => IsClient && statuses.TryGetValue(d.Id, out var s) ? s : null;

    static string lastState;
    static void Receive()
    {
        var data = steam.GetLobbyData(StateKey);
        if (string.IsNullOrEmpty(data) || data == lastState || Drones.Instance == null) return;
        lastState = data;
        var list = JArray.Parse(Unzip(data));
        var owned = Drones.Instance.Owned;
        var ids = new HashSet<string>();
        foreach (JObject o in list)
        {
            string id = (string)o["Id"];
            ids.Add(id);
            statuses[id] = (string)o["Status"];
            o.Remove("Status");
            var d = owned.Find(x => x.Id == id);
            if (d == null) owned.Add(d = new Drone());
            else if (editedAt.TryGetValue(id, out var t) && UnityEngine.Time.time - t < 3f) continue; // don't undo our own fresh edit
            JsonConvert.PopulateObject(o.ToString(), d, Replace);
        }
        owned.RemoveAll(d => !ids.Contains(d.Id));
    }

    // Lists replaced, not appended to (Newtonsoft's default fills existing lists)
    static readonly JsonSerializerSettings Replace = new() { ObjectCreationHandling = ObjectCreationHandling.Replace };

    // --- host side ---

    static string lastPublished;
    static void Publish()
    {
        var list = new JArray();
        foreach (var d in Drones.Instance.Owned)
        {
            var o = JObject.FromObject(d);
            var ids = new JArray();
            foreach (var c in d.Cargo) ids.Add(Courier.CargoId(c)); // item ids only: clients just count them
            o["Cargo"] = ids;
            o["Status"] = Courier.Status(d);
            list.Add(o);
        }
        string data = Zip(list.ToString(Formatting.None));
        if (data == lastPublished) return;
        if (data.Length > 8000) Mod.Log($"warning: drone list is {data.Length} bytes, near Steam's lobby data limit (8 KB)"); // ponytail: split across keys if fleets get huge
        lastPublished = data;
        steam.SetLobbyData(StateKey, data);
    }

    // --- once a second ---

    public static void Tick()
    {
        if (Drones.Instance == null || !Online) return;
        try
        {
            if (InstanceFinder.IsServer) Publish();
            else if (InstanceFinder.IsClient) Receive();
        }
        catch (Exception e) { Mod.Log($"net: {e.Message}"); }
    }

    static string Zip(string s)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) { var b = Encoding.UTF8.GetBytes(s); gz.Write(b, 0, b.Length); }
        return Convert.ToBase64String(ms.ToArray());
    }

    static string Unzip(string s)
    {
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(s)), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd();
    }
}

// One drone edit, client -> host. Simple public get/set properties: SteamNetworkLib's IL2CPP-safe JSON.
public class DroneCmd
{
    public int Op { get; set; }
    public string DroneId { get; set; } = "";
    public string Arg { get; set; } = "";
}

public class DroneCmdMessage : TypedP2PMessage<DroneCmd>
{
    public override string MessageType => "DRONECOURIER_CMD";
    public DroneCmdMessage() { }
    public DroneCmdMessage(DroneCmd payload) : base(payload) { }
}
