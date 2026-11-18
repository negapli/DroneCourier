using MelonLoader;

[assembly: MelonInfo(typeof(DroneCourier.Mod), "DroneCourier", "0.1.7", "pligane")]
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
        float now = UnityEngine.Time.time;
        if (now >= nextPlace) { nextPlace = now + 0.5f; Safe(TechNerd.KeepInPlace, "Milo"); } // fast: after a load he spawns at 0,0,0 until we move him
        if (now >= nextCheck) { nextCheck = now + 5f; Safe(TechNerd.Redress, "Milo outfit"); }
        Safe(WorldPicker.Update, "picker");
        Safe(Net.Update, "net");
        Safe(DronesApp.TypingUpdate, "app typing");
        if (now >= nextTick) { nextTick = now + 1f; Safe(() => Courier.Tick(1f), "trips"); Safe(Net.Tick, "sync"); Safe(DronesApp.Refresh, "app"); }
    }

    // One failing part must not stop the others (an exception ends OnUpdate). Logged once a minute per part, not every frame.
    static readonly System.Collections.Generic.Dictionary<string, float> lastError = new();
    static void Safe(System.Action a, string part)
    {
        try { a(); }
        catch (System.Exception e)
        {
            float now = UnityEngine.Time.time;
            if (lastError.TryGetValue(part, out var t) && now - t < 60f) return;
            lastError[part] = now;
            Log($"error in {part}: {e}");
        }
    }
}
