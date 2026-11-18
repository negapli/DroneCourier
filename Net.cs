using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Il2CppFishNet;
using Il2CppSteamworks;
using SteamNetworkLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DroneCourier;

public enum Op { Sender, Receiver, Trigger, Stacks, Every, Mode, Items, Retire, Buy, Rename } // sent as numbers: only append

// Multiplayer with SteamNetworkLib (ifBars, the S1API author; what other Schedule I mods use), over the game's Steam lobby.
// The host owns the drones (save, trips, refunds).
//  - host -> everyone: lobby data "dronecourier" = the drone list (gzip+base64 JSON), republished when it changes.
//  - client -> host: the client's own player data "dronecourier_cmds" = its unconfirmed edits, numbered (Seq) within a
//    random session id; the host applies the new ones in order and publishes the last Seq it handled per player in
//    lobby data "dronecourier_acks"; the client then drops those. No P2P: every SteamNetworkLib client reads ALL P2P
//    packets (channels 0-3), so two mods using P2P steal each other's messages.
// Every change to a drone goes through Do(): the host applies it, a client applies it locally (so the app reacts at
// once) and sends it to the host. Singleplayer = host with no lobby.
// The host checks every client edit (Valid) and only reads lobby members' data.
static class Net
{
    const string StateKey = "dronecourier", CmdsKey = "dronecourier_cmds", AcksKey = "dronecourier_acks";

