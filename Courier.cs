using System.Collections.Generic;
using Il2CppFishNet;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence;
using UnityEngine;
using Property = Il2CppScheduleOne.Property.Property;

namespace DroneCourier;

// The drones' work, host only, once a second. Items move with the game's own storage calls, so storage
// contents sync to other players like vanilla. Drones are invisible: a trip is just time passing.
static class Courier
{
    public const float LoadSeconds = 10f, UnloadSeconds = 10f, MetersPerSecond = 10f;

    public record Storage(string Guid, string Label, PlaceableStorageEntity Entity, string Place);

    // Only racks and storage closets (the game also counts tables, beds, safes, lockers, trash cans as storage)
    static readonly HashSet<string> Kinds = new()
    {
        "smallstoragerack", "mediumstoragerack", "largestoragerack",
        "smallstoragecloset", "mediumstoragecloset", "largestoragecloset", "hugestoragecloset",
    };

    /// Every rack/closet in the player's properties, labelled "Property · Name" (numbered when names repeat).
    // Cached per frame: Find runs for every drone and app row each second, and each scan walks every placed object.
    static List<Storage> cache;
    static int cacheFrame = -1;

    public static List<Storage> All()
    {
        if (cacheFrame == Time.frameCount && cache != null) return cache;
        var list = new List<Storage>();
        foreach (var prop in Property.OwnedProperties)
        {
            // Sorted by GUID: the game's BuildableItems order differs between host and clients, so "#2" must not depend on it
            var found = new List<PlaceableStorageEntity>();
            foreach (var b in prop.BuildableItems)
            {
                var s = b?.TryCast<PlaceableStorageEntity>();
                if (s != null && Kinds.Contains(s.ItemInstance?.ID ?? "")) found.Add(s);
            }
            found.Sort((a, b) => string.CompareOrdinal(a.GUID.ToString(), b.GUID.ToString()));
            var seen = new Dictionary<string, int>();
            foreach (var s in found)
            {
                string name = s.Name;
                seen[name] = seen.TryGetValue(name, out int n) ? n + 1 : 1;
                list.Add(new(s.GUID.ToString(), $"{prop.PropertyName} · {name}{(seen[name] > 1 ? $" #{seen[name]}" : "")}", s, prop.PropertyCode));
            }
        }
        cacheFrame = Time.frameCount;
        return cache = list;
    }

    public static Storage Find(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        foreach (var s in All()) if (s.Guid == guid) return s;
        return null; // sold, picked up or destroyed
    }

    /// Item IDs currently in a storage, in slot order, no repeats (choices for a drone's item filter).
    public static List<string> ItemsIn(string guid)
    {
        var ids = new List<string>();
        var slots = Find(guid)?.Entity.StorageEntity.ItemSlots;
        for (int i = 0; slots != null && i < slots.Count; i++)
        {
            var id = slots[i].ItemInstance?.ID;
            if (slots[i].Quantity > 0 && id != null && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    public static string ItemName(string id) => Il2CppScheduleOne.Registry.GetItem(id)?.Name ?? id;
    public static Sprite ItemIcon(string id) => Il2CppScheduleOne.Registry.GetItem(id)?.Icon;

    public static void Tick(float dt)
    {
        if (!InstanceFinder.IsServer || Drones.Instance == null) return;
        foreach (var d in Drones.Instance.Owned) Step(d, dt);
    }

    // Receiver slots each carrying drone has locked, like workers do ("about to place an item here").
    // Runtime only: locks aren't saved, so they are re-applied on the first tick after a load.
    static readonly Dictionary<Drone, (PlaceableStorageEntity at, List<ItemSlot> slots)> locks = new();
    static readonly HashSet<Drone> noRoom = new(); // ready to leave but the receiver lacks free slots (shown in the app)

    /// A load replaces the Drone objects: forget runtime state tied to the old ones (their storages are gone too).
    public static void OnLoad() { locks.Clear(); noRoom.Clear(); }

    static void Step(Drone d, float dt)
    {
        d.SinceDeparture += dt;
        if (d.Leg == Leg.AtSender && d.Mode == Mode.Off) return;
        var from = Find(d.Sender); var to = Find(d.Receiver);
        bool carrying = d.Leg is Leg.ToReceiver or Leg.Unloading;
        // A new trip needs both; carrying needs the receiver (cargo waits until one is set); flying back empty needs neither.
        if (d.Leg == Leg.AtSender ? from == null || to == null : carrying && to == null) { Unlock(d); noRoom.Remove(d); return; }
        if (locks.TryGetValue(d, out var held) && (!carrying || held.at.Pointer != to.Entity.Pointer)) Unlock(d); // receiver changed
        if (carrying && !locks.ContainsKey(d)) locks[d] = (to.Entity, Reserve(to.Entity, d.Cargo));
        if (d.LegSecondsLeft > 0f) { d.LegSecondsLeft -= dt; return; }
        float flight = from == null || to == null ? 0f : Vector3.Distance(from.Entity.transform.position, to.Entity.transform.position) / MetersPerSecond;

        switch (d.Leg)
        {
            case Leg.AtSender:
                int want = WantToTake(d, from.Entity);
                noRoom.Remove(d);
                if (want <= 0) return;
                if (FreeSlots(to.Entity) < want) { noRoom.Add(d); return; } // don't fly until the receiver has a free slot per stack
                Take(d, from.Entity, want, d.Cargo);
                locks[d] = (to.Entity, Reserve(to.Entity, d.Cargo)); // lock them now so nothing takes them during the flight
                d.SinceDeparture = 0f;
                d.Leg = Leg.ToReceiver; d.LegSecondsLeft = LoadSeconds + flight;
                Mod.Log($"{d.Name}: took {d.Cargo.Count} stack(s) from {from.Label}");
                break;
            case Leg.ToReceiver:
                d.Leg = Leg.Unloading; d.LegSecondsLeft = UnloadSeconds;
                break;
            case Leg.Unloading:
                Unlock(d); // insert skips locked slots; leftovers get re-reserved next tick
                Unload(to.Entity, d.Cargo);
                if (d.Cargo.Count > 0) { d.LegSecondsLeft = 5f; return; } // receiver full: retry in 5 s
                d.Leg = Leg.Returning; d.LegSecondsLeft = flight;
                Mod.Log($"{d.Name}: delivered to {to.Label}");
                break;
            case Leg.Returning:
                d.Trips++;
                d.Leg = Leg.AtSender;
                if (d.Mode == Mode.Once) d.Mode = Mode.Off;
                break;
        }
    }

    /// How many stacks to take now (0 = wait), following the drone's trigger.
    static int WantToTake(Drone d, PlaceableStorageEntity from)
    {
        var slots = from.StorageEntity.ItemSlots;
        int stacks = 0;
        for (int i = 0; i < slots.Count; i++) if (Takeable(d, slots[i])) stacks++;
        int cap = d.Spec.Stacks;
        return d.Trigger switch
        {
            // the sender rack/closet is full (no empty slot): take a drone load; in Loop mode it goes again each time it fills up
            Trigger.WhenFull => Full(slots) && stacks > 0 ? Mathf.Min(stacks, cap) : 0,
            Trigger.WhenStacks => stacks >= Mathf.Min(d.TriggerStacks, cap) ? Mathf.Min(stacks, cap) : 0,
            _ => d.SinceDeparture >= d.EveryMinutes && stacks > 0 ? Mathf.Min(stacks, cap) : 0,
        };
    }

    static int FreeSlots(PlaceableStorageEntity s)
    {
        var slots = s.StorageEntity.ItemSlots;
        int n = 0;
        for (int i = 0; i < slots.Count; i++)
            if ((slots[i].ItemInstance == null || slots[i].Quantity <= 0) && !slots[i].IsLocked && !slots[i].IsAddLocked) n++;
        return n;
    }

    static bool Full(Il2CppSystem.Collections.Generic.List<ItemSlot> slots)
    {
        for (int i = 0; i < slots.Count; i++) if (slots[i].ItemInstance == null || slots[i].Quantity <= 0) return false;
        return true;
    }

    static bool Takeable(Drone d, ItemSlot s) => s.ItemInstance != null && s.Quantity > 0 && !s.IsLocked && !s.IsRemovalLocked && d.Takes(s.ItemInstance.ID);

    static void Take(Drone d, PlaceableStorageEntity from, int stacks, List<string> cargo)
    {
        var slots = from.StorageEntity.ItemSlots;
        for (int i = 0; i < slots.Count && stacks > 0; i++)
        {
            var s = slots[i];
            if (!Takeable(d, s)) continue;
            cargo.Add(s.ItemInstance.GetCopy(s.Quantity).GetItemData().GetJson(false));
            s.ChangeQuantity(-s.Quantity); // empties the slot, networked
            stacks--;
        }
    }

    static List<ItemSlot> Reserve(PlaceableStorageEntity to, List<string> cargo)
    {
        var slots = to.StorageEntity.ItemSlots;
        var mine = new List<ItemSlot>();
        foreach (var json in cargo)
        {
            var item = ItemDeserializer.LoadItem(json);
            if (item == null) continue;
            int left = item.Quantity;
            for (int i = 0; i < slots.Count && left > 0; i++)
            {
                var s = slots[i];
                if (s.IsLocked || s.IsAddLocked) continue;
                int cap = s.GetCapacityForItem(item);
                if (cap <= 0) continue;
                s.ApplyLock(to.NetworkObject, "A drone is about to place an item here"); // networked
                mine.Add(s);
                left -= cap;
            }
        }
        return mine;
    }

    /// A drone leaves the fleet: drop its runtime state (slot locks, waiting flag).
    public static void Forget(Drone d) { Unlock(d); noRoom.Remove(d); }

    static void Unlock(Drone d)
    {
        if (!locks.Remove(d, out var held)) return;
        // Only our own lock: a worker may have locked the slot since (its reservation), and RemoveLock clears any lock
        if (held.at == null) return; // storage picked up or destroyed: its slots (and locks) are gone with it
        var owner = held.at.NetworkObject;
        foreach (var s in held.slots)
            if (s != null && s.ActiveLock != null && owner != null && s.ActiveLock.LockOwner != null && s.ActiveLock.LockOwner.Pointer == owner.Pointer)
                s.RemoveLock();
    }

    // Puts what fits into the receiver; whatever doesn't fit stays in the cargo.
    static void Unload(PlaceableStorageEntity to, List<string> cargo)
    {
        var slots = to.StorageEntity.ItemSlots;
        for (int i = cargo.Count - 1; i >= 0; i--)
        {
            var item = ItemDeserializer.LoadItem(cargo[i]);
            if (item == null) { Mod.Log($"dropped unreadable cargo: {cargo[i]}"); cargo.RemoveAt(i); continue; }
            int left = Insert(slots, item);
            if (left == item.Quantity) continue;
            if (left == 0) cargo.RemoveAt(i);
            else cargo[i] = item.GetCopy(left).GetItemData().GetJson(false);
        }
    }

    // Not StorageEntity.InsertItem: in 0.4.7f12 it SUBTRACTS from a matching stack (ChangeQuantity(-n)) and puts the
    // whole item in the first empty slot, so delivering onto a partial stack destroyed goods. Per slot instead:
    // matching stacks first, then empty slots, skipping locked ones. Returns how many didn't fit.
    static int Insert(Il2CppSystem.Collections.Generic.List<ItemSlot> slots, ItemInstance item)
    {
        int left = item.Quantity;
        for (int pass = 0; pass < 2 && left > 0; pass++)
            for (int i = 0; i < slots.Count && left > 0; i++)
            {
                var s = slots[i];
                if (s.IsLocked || s.IsAddLocked || (s.ItemInstance == null) != (pass == 1)) continue;
                int n = Mathf.Min(left, s.GetCapacityForItem(item, false));
                if (n <= 0) continue;
                s.AddItem(item.GetCopy(n), false); // networked: stacks with ChangeQuantity(+n), fills an empty slot with SetStoredItem
                left -= n;
            }
        return left;
    }

    /// Carrying goods but the receiver is cleared or gone: the drone waits with them.
    public static bool Holding(Drone d) => d.Leg is Leg.ToReceiver or Leg.Unloading && d.Cargo.Count > 0 && Find(d.Receiver) == null;

    /// Item id of one cargo stack (the game's item JSON).
    public static string CargoId(string json) => ItemDeserializer.LoadItem(json)?.ID ?? "?";

    public static string Status(Drone d)
    {
        if (Net.Status(d) is string remote) return remote; // co-op client: the host runs the trips
        string t = Mathf.CeilToInt(Mathf.Max(0f, d.LegSecondsLeft)) + " s";
        if (Holding(d)) return $"Holding {d.Cargo.Count} stack{(d.Cargo.Count == 1 ? "" : "s")}: set a receiver";
        return d.Leg switch
        {
            Leg.ToReceiver => $"Flying to receiver ({t})",
            Leg.Unloading => d.Cargo.Count > 0 && d.LegSecondsLeft <= 0f ? "Waiting: receiver is full" : $"Unloading ({t})",
            Leg.Returning => $"Flying back ({t})",
            _ => d.Mode == Mode.Off ? "Stopped" : d.Sender == null || d.Receiver == null ? "Needs sender and receiver"
                : noRoom.Contains(d) ? "Waiting: receiver has no free slots" : "Waiting at sender",
        };
    }
}
