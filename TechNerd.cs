using S1API.Entities;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Money;
using UnityEngine;

namespace DroneCourier;

// The drone vendor. S1API spawns him (never `new` him); built only from the game's own avatar parts.
public sealed class TechNerd : NPC
{
    const string Talk = "DroneShop";
    // In the warehouse (dark market), picked in game by the player; y = the floor (navmesh), where Oscar stands too
    internal static readonly Vector3 Spot = new(-62.12f, -2.10f, 28.81f);
    internal static readonly Quaternion Facing = Quaternion.Euler(0f, 60f, 0f);

    public override bool IsPhysical => true;

    protected override void ConfigurePrefab(NPCPrefabBuilder builder)
    {
        builder.WithIdentity("dronecourier_technerd", "Milo", "Byte") // stable save ID: never change it
            .WithSpawnPosition(Spot, Facing)
            .WithRelationshipDefaults(r => r.WithDelta(1.0f).SetUnlocked(true).SetUnlockType(NPCRelationship.UnlockType.DirectApproach));
        // No schedule: he stands at his counter like a shopkeeper (KeepInPlace puts him back if anything moves him).
    }

    protected override void OnCreated()
    {
        base.OnCreated();
        Dress(Appearance).Build();
        Schedule.Disable(); // the game's "Follow Schedule" kept walking him off to a default spot; he stays at his counter

        Dialogue.BuildAndRegisterContainer(Talk, c =>
        {
            c.AddNode("ENTRY", "Yo. You need stuff moved between your places? My drones can handle that.", ch => ch
                .Add("SHOP", "Show me the drones", "SHOP")
                .Add("HOW", "How does it work?", "HOW")
                .Add("LEAVE", "Nevermind", "EXIT"));
            c.AddNode("HOW", "Easy. Open the Drones app, pick a shelf to load from and a shelf to drop at. " +
                             "The drone does the rest. You pay once, it's yours.", ch => ch
                .Add("SHOP2", "Show me the drones", "SHOP")
                .Add("LEAVE2", "Got it", "EXIT"));
            c.AddNode("SHOP", "Cash only. Bigger drone, bigger loads.", ch =>
            {
                foreach (var (size, spec) in Drones.Specs)
                    ch.Add("BUY_" + size, $"{spec.Name} ({spec.Stacks} stacks) - ${spec.Price:N0} [{spec.RankName}]", "SOLD");
                ch.Add("BACK", "Nevermind", "EXIT");
            });
            c.AddNode("SOLD", "Nice doing business. It's in your Drones app.");
            c.AddNode("BROKE", "Come back when you're a little... mmmm... richer.");
            c.AddNode("FULL", $"You've got {Drones.Max} drones already. Give one back first.");
            foreach (var (size, spec) in Drones.Specs)
                c.AddNode("RANK_" + size, $"Not that one, not yet. Come back when you're a {spec.RankName}.");
            c.AddNode("EXIT", "Later.");
        });
        foreach (var size in Drones.Specs.Keys)
        {
            var s = size; // captured per choice
            Dialogue.OnChoiceSelected("BUY_" + s, () => Buy(s));
        }
        Dialogue.UseContainerOnInteract(Talk);
    }

    // Only paths that exist in the game's Resources (checked in resources.assets; there is no hoodie)
    static S1API.Entities.NPCAppearance Dress(S1API.Entities.NPCAppearance a) => a
            .Set<Gender>(0.1f)
            .Set<Height>(0.95f)
            .Set<Weight>(0.2f)
            .Set<SkinColor>(new Color32(206, 160, 125, 255))
            .Set<HairStyle>("Avatar/Hair/BowlCut/BowlCut")
            .Set<HairColor>(new Color(0.2f, 0.13f, 0.08f))
            .Set<EyeBallTint>(Color.white)
            .Set<PupilDilation>(0.6f)
            .Set<EyeLidRestingStateLeft>((0.5f, 0.5f))
            .Set<EyeLidRestingStateRight>((0.5f, 0.5f))
            .Set<EyebrowScale>(0.9f)
            .Set<EyebrowThickness>(0.6f)
            .WithFaceLayer<Face>("Avatar/Layers/Face/Face_SlightSmile", Color.black)
            .WithFaceLayer<Eyes>("Avatar/Layers/Face/TiredEyes", new Color(0.3f, 0.2f, 0.2f))
            .WithBodyLayer<Shirts>("Avatar/Layers/Top/T-Shirt", new Color(0.12f, 0.12f, 0.16f))
            .WithBodyLayer<Pants>("Avatar/Layers/Bottom/CargoPants", new Color(0.35f, 0.33f, 0.25f))
            .WithAccessoryLayer<Head>("Avatar/Accessories/Head/SmallRoundGlasses/SmallRoundGlasses", new Color(0.1f, 0.1f, 0.1f))
            .WithAccessoryLayer<Feet>("Avatar/Accessories/Feet/Sneakers/Sneakers", Color.white);

