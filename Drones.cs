using System;
using System.Collections.Generic;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using Il2CppScheduleOne.Levelling;

namespace DroneCourier;

public enum DroneSize { Small, Medium, Large, ExtraLarge, Huge } // saved as numbers: only append
public enum Trigger { WhenFull, WhenStacks, Every }  // when the drone leaves the sender
public enum Mode { Off, Loop, Once }
public enum Leg { AtSender, ToReceiver, Unloading, Returning }

public record DroneSpec(string Name, int Stacks, float Price, ERank Rank)
{
    public string RankName => Rank.ToString().Replace('_', ' ');
}

public class Drone
{
    public string Id = Guid.NewGuid().ToString("N");
    public DroneSize Size;
    public int Number;                    // "Small drone #2"
    public string Sender, Receiver;       // GUIDs of the player's storages
    public Trigger Trigger = Trigger.WhenFull;
    public int TriggerStacks = 1;         // WhenStacks: leave with at least this many stacks
    public int EveryMinutes = 60;         // Every: in-game minutes (1 per real second)
    public Mode Mode;
    public Leg Leg;
    public float LegSecondsLeft;          // flight/unload time left on the current leg
    public float SinceDeparture;          // seconds since the last departure (for Every)
    public List<string> Cargo = new();    // goods in flight, as the game's own item JSON
    public int Trips;                     // completed trips (shown in the app)
    public float Paid;                    // what the player paid (refund base); 0 = bought before this was saved
    public List<string> Items = new();    // item IDs this drone takes from the sender; empty = everything the other drones there don't take

    public bool Takes(string itemId)
    {
        if (Items.Count > 0) return Items.Contains(itemId);
        foreach (var o in Drones.Instance.Owned) if (o != this && o.Sender == Sender && o.Items.Contains(itemId)) return false;
        return true;
    }

    public bool SharesSender() => Sender != null && Drones.Instance.Owned.Exists(o => o != this && o.Sender == Sender);

    /// Another drone on the same sender that would race for the same stacks (both "the rest", or a shared item), or null.
    public Drone Conflict(string sender, List<string> items)
    {
        if (sender == null) return null;
        foreach (var o in Drones.Instance.Owned)
            if (o != this && o.Sender == sender && (items.Count == 0 && o.Items.Count == 0 || o.Items.Exists(items.Contains))) return o;
        return null;
    }

    // Not saved: computed. Saving Spec let the JSON loader write old saved prices INTO the shared price table.
    [Newtonsoft.Json.JsonIgnore] public DroneSpec Spec => Drones.Specs[Size];
    public string CustomName;             // set in the app; null = "Small drone #2"
    [Newtonsoft.Json.JsonIgnore] public string Name => string.IsNullOrWhiteSpace(CustomName) ? $"{Spec.Name} #{Number}" : CustomName;
}

// One per save file. S1API discovers Saveable subclasses, creates the single instance and saves its fields.
public class Drones : Saveable
{
    // One drone per storage size: stacks = slots of the matching vanilla rack/closet (read from the game's registry).
    // ponytail: fixed table, see idea.md; promote to MelonPreferences if people want to tune it
    public static readonly Dictionary<DroneSize, DroneSpec> Specs = new()
    {
        [DroneSize.Small] = new("Small drone", 4, 6500f, ERank.Peddler),                // small rack
        [DroneSize.Medium] = new("Medium drone", 6, 10000f, ERank.Hustler),             // medium rack, small closet
        [DroneSize.Large] = new("Large drone", 8, 15000f, ERank.Bagman),               // large rack, medium closet
        [DroneSize.ExtraLarge] = new("Extra large drone", 12, 25000f, ERank.Enforcer), // large closet
        [DroneSize.Huge] = new("Huge drone", 20, 45000f, ERank.Shot_Caller),          // huge closet
    };

    public static Drones Instance { get; private set; }

    [SaveableField("drones")] public List<Drone> Owned = new();

    public Drones() => Instance = this;

    public Drone Add(DroneSize size)
    {
        int n = 1;
        foreach (var d in Owned) if (d.Size == size) n++;
        var drone = new Drone { Size = size, Number = n, Paid = Specs[size].Price };
        Owned.Add(drone);
        return drone;
    }

    // S1API keeps one instance per game session: a save with no drone data must not inherit the last save's (or host's) drones.
    protected override void OnCreated() => Owned = new();

    protected override void OnLoaded()
    {
        Courier.OnLoad();
        Mod.Log($"loaded {Owned.Count} drone(s)");
    }
}
