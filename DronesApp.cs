using System;
using System.Collections.Generic;
using S1API.PhoneApp;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityAction = UnityEngine.Events.UnityAction;

namespace DroneCourier;

// The "Drones" phone app (S1API discovers it). Three screens: drone list -> drone -> items.
// Right click / Esc goes back one screen, like the game's own apps; on the list it closes the app.
public class DronesApp : PhoneApp
{
    protected override string AppName => "DroneCourier";
    protected override string AppTitle => "Drones";
    protected override string IconLabel => "Drones";
    protected override string IconFileName => "DroneCourier.png"; // unused: IconSprite below wins
    protected override Sprite IconSprite => icon ??= LoadIcon();
    static Sprite icon;

    // droon.png is embedded in the DLL (csproj EmbeddedResource), so the mod stays a single file
    static Sprite LoadIcon()
    {
        using var s = typeof(DronesApp).Assembly.GetManifestResourceStream("DroneCourier.droon.png");
        if (s == null) { Mod.Log("app icon resource missing"); return null; }
        var ms = new System.IO.MemoryStream(); s.CopyTo(ms);
        var bytes = ms.ToArray();
        var tex = new Texture2D(2, 2);
        if (!tex.LoadImage(bytes)) return null;
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
    }

    // Palette
    static readonly Color Bg = new(0.07f, 0.08f, 0.10f), Header = new(0.10f, 0.11f, 0.14f), Card = new(0.13f, 0.15f, 0.19f),
        Btn = new(0.21f, 0.25f, 0.31f), Accent = new(0.16f, 0.62f, 0.52f), Danger = new(0.62f, 0.24f, 0.24f),
        Muted = new(0.60f, 0.64f, 0.70f), Flying = new(0.35f, 0.72f, 0.98f), Busy = new(0.40f, 0.85f, 0.50f),
        Waiting = new(0.98f, 0.75f, 0.30f), Bad = new(0.95f, 0.45f, 0.45f);

    enum Screen { List, Drone, Items }

    static DronesApp instance;
    static Sprite round;  // rounded-corner sprite for cards and buttons (made once, sliced)
    static Font font;     // the game's UI font when one is found, else Arial

    RectTransform body;
    ScrollRect scroll;
    Text title;
    GameObject back;
    Screen screen;
    Drone shown;
    bool wasOpen;
    Drone confirmStop;
    InputField nameField; // drone screen: rename
    static bool typing;   // we told the game the player is typing (no walking/hotbar while the name field is focused) // first tap on "Stop using this drone" arms it, second tap confirms
    float? keepY; // scroll position to restore when the same screen is redrawn (a tap on +/-, an item toggle)
    string listSig;
    Text status, sender, receiver; // drone screen lines updated in place
    readonly List<(Drone d, Text status, Text route, Image strip)> rows = new();

    protected override void OnCreatedUI(GameObject container)
    {
        instance = this;
        font ??= FindGameFont();
        var root = UIFactory.Panel("Root", container.transform, Bg, fullAnchor: true);

        var head = UIFactory.Panel("Header", root.transform, Header, new Vector2(0f, 0.88f), Vector2.one);
        Fill(head);
        var hl = head.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset(16, 16, 8, 8); hl.spacing = 12;
        hl.childControlWidth = hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
        back = Button(head.transform, "<  Back", Back, Btn, 130).transform.parent.gameObject;
        title = Label(head.transform, "Drones", 26, Color.white, FontStyle.Bold);
        title.GetComponent<LayoutElement>().flexibleWidth = 1f;

        var area = new GameObject("Body");
        area.transform.SetParent(root.transform, false);
        var art = area.AddComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = new Vector2(1f, 0.88f); art.offsetMin = art.offsetMax = Vector2.zero;
        body = UIFactory.ScrollableVerticalList("List", area.transform, out scroll);
        body.sizeDelta = new Vector2(0f, body.sizeDelta.y); // S1API leaves +100 px width: content overflowed the screen
        var vl = body.GetComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset(18, 18, 14, 18); vl.spacing = 12;
        ShowList();
    }