    // Host only, every 0.5 s: keep him at his spot (an older save or the schedule may leave him elsewhere).
    // ponytail: brute-force warp; log shows if something keeps moving him
    internal static void KeepInPlace()
    {
        if (!Il2CppFishNet.InstanceFinder.IsServer || Get<TechNerd>() is not TechNerd m || m.gameObject == null) return;
        if (m.Schedule.IsEnabled) m.Schedule.Disable();
        if (Vector3.Distance(m.Position, Spot) < 1.5f) return;
        Mod.Log($"Milo was at {m.Position}, warping him back to the warehouse");
        MoveTo(m, Spot);
        m.Transform.rotation = Facing;
    }

    // Every client, every few seconds: S1API applies his outfit only when his portrait capture finishes; if that fails
    // he stays in underwear (seen once). Re-apply it once ~5-10 s after he spawns. Reflection: ApplyToAvatar is internal.
    // ponytail: S1API 3.2.3 internals; drop this if a newer S1API applies appearance reliably
    static System.IntPtr dressedFor;
    static int checks;
    internal static void Redress()
    {
        if (Get<TechNerd>() is not TechNerd m || m.gameObject == null) return;
        if (m.gameObject.Pointer != dressedFor) { dressedFor = m.gameObject.Pointer; checks = 0; }
        ++checks;
        if (checks == 3) { CropPortrait(m); return; } // 5 s after the re-render below
        if (checks != 2) return;
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var t = m.Appearance.GetType();
        var avatar = t.GetField("_runtimeAvatar", Any)?.GetValue(m.Appearance);
        var apply = t.GetMethod("ApplyToAvatar", Any);
        if (avatar == null || apply == null) { Mod.Log($"Milo outfit: can't re-apply (avatar={avatar != null}, method={apply != null})"); return; }
        // S1API can swap in a fresh (blank) appearance object after OnCreated: his portrait was then taken bald and
        // naked. Re-apply the whole look to the current object, show it, and render the portrait again.
        Dress(m.Appearance);
        apply.Invoke(m.Appearance, new[] { avatar });
        t.GetField("_mugshotCompleted", Any)?.SetValue(m.Appearance, false);
        t.GetField("_mugshotQueued", Any)?.SetValue(m.Appearance, false);
        t.GetMethod("GenerateMugshot", Any)?.Invoke(m.Appearance, null);
        m.ClearConversationCategories(); // no C/S/D badge in Messages (he's a shopkeeper, like Uncle Nelson)
        Mod.Log("Milo outfit + portrait re-applied");
    }

    // The game's contact icons are a close-up of the face; S1API's render is head and shoulders. Crop to the face
    // (fractions of the render, picked from the 512 px portrait) and set it as his icon, which also stops re-renders.
    static void CropPortrait(TechNerd m)
    {
        var tex = m.Icon?.texture;
        if (tex == null) { Mod.Log("Milo portrait: no icon to crop"); return; }
        float w = tex.width, h = tex.height;
        m.Icon = Sprite.Create(tex, new Rect(w * 0.12f, h * 0.19f, w * 0.66f, h * 0.66f), new Vector2(0.5f, 0.5f), 100f);
        Mod.Log($"Milo portrait cropped to the face ({tex.width}x{tex.height} source)");
    }

    static void MoveTo(TechNerd m, Vector3 p)
    {
        // He never walks: switch his NavMeshAgent off (it kept snapping him back to its old position) and place him directly.
        var npc = m.gameObject.GetComponent<Il2CppScheduleOne.NPCs.NPC>();
        var agent = npc?.Movement._agent;
        if (agent != null) agent.enabled = false;
        m.Transform.SetPositionAndRotation(p, Facing);
        Mod.Log($"Milo placed at {p}: now at {m.Position}, agent off={(agent != null && !agent.enabled)} behaviour={npc?.Behaviour.activeBehaviour?.Name}");
    }

