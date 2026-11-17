using MelonLoader;

[assembly: MelonInfo(typeof(DroneCourier.Mod), "DroneCourier", "0.1.0", "pligane")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DroneCourier;

// Buy drones from a vendor NPC; they move goods between your storages. See README.md.
// The NPC (TechNerd) and the save data (Drones) are discovered and created by S1API.
public class Mod : MelonMod
{
    public static void Log(string msg) => Melon<Mod>.Logger.Msg(msg);

    public static void Notify(string title, string text)
    {
        Log($"{title}: {text}");
        Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.NotificationsManager>.Instance?.SendNotification(title, text, null, 5f, true);
    }

    public override void OnDeinitializeMelon() => Net.Shutdown();

    float nextCheck, nextTick, nextPlace;
    public override void OnUpdate()
    {
        if (UnityEngine.Time.time >= nextPlace) { nextPlace = UnityEngine.Time.time + 0.5f; TechNerd.KeepInPlace(); } // fast: after a load he spawns at 0,0,0 until we move him
        if (UnityEngine.Time.time >= nextCheck) { nextCheck = UnityEngine.Time.time + 5f; TechNerd.Redress(); }
        WorldPicker.Update();
        Net.Update();
        DronesApp.TypingUpdate();
        if (UnityEngine.Time.time >= nextTick) { nextTick = UnityEngine.Time.time + 1f; Courier.Tick(1f); Net.Tick(); DronesApp.Refresh(); }

    }
}
