using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Management;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.UI.Phone;
using UnityEngine;

namespace DroneCourier;

// Pick a drone's sender/receiver in the world, like the management clipboard: aim at a rack or storage
// closet (it gets outlined), click to choose, right click / Esc to cancel. Reuses the clipboard
// selector's outline colour and detection mask so it looks the same.
static class WorldPicker
{
    static Drone drone;
    static bool sender;
    static float startAt;
    static PlaceableStorageEntity hovered;
    // Esc goes through the game's exit listeners (like the clipboard selector, priority 12) so it is marked used
    // and the pause menu (last listener) doesn't also open.
    static readonly GameInput.ExitDelegate onExit = (System.Action<ExitAction>)(a => { if (Active) { a.Used = true; Cancel(); } });
    static ObjectSelector selector; // the clipboard's selector: exists in the scene even without a clipboard equipped

    public static bool Active => drone != null;

    public static void Start(Drone d, bool pickSender)
    {
        drone = d; sender = pickSender;
        startAt = Time.time + 0.3f; // ignore the click that closed the phone
        Singleton<GameplayMenu>.Instance?.Close(); // the game's own close: lowers and puts the phone away first: it can only pop while on top of the state stack (SetIsOpen alone left it in hand)
        if (selector == null) selector = UnityEngine.Object.FindObjectOfType<ObjectSelector>(true); // Unity null check (scene reload)
        if (selector == null) Mod.Log("WorldPicker: no ObjectSelector in scene");
        GameInput.RegisterExitListener(onExit, 12);
        selector?.State.PushToDefaultParent(); // same input state as the clipboard: no punching while picking
        Singleton<HUD>.Instance?.ShowTopScreenText($"Click a rack or storage closet to set the {(sender ? "SENDER" : "RECEIVER")} of {d.Name}  (right click: cancel)");
    }

    public static void Update()
    {
        if (drone == null || Time.time < startAt) return;
        // ponytail: only the storage menu is watched; add other menus (stations, dialogue) if they overlap too
        if (Singleton<StorageMenu>.Instance?.IsOpen == true) { End(reopenPhone: false); Mod.Notify("Drones", "Selection cancelled."); return; } // E on a storage: let its menu own Esc
        if (Input.GetMouseButtonDown(1)) { Cancel(); return; }

        var cam = PlayerSingleton<PlayerCamera>.Instance;
        PlaceableStorageEntity look = null; RaycastHit hit = default;
        if (selector != null && cam != null && cam.LookRaycast(5f, out hit, selector.DetectionMask, false, 0.1f))
            look = hit.collider.GetComponentInParent<PlaceableStorageEntity>();
        var valid = look == null ? null : Courier.Find(look.GUID.ToString()); // only the player's racks/closets
        string other = sender ? drone.Receiver : drone.Sender;
        var otherS = Courier.Find(other);
        string reason = valid == null || otherS == null ? null // sender and receiver: different storages in different properties
            : valid.Guid == otherS.Guid ? $"Already the {(sender ? "receiver" : "sender")}"
            : valid.Place == otherS.Place ? $"Same property as the {(sender ? "receiver" : "sender")}" : null;
        if (valid != null && sender && reason == null && drone.Conflict(valid.Guid, drone.Items) is { } o) // shared sender: items must not overlap
            reason = $"{o.Name} already takes {(o.Items.Count == 0 ? "the rest" : "the same items")} from it";
        if (reason != null) { Singleton<HUD>.Instance?.CrosshairText.Show(reason, new Color32(255, 125, 125, 255)); valid = null; }

        var now = valid?.Entity;
        if (hovered != null && (now == null || hovered.Pointer != now.Pointer)) { hovered.HideOutline(); hovered = null; }
        if (now != null && hovered == null) { hovered = now; hovered.ShowOutline(selector.HoverOutlineColor); }

        if (valid != null && Input.GetMouseButtonDown(0))
        {
            Net.Do(drone, sender ? Op.Sender : Op.Receiver, valid.Guid);
            Mod.Notify("Drones", $"{drone.Name} {(sender ? "sender" : "receiver")}: {valid.Label}");
            End();
            DronesApp.Refresh(); // show the pick now, not on the next 1 s tick
        }
    }

    static void Cancel() { End(); Mod.Notify("Drones", "Selection cancelled."); }

    static void End(bool reopenPhone = true)
    {
        GameInput.DeregisterExitListener(onExit); // safe inside the listener: the game stops iterating once Used
        if (hovered != null) { hovered.HideOutline(); hovered = null; }
        Singleton<HUD>.Instance?.HideTopScreenText();
        selector?.State.RemoveFromDefaultParent();
        if (reopenPhone) Singleton<GameplayMenu>.Instance?.Open(); // phone back up on the Drones app (after the selector state is gone, same stack rule)
        drone = null;
    }
}