    public const float RefundShare = 0.5f;
    public static float Refund(Drone d) => Mathf.Round((d.Paid > 0f ? d.Paid : d.Spec.Price) * RefundShare);

    // "Stop using this drone" in the app: the drone goes back to Milo, who leaves part of its price as cash in a dead drop
    // and texts where. Same pick as supplier deliveries (an empty drop, not the nearest): it never lands in a drop that
    // holds a supplier's goods, and suppliers skip it while the cash is there (they only use empty drops).
    public static void Retire(Drone d)
    {
        float back = Refund(d);
        Courier.Forget(d);
        Drones.Instance.Owned.Remove(d);
        string where = null;
        try
        {
            var me = Il2CppScheduleOne.PlayerScripts.Player.Local;
            var drop = Il2CppScheduleOne.Economy.DeadDrop.GetRandomEmptyDrop(me != null ? me.transform.position : Vector3.zero);
            if (drop != null)
            {
                var money = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
                drop.Storage.InsertItem(money.GetCashInstance(back), true); // networked, like a supplier's delivery
                Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Quests.QuestManager>.Instance
                    .CreateDeaddropCollectionQuest(null, drop.GUID.ToString(), System.Guid.NewGuid().ToString()); // waypoint, like suppliers
                where = drop.DeadDropDescription;
            }
        }
        catch (System.Exception e) { Mod.Log($"dead drop refund failed: {e.Message}"); } // vanilla picker throws when no drop is empty
        if (where == null) Money.CreateOnlineTransaction("Drone refund (Milo)", back, 1f, d.Name); // no free dead drop: bank instead
        // Vanilla drop texts read "...that drop is ready <LOCATION>" (the description starts with "in"/"behind"/...)
        string msg = $"No worries, I'll take the {d.Name.ToLower()} back. ";
        msg += where != null ? $"Left ${back:N0} for you {where}." : $"All the drops are full, so I sent ${back:N0} to your bank.";
        if (Get<TechNerd>() is TechNerd m) m.SendTextMessage(msg);
        Mod.Log($"retired {d.Name}, refunded ${back} {(where != null ? "in dead drop " + where : "to the bank")}");
    }

    void Buy(DroneSize size)
    {
        var spec = Drones.Specs[size];
        var lm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Levelling.LevelManager>.Instance;
        if (lm != null && lm.Rank < spec.Rank) { Dialogue.JumpTo(Talk, "RANK_" + size); return; }
        if (Drones.Instance != null && Drones.Instance.Owned.Count >= Drones.Max) { Dialogue.JumpTo(Talk, "FULL"); return; } // checked before taking the cash
        if (Money.GetCashBalance() < spec.Price) { Dialogue.JumpTo(Talk, "BROKE"); return; }
        Money.ChangeCashBalance(-spec.Price, visualizeChange: true, playCashSound: true);
        Net.Do(null, Op.Buy, ((int)size).ToString()); // host adds it now; a co-op client asks the host
        Mod.Log($"bought a {spec.Name} for ${spec.Price}; now {Drones.Instance.Owned.Count} drone(s)");
    }
}

// The game turns an NPC's NavMeshAgent back on whenever he becomes visible (server). The agent then snaps Milo to the
// nearest walkable point, ~3 m off his counter (his spot isn't on the Humanoid navmesh). Switch it off again, put him back.
[HarmonyLib.HarmonyPatch(typeof(Il2CppScheduleOne.NPCs.NPCMovement), nameof(Il2CppScheduleOne.NPCs.NPCMovement.OnVisibilityChange))]
static class MiloStaysPut
{
    static void Postfix(Il2CppScheduleOne.NPCs.NPCMovement __instance)
    {
        if (NPC.Get<TechNerd>() is not TechNerd m || m.gameObject == null || __instance._npc == null) return;
        if (__instance._npc.gameObject.Pointer != m.gameObject.Pointer) return;
        if (__instance._agent != null) __instance._agent.enabled = false;
        m.Transform.SetPositionAndRotation(TechNerd.Spot, TechNerd.Facing);
    }
}