    // Right click / Esc: back one screen (the game's Messages app does the same with a conversation open).
    public override void Exit(ExitAction exit)
    {
        var phone = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
        if (!exit.Used && nameField != null && nameField.isFocused) { exit.Used = true; nameField.DeactivateInputField(); return; } // Esc/right click: stop typing
        if (!exit.Used && screen != Screen.List && IsOpen() && phone != null && phone.IsOpen) { exit.Used = true; Back(); return; }
        base.Exit(exit);
    }

    // Every frame: while the name field has focus the game must ignore WASD/hotbar keys (its own flag for text fields).
    // Losing focus (Enter, click elsewhere) saves the name.
    public static void TypingUpdate()
    {
        var f = instance?.nameField;
        bool now = f != null && f.isFocused;
        // Phone put away (storage pick, phone closed) with the field focused: it kept eating ZQSD/WASD and blocked moving
        var phone = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
        if (now && (!instance.IsOpen() || phone == null || !phone.IsOpen)) { f.DeactivateInputField(); now = false; }
        if (now == typing) return;
        typing = now;
        Il2CppScheduleOne.GameInput.IsTyping = now;
        if (!now) instance?.SaveName();
    }

    void SaveName()
    {
        if (shown == null || nameField == null) return;
        string name = nameField.text.Trim();
        if (name == (shown.CustomName ?? "")) return;
        Net.Do(shown, Op.Rename, name);
        ShowDrone(shown);
    }

    void Back() { if (screen == Screen.Items && shown != null) ShowDrone(shown); else ShowList(); }

    /// Called once a second. S1API has no "opened" hook: rebuild when the app opens or the drones change.
    public static void Refresh()
    {
        var a = instance;
        if (a?.body == null) return;
        var owned = Drones.Instance?.Owned;
        if (owned != null && a.shown != null && !owned.Contains(a.shown)) // a load replaced the Drone objects
        {
            var fresh = owned.Find(x => x.Id == a.shown.Id);
            if (fresh != null) a.Reshow(fresh); else a.ShowList();
        }
        bool open = a.IsOpen();
        if (open && !a.wasOpen) a.Reshow(a.shown); // back where you left (e.g. after picking a storage)
        a.wasOpen = open;
        if (a.shown != null && a.screen == Screen.Drone) // also while closed, so a pick shows at once when the phone comes back
        {
            if (a.status != null) { a.status.text = Courier.Status(a.shown); a.status.color = StatusColor(a.shown); }
            if (a.sender != null) a.sender.text = Short(a.shown.Sender);
            if (a.receiver != null) a.receiver.text = Short(a.shown.Receiver);
        }
        if (!open || a.screen != Screen.List) return;
        if (a.listSig != ListSig()) { a.ShowList(); return; }
        foreach (var (d, s, r, strip) in a.rows)
        {
            if (s == null) continue;
            s.text = Courier.Status(d); s.color = strip.color = StatusColor(d);
            r.text = $"{Short(d.Sender)}  →  {Short(d.Receiver)}";
        }
    }

    void Reshow(Drone d)
    {
        if (d == null) ShowList();
        else if (screen == Screen.Items) ShowItems(d);
        else ShowDrone(d);
    }