    static SteamNetworkClient steam;
    public static bool IsClient => !InstanceFinder.IsServer && InstanceFinder.IsClient;
    static bool Online => steam != null && steam.IsInLobby;
    /// A co-op client can send edits (and should pay for a drone) only while it sees the host.
    public static bool CanReachHost => Online && steam.GetHostMember() != null;

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
            steam = c; // no ProcessIncomingMessages: no P2P here, and reading packets would take other mods' messages
            Mod.Log("net: SteamNetworkLib ready");
        }
    }

    public static void Shutdown() { steam?.Dispose(); steam = null; }

    // --- edits ---

    public static void Do(Drone d, Op op, string arg = "")
    {
        if (!IsClient) { Apply(d, op, arg); return; }
        string cmdId = Guid.NewGuid().ToString("N");
        if (op is not (Op.Retire or Op.Buy)) { Apply(d, op, arg); pending[d.Id] = (cmdId, UnityEngine.Time.time); } // optimistic, kept until the host confirms
        if (!Online || steam.GetHostMember() == null) { Mod.Log($"net: not in a lobby, {op} not sent"); return; }
        queue.Add(new DroneCmd { Seq = ++seq, Op = (int)op, DroneId = d?.Id ?? "", Arg = arg, CmdId = cmdId });
        if (queue.Count > MaxQueue) { Mod.Log($"net: {queue.Count} edits unconfirmed by the host, dropping the oldest"); queue.RemoveAt(0); }
        WriteQueue();
    }

    // --- client -> host queue ---

    const int MaxQueue = 30; // player data is small; the host normally confirms within a second
    static readonly string session = Guid.NewGuid().ToString("N").Substring(0, 8); // a restarted client starts over
    static int seq;
    static readonly List<DroneCmd> queue = new();
    static void WriteQueue() =>
        steam.SetMyData(CmdsKey, queue.Count == 0 ? "" : JsonConvert.SerializeObject(new CmdBatch { Session = session, Cmds = queue }));

    // Client: drop the edits the host has handled.
    static void ReadAck()
    {
        if (queue.Count == 0) return;
        var acks = steam.GetLobbyData(AcksKey);
        if (string.IsNullOrEmpty(acks)) return;
        var mine = (string)JObject.Parse(acks)[steam.LocalPlayerId.m_SteamID.ToString()];
        if (mine == null || !mine.StartsWith(session + ":") || !int.TryParse(mine.Substring(session.Length + 1), out int done)) return;
        if (queue.RemoveAll(c => c.Seq <= done) > 0) WriteQueue();
    }

    // Host: each member's session and last handled Seq.
    static readonly Dictionary<ulong, (string session, int seq)> handled = new();
    static void ReadCommands()
    {
        foreach (var m in steam.GetLobbyMembers())
        {
            if (m.IsLocalPlayer) continue;
            var data = steam.GetPlayerData(m.SteamId, CmdsKey);
            if (string.IsNullOrEmpty(data)) continue;
            CmdBatch batch;
            try { batch = JsonConvert.DeserializeObject<CmdBatch>(data); } catch { continue; }
            if (batch?.Cmds == null || string.IsNullOrEmpty(batch.Session)) continue;
            ulong who = m.SteamId.m_SteamID;
            if (!handled.TryGetValue(who, out var h) || h.session != batch.Session) h = (batch.Session, 0);
            foreach (var c in batch.Cmds)
            {
                if (c == null || c.Seq <= h.seq) continue;
                h.seq = c.Seq;
                OnCommand(c, m.SteamId);
            }
            handled[who] = h;
        }
        var acks = new JObject();
        foreach (var kv in handled) acks[kv.Key.ToString()] = $"{kv.Value.session}:{kv.Value.seq}";
        string a = acks.ToString(Formatting.None);
        if (a != lastAcks) { lastAcks = a; steam.SetLobbyData(AcksKey, a); }
    }
    static string lastAcks;

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
            case Op.Rename: arg = arg.Replace("<", "").Replace(">", "").Trim(); d.CustomName = arg == "" ? null : arg; break; // no rich-text tags from anyone
        }
    }

    // Host: an edit from a client (read from a lobby member's player data, so only members can send any), and
    // every value is checked as if the app hadn't (a modified client could send anything).
    static void OnCommand(DroneCmd c, CSteamID from)
    {
        try
        {
            if (!InstanceFinder.IsServer || Drones.Instance == null) return;
            var op = (Op)c.Op;
            string arg = c.Arg ?? "";
            var d = Drones.Instance.Owned.Find(x => x.Id == c.DroneId);
            if (d == null && op != Op.Buy) return; // drone gone meanwhile
            if (d != null) acked[d.Id] = c.CmdId ?? ""; // published with the list: the sender stops overriding it
            if (!Valid(d, op, arg)) { Mod.Log($"net: rejected {op} {d?.Name} \"{Clip(arg)}\" from {from.m_SteamID}"); return; }
            Mod.Log($"client {from.m_SteamID}: {op} {d?.Name} {arg}");
            Apply(d, op, arg);
        }
        catch (Exception e) { Mod.Log($"net: command failed: {e.Message}"); }
    }

    static string Clip(string s) { s = s.Replace('\n', ' ').Replace('\r', ' '); return s.Length > 40 ? s.Substring(0, 40) + "…" : s; } // one log line, short

    // The same limits the app enforces
    static bool Valid(Drone d, Op op, string arg)
    {
        bool num = int.TryParse(arg, out int n);
        switch (op)
        {
            case Op.Sender: case Op.Receiver:
                if (arg == "") return true;
                string other = op == Op.Sender ? d.Receiver : d.Sender;
                return arg != other && Courier.Find(arg) != null;
            case Op.Trigger: return num && Enum.IsDefined(typeof(Trigger), n);
            case Op.Mode: return num && Enum.IsDefined(typeof(Mode), n);
            case Op.Stacks: return num && n >= 1 && n <= d.Spec.Stacks;
            case Op.Every: return num && n >= 10 && n <= 24 * 60;
            case Op.Items:
                if (arg == "") return true;
                var ids = arg.Split(',');
                return ids.Length <= 64 && Array.TrueForAll(ids, id => Il2CppScheduleOne.Registry.GetItem(id) != null);
            case Op.Rename: return arg.Trim().Length <= DronesApp.NameLength && arg.IndexOfAny(new[] { '\n', '\r' }) < 0; // Apply strips rich-text tags
            case Op.Retire: return d.Cargo.Count == 0; // the app waits for delivery too: cargo would be lost
            case Op.Buy: return num && Enum.IsDefined(typeof(DroneSize), n) && Drones.Instance.Owned.Count < Drones.Max;
            default: return false;
        }
    }

    // --- client side ---

    // drone id -> our last edit, shown locally until the host's list carries its id (Steam lobby data can lag several seconds)
    static readonly Dictionary<string, (string id, float at)> pending = new();
    static readonly Dictionary<string, string> statuses = new(); // drone id -> host's status line

    /// The host's status line for a drone (clients don't run the trips), or null.
    public static string Status(Drone d) => IsClient && statuses.TryGetValue(d.Id, out var s) ? s : null;

    static string lastState;
    static void Receive()
    {
        var data = steam.GetLobbyData(StateKey);
        if (string.IsNullOrEmpty(data) || Drones.Instance == null) return;
        if (data == lastState && pending.Count == 0) return;
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
            string ack = (string)o["Ack"] ?? ""; o.Remove("Ack");
            var d = owned.Find(x => x.Id == id);
            if (d == null) owned.Add(d = new Drone());
            else if (pending.TryGetValue(id, out var p))
            {
                if (ack != p.id && UnityEngine.Time.time - p.at < 15f) continue; // host hasn't confirmed it yet: keep showing our edit
                pending.Remove(id); // confirmed (applied or rejected), or lost: the host's values win
            }
            JsonConvert.PopulateObject(o.ToString(), d, Replace);
            d.Repair();
        }
        owned.RemoveAll(d => !ids.Contains(d.Id));
        foreach (var id in new List<string>(pending.Keys)) if (!ids.Contains(id)) pending.Remove(id);
    }

    // Lists replaced, not appended to (Newtonsoft's default fills existing lists)
    static readonly JsonSerializerSettings Replace = new() { ObjectCreationHandling = ObjectCreationHandling.Replace };

    // --- host side ---

    static string lastPublished;
    static readonly Dictionary<string, string> acked = new(); // drone id -> last client command handled (host)
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
            o["Ack"] = acked.TryGetValue(d.Id, out var ack) ? ack : "";
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
        // A co-op client never loads the save, so S1API never creates Drones there: make the mirror of the host's list.
        // (If the client later loads its own save, S1API's instance replaces it via the constructor.)
        if (Drones.Instance == null && IsClient) new Drones();
        if (!Online) { lastPublished = lastState = lastAcks = null; handled.Clear(); return; } // a new lobby must get the list even if it didn't change
        if (Drones.Instance == null) return;
        try
        {
            if (InstanceFinder.IsServer) { Publish(); ReadCommands(); }
            else if (InstanceFinder.IsClient) { Receive(); ReadAck(); }
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

// One drone edit, client -> host (inside the client's CmdBatch player data).
public class DroneCmd
{
    public int Seq { get; set; }            // order within the client's session; the host handles each Seq once
    public int Op { get; set; }
    public string DroneId { get; set; } = "";
    public string Arg { get; set; } = "";
    public string CmdId { get; set; } = ""; // echoed back by the host as the drone's "Ack"
}

public class CmdBatch
{
    public string Session { get; set; } = "";
    public List<DroneCmd> Cmds { get; set; } = new();
}