    static string ListSig()
    {
        var owned = Drones.Instance?.Owned;
        var sb = new System.Text.StringBuilder();
        sb.Append(owned == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owned)); // a load swaps the list
        foreach (var d in owned ?? new()) sb.Append(d.Id).Append(d.Name); // rebuild when drones are added or renamed
        return sb.ToString();
    }

    void Clear(Screen s, string heading, Drone d = null)
    {
        keepY = s == screen && d == shown ? body.anchoredPosition.y : null;
        UIFactory.ClearChildren(body);
        status = sender = receiver = null; rows.Clear(); nameField = null;
        screen = s;
        title.text = heading;
        back.SetActive(s != Screen.List);
        scroll.verticalNormalizedPosition = 1f;
    }

    // After building a screen: same screen redrawn -> stay where the player was scrolled.
    void Done()
    {
        if (keepY is not float y) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(body);
        body.anchoredPosition = new Vector2(body.anchoredPosition.x, y);
    }

    // --- screens ---

    void ShowList()
    {
        confirmStop = null;
        Clear(Screen.List, "Drones", null);
        shown = null; listSig = ListSig();
        var owned = Drones.Instance?.Owned;
        if (owned == null || owned.Count == 0)
        {
            var c = CardBox(body);
            Label(c.transform, "No drones yet", 22, Color.white, FontStyle.Bold);
            Label(c.transform, "Milo sells them in the warehouse.", 18, Muted);
            return;
        }
        foreach (var d in owned)
        {
            var drone = d;
            var card = UIFactory.Panel("Drone", body, Card);
            Round(card.GetComponent<Image>());
            var h = card.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(0, 16, 0, 0); h.spacing = 16;
            h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            card.AddComponent<LayoutElement>().preferredHeight = 104f;
            card.AddComponent<Button>().onClick.AddListener((UnityAction)(() => ShowDrone(drone)));

            var strip = UIFactory.Panel("Strip", card.transform, StatusColor(d)).GetComponent<Image>();
            Round(strip.GetComponent<Image>());
            strip.gameObject.AddComponent<LayoutElement>().preferredWidth = 8f;

            var col = Column(card.transform, 4f, new RectOffset(0, 0, 12, 12));
            col.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var top = Row(col.transform, 28f);
            var name = Label(top.transform, d.Name, 22, Color.white, FontStyle.Bold); name.GetComponent<LayoutElement>().flexibleWidth = 1f;
            Label(top.transform, $"{d.Spec.Stacks} stacks  ·  {d.Trips} trips", 16, Muted, FontStyle.Normal, TextAnchor.MiddleRight).GetComponent<LayoutElement>().preferredWidth = 260f;
            var st = Label(col.transform, Courier.Status(d), 18, StatusColor(d));
            var route = Label(col.transform, $"{Short(d.Sender)}  →  {Short(d.Receiver)}", 16, Muted);
            rows.Add((drone, st, route, strip));
        }
    }

    void ShowDrone(Drone d)
    {
        Clear(Screen.Drone, d.Name, d);
        shown = d;

        var top = CardBox(body);
        var nr = Row(top.transform, 44f);
        nameField = TextField(nr.transform, d.CustomName ?? "", $"{d.Spec.Name} #{d.Number}");
        nameField.GetComponent<LayoutElement>().flexibleWidth = 1f;
        Button(nr.transform, "Save name", () => SaveName(), Btn, 160);
        status = Label(top.transform, Courier.Status(d), 22, StatusColor(d), FontStyle.Bold);
        Label(top.transform, $"{d.Spec.Stacks} stacks per trip  ·  {d.Trips} trips done", 16, Muted);

        Section("ROUTE");
        var route = CardBox(body);
        sender = RouteRow(route.transform, "FROM", d.Sender, () => WorldPicker.Start(d, pickSender: true), () => { Net.Do(d, Op.Sender, ""); ShowDrone(d); });
        receiver = RouteRow(route.transform, "TO", d.Receiver, () => WorldPicker.Start(d, pickSender: false), () => { Net.Do(d, Op.Receiver, ""); ShowDrone(d); });

        Section("CARGO");
        var cargo = CardBox(body);
        var r = Row(cargo.transform, 56f);
        if (d.Items.Count == 0) Label(r.transform, d.SharesSender() ? "Everything the other drones don't take" : "Everything", 18, Color.white).GetComponent<LayoutElement>().flexibleWidth = 1f;
        else
        {
            var icons = Row(r.transform, 56f, 6f);
            icons.GetComponent<LayoutElement>().flexibleWidth = 1f;
            for (int i = 0; i < d.Items.Count && i < 8; i++) Icon(icons.transform, d.Items[i], 52f);
            if (d.Items.Count > 8) Label(icons.transform, $"+{d.Items.Count - 8}", 18, Muted);
        }
        Button(r.transform, "Choose items", () => ShowItems(d), Btn, 200);

        Section("LEAVE THE SENDER WHEN");
        var when = CardBox(body);
        Option(when.transform, "It's full (no empty slot)", d.Trigger == Trigger.WhenFull, () => Edit(d, Op.Trigger, (int)Trigger.WhenFull));
        Option(when.transform, $"{d.TriggerStacks} stack{(d.TriggerStacks > 1 ? "s" : "")} are ready", d.Trigger == Trigger.WhenStacks, () => Edit(d, Op.Trigger, (int)Trigger.WhenStacks),
            () => { Net.Do(d, Op.Stacks, Math.Max(1, d.TriggerStacks - 1).ToString()); Edit(d, Op.Trigger, (int)Trigger.WhenStacks); },
            () => { Net.Do(d, Op.Stacks, Math.Min(d.Spec.Stacks, d.TriggerStacks + 1).ToString()); Edit(d, Op.Trigger, (int)Trigger.WhenStacks); });
        Option(when.transform, $"Every {d.EveryMinutes} minutes", d.Trigger == Trigger.Every, () => Edit(d, Op.Trigger, (int)Trigger.Every),
            () => { Net.Do(d, Op.Every, Math.Max(10, d.EveryMinutes - 10).ToString()); Edit(d, Op.Trigger, (int)Trigger.Every); },
            () => { Net.Do(d, Op.Every, Math.Min(24 * 60, d.EveryMinutes + 10).ToString()); Edit(d, Op.Trigger, (int)Trigger.Every); });

        bool ready = d.Sender != null && d.Receiver != null;
        var act = Row(body, 52f, 12f);
        Button(act.transform, "Start loop", () => { if (ready) Edit(d, Op.Mode, (int)Mode.Loop); }, d.Mode == Mode.Loop ? Accent : Btn);
        Button(act.transform, "One trip", () => { if (ready) Edit(d, Op.Mode, (int)Mode.Once); }, d.Mode == Mode.Once ? Accent : Btn);
        Button(act.transform, "Stop", () => Edit(d, Op.Mode, (int)Mode.Off), d.Mode == Mode.Off ? Danger : Btn);
        if (!ready) Label(body, "Set where it picks up (FROM) and drops off (TO) first.", 16, Waiting, FontStyle.Normal, TextAnchor.MiddleCenter);

        Section("GIVE IT BACK");
        var bye = CardBox(body);
        bool armed = confirmStop == d;
        Label(bye.transform, d.Cargo.Count > 0 ? "It's carrying goods: wait until they're delivered."
            : $"Milo takes it back and leaves ${TechNerd.Refund(d):N0} cash for you in a dead drop.", 16, Muted);
        Button(bye.transform, armed ? "Tap again to confirm" : "Stop using this drone", () =>
        {
            if (d.Cargo.Count > 0) return;
            if (!armed) { confirmStop = d; ShowDrone(d); return; }
            confirmStop = null;
            Net.Do(d, Op.Retire);
            ShowList();
        }, Danger).transform.parent.GetComponent<LayoutElement>().preferredHeight = 46f;
        Done();
    }

    // Toggle which items the drone takes: what's in the sender now, plus what's already chosen.
    void ShowItems(Drone d)
    {
        Clear(Screen.Items, $"Items  ·  {d.Name}", d);
        shown = d;
        var ids = new List<string>(d.Items);
        foreach (var id in Courier.ItemsIn(d.Sender)) if (!ids.Contains(id)) ids.Add(id);
        Label(body, ids.Count == 0
            ? d.Sender == null ? "Set the sender first: items are chosen from what's in it." : "The sender is empty: put the items in it first."
            : "Tap items to choose what this drone carries. None chosen = everything.", 16, Muted);

        var grid = new GameObject("Grid");
        grid.transform.SetParent(body, false);
        grid.AddComponent<RectTransform>();
        var g = grid.AddComponent<GridLayoutGroup>();
        g.cellSize = new Vector2(150f, 160f); g.spacing = new Vector2(12f, 12f);

        Tile(grid.transform, null, d.SharesSender() ? "The rest" : "Everything", d.Items.Count == 0, () => SetItems(d, new()));
        foreach (var id in ids)
        {
            var item = id;
            Tile(grid.transform, item, Courier.ItemName(item), d.Items.Contains(item), () =>
            {
                var next = new List<string>(d.Items);
                if (!next.Remove(item)) next.Add(item);
                SetItems(d, next);
            });
        }
        Done();
    }

    void SetItems(Drone d, List<string> items)
    {
        if (d.Conflict(d.Sender, items) is { } o) { Mod.Notify("Drones", $"{o.Name} already takes {(items.Count == 0 ? "the rest" : "one of these items")} from this sender."); return; }
        Net.Do(d, Op.Items, string.Join(",", items));
        ShowItems(d);
    }

    // Change a drone (through Net: the host applies it, a co-op client also sends it to the host) and redraw.
    void Edit(Drone d, Op op, int value) { Net.Do(d, op, value.ToString()); ShowDrone(d); }

    // --- pieces ---

    static string Short(string guid) => guid == null ? "not set" : Courier.Find(guid)?.Label ?? "missing";

    static Color StatusColor(Drone d) => Courier.Holding(d) ? Bad : d.Leg switch
    {
        Leg.ToReceiver or Leg.Returning => Flying,
        Leg.Unloading => d.Cargo.Count > 0 && d.LegSecondsLeft <= 0f ? Bad : Busy,
        _ => d.Mode == Mode.Off ? Muted : d.Sender == null || d.Receiver == null ? Bad : Waiting,
    };

    void Section(string text) => Label(body, text, 15, Muted, FontStyle.Bold).GetComponent<LayoutElement>().preferredHeight = 22f;

    static GameObject CardBox(Transform parent)
    {
        var c = Column(parent, 8f, new RectOffset(18, 18, 14, 14));
        var img = c.AddComponent<Image>(); img.color = Card; Round(img);
        return c;
    }

    Text RouteRow(Transform parent, string tag, string guid, Action pick, Action clear)
    {
        var r = Row(parent, 46f);
        Label(r.transform, tag, 15, Muted, FontStyle.Bold).GetComponent<LayoutElement>().preferredWidth = 64f;
        var t = Label(r.transform, Short(guid), 19, Color.white);
        t.GetComponent<LayoutElement>().flexibleWidth = 1f;
        Button(r.transform, "Select", pick, Btn, 130);
        if (guid != null) Button(r.transform, "X", clear, Danger, 46);
        return t;
    }

    static void Option(Transform parent, string label, bool on, Action pick, Action minus = null, Action plus = null)
    {
        var r = Row(parent, 44f);
        Button(r.transform, label, pick, on ? Accent : Btn, 0, TextAnchor.MiddleLeft);
        if (minus == null) return;
        Button(r.transform, "-", minus, Btn, 52);
        Button(r.transform, "+", plus, Btn, 52);
    }

    static void Tile(Transform parent, string itemId, string name, bool on, Action click)
    {
        var t = UIFactory.Panel("Tile", parent, on ? Accent : Card);
        Round(t.GetComponent<Image>());
        t.AddComponent<Button>().onClick.AddListener((UnityAction)click);
        var v = t.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(8, 8, 12, 8); v.spacing = 6; v.childAlignment = TextAnchor.UpperCenter;
        v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        if (itemId != null) Icon(t.transform, itemId, 88f).GetComponent<LayoutElement>().preferredWidth = 88f;
        else Label(t.transform, "ALL", 34, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter).GetComponent<LayoutElement>().preferredHeight = 88f;
        var n = Label(t.transform, name, 15, Color.white, FontStyle.Normal, TextAnchor.UpperCenter);
        n.GetComponent<LayoutElement>().preferredHeight = 40f;
    }

    static InputField TextField(Transform parent, string value, string placeholder)
    {
        var go = UIFactory.Panel("Field", parent, new Color(0.08f, 0.09f, 0.12f));
        Round(go.GetComponent<Image>());
        go.AddComponent<LayoutElement>();
        Text Inner(string name, string text, Color color)
        {
            var t = Label(go.transform, text, 18, color);
            t.name = name;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(14f, 0f); rt.offsetMax = new Vector2(-14f, 0f);
            return t;
        }
        var hint = Inner("Placeholder", placeholder, Muted);
        var text = Inner("Text", "", Color.white);
        text.supportRichText = false;
        var f = go.AddComponent<InputField>();
        f.textComponent = text; f.placeholder = hint;
        f.characterLimit = 24; f.lineType = InputField.LineType.SingleLine;
        f.text = value;
        return f;
    }

    static Image Icon(Transform parent, string itemId, float size)
    {
        var go = new GameObject("Icon");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.sprite = Courier.ItemIcon(itemId);
        img.preserveAspect = true;
        if (img.sprite == null) img.color = new Color(1f, 1f, 1f, 0.1f);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = size; le.preferredWidth = size;
        return img;
    }

    static GameObject Row(Transform parent, float height, float spacing = 10f)
    {
        var go = new GameObject("Row");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing; h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = true;
        go.AddComponent<LayoutElement>().preferredHeight = height;
        return go;
    }

    static GameObject Column(Transform parent, float spacing, RectOffset padding)
    {
        var go = new GameObject("Column");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var v = go.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing; v.padding = padding;
        v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        go.AddComponent<LayoutElement>();
        return go;
    }

    static Text Label(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        var t = UIFactory.Text("Label", text, parent, size, anchor, style);
        t.color = color;
        if (font != null) t.font = font;
        t.gameObject.AddComponent<LayoutElement>().preferredHeight = size + 10;
        return t;
    }

    static Text Button(Transform parent, string label, Action onClick, Color color, float width = 0f, TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        var go = UIFactory.Panel("Button", parent, color);
        Round(go.GetComponent<Image>());
        var le = go.AddComponent<LayoutElement>();
        if (width > 0f) le.preferredWidth = width; else le.flexibleWidth = 1f;
        // Not S1API's ButtonUtils.AddListener: it skips a delegate it has seen before (rebuilt Back buttons got no click)
        go.AddComponent<Button>().onClick.AddListener((UnityAction)onClick);
        var t = Label(go.transform, label, 18, Color.white, FontStyle.Bold, anchor);
        var rt = t.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(14f, 0f); rt.offsetMax = new Vector2(-14f, 0f);
        return t;
    }

    static void Fill(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    // A white rounded rectangle, 9-sliced so any size keeps 10 px corners; tinted by Image.color.
    static void Round(Image img)
    {
        if (round == null)
        {
            const int S = 32, R = 10;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, R, S - R), cy = Mathf.Clamp(y + 0.5f, R, S - R);
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(R - dist + 0.5f)));
                }
            tex.Apply();
            round = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
            round.hideFlags = HideFlags.HideAndDontSave;
        }
        img.sprite = round;
        img.type = Image.Type.Sliced;
    }

    // The phone's own text font, so the app doesn't look like a debug panel (falls back to Arial).
    static Font FindGameFont()
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
            if (t.font != null && t.font.name != "Arial" && t.font.name != "LegacyRuntime") { Mod.Log($"app font: {t.font.name}"); return t.font; }
        return null;
    }
}
