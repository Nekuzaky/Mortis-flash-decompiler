using MortisFlash.Swf;

namespace MortisFlash.Assets;

/// <summary>AS2 handler attached to a clip (onClipEvent) or a button (on).</summary>
public sealed class EventActions
{
    public required string Header { get; init; }
    public required byte[] Actions { get; init; }
    /// <summary>Location of the actions within the tag data (used to replace them).</summary>
    public int Offset { get; init; }
    /// <summary>Size field to update: offset, width (2 or 4 bytes, 0 = none) and extra bytes included in the count.</summary>
    public int SizeField { get; init; } = -1;
    public int SizeBytes { get; init; }
    public int SizeExtra { get; init; }
}

public sealed class PlaceObject
{
    public int Depth;
    public int? CharacterId;
    public bool Move;
    public Matrix? Matrix;
    public ColorTransform? ColorTransform;
    public int? Ratio;
    public string? Name;
    public int? ClipDepth;
    public string? ClassName;
    public int? BlendMode;
    public bool? Visible;
    public List<EventActions> ClipActions = [];

    public static PlaceObject Parse(SwfTag tag, int version)
    {
        var r = new SwfReader(tag.Data) { Version = version };
        var p = new PlaceObject();
        if (tag.Code == TagCodes.PlaceObject)
        {
            p.CharacterId = r.U16();
            p.Depth = r.U16();
            p.Matrix = r.Matrix();
            if (r.Remaining > 0) p.ColorTransform = r.CxForm(false);
            return p;
        }
        bool hasClipActions = r.Bit(), hasClipDepth = r.Bit(), hasName = r.Bit(), hasRatio = r.Bit(),
            hasCxform = r.Bit(), hasMatrix = r.Bit(), hasChar = r.Bit(), move = r.Bit();
        bool hasFilters = false, hasBlend = false, hasCache = false, hasClassName = false, hasImage = false,
            hasVisible = false, hasBg = false;
        if (tag.Code is TagCodes.PlaceObject3 or TagCodes.PlaceObject4)
        {
            r.Bit();
            hasBg = r.Bit();
            hasVisible = r.Bit();
            hasImage = r.Bit();
            hasClassName = r.Bit();
            hasCache = r.Bit();
            hasBlend = r.Bit();
            hasFilters = r.Bit();
        }
        p.Move = move;
        p.Depth = r.U16();
        if (hasClassName || (hasImage && hasChar)) p.ClassName = r.String();
        if (hasChar) p.CharacterId = r.U16();
        if (hasMatrix) p.Matrix = r.Matrix();
        if (hasCxform) p.ColorTransform = r.CxForm(true);
        if (hasRatio) p.Ratio = r.U16();
        if (hasName) p.Name = r.String();
        if (hasClipDepth) p.ClipDepth = r.U16();
        if (hasFilters) SkipFilters(r);
        if (hasBlend) p.BlendMode = r.U8();
        if (hasCache) r.U8();
        if (hasVisible) p.Visible = r.U8() != 0;
        if (hasBg) r.Rgba();
        if (hasClipActions)
        {
            try { p.ClipActions = ReadClipActions(r, version); }
            catch (EndOfStreamException) { }
        }
        return p;
    }

    public static void SkipFilters(SwfReader r)
    {
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            int id = r.U8();
            switch (id)
            {
                case 0: r.Skip(23); break;
                case 1: r.Skip(9); break;
                case 2: r.Skip(15); break;
                case 3: r.Skip(27); break;
                case 4:
                case 7:
                {
                    int c = r.U8();
                    r.Skip(c * 5 + 19);
                    break;
                }
                case 5:
                {
                    int mx = r.U8(), my = r.U8();
                    r.Skip(8 + mx * my * 4 + 5);
                    break;
                }
                case 6: r.Skip(80); break;
                default: throw new InvalidDataException($"Unknown filter {id}");
            }
        }
    }

    static readonly string[] ClipEvents =
    [
        "keyUp", "keyDown", "mouseUp", "mouseDown", "mouseMove", "unload", "enterFrame", "load",
        "dragOver", "rollOut", "rollOver", "releaseOutside", "release", "press", "initialize", "data",
        "", "", "", "", "", "construct", "keyPress", "dragOut",
    ];

    static List<EventActions> ReadClipActions(SwfReader r, int version)
    {
        var list = new List<EventActions>();
        r.U16();
        _ = version >= 6 ? r.U32() : r.U16();
        while (r.Remaining > 0)
        {
            uint flags = version >= 6 ? ReadFlags32(r) : (uint)ReadFlags16(r) << 16;
            if (flags == 0) break;
            int sizeField = r.Position;
            int size = (int)r.U32();
            int extra = 0;
            int key = -1;
            if ((flags & 1u << (31 - 22)) != 0 && version >= 6)
            {
                key = r.U8();
                size--;
                extra = 1;
            }
            int actionsOffset = r.Position;
            var actions = r.Bytes(size);
            var names = new List<string>();
            for (int i = 0; i < ClipEvents.Length; i++)
                if ((flags & 1u << (31 - i)) != 0 && ClipEvents[i] != "")
                    names.Add(ClipEvents[i] == "keyPress" && key >= 0 ? $"keyPress \"{KeyName(key)}\"" : ClipEvents[i]);
            if (names.Count == 0) names.Add("?");
            bool isButtonEvent = names.All(n => n is "release" or "press" or "rollOver" or "rollOut" or "dragOver" or "dragOut" or "releaseOutside" || n.StartsWith("keyPress"));
            string header = isButtonEvent ? $"on({string.Join(", ", names)})" : $"onClipEvent({string.Join(", ", names)})";
            list.Add(new EventActions
            {
                Header = header, Actions = actions, Offset = actionsOffset, SizeField = sizeField, SizeBytes = 4, SizeExtra = extra,
            });
        }
        return list;
    }

    /// <summary>The 32 flag bits are read as-is: bit 31 = first flag.</summary>
    static uint ReadFlags32(SwfReader r)
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v = v << 8 | r.U8();
        return v;
    }

    static uint ReadFlags16(SwfReader r)
    {
        uint v = (uint)r.U8() << 8;
        return v | r.U8();
    }

    public static string KeyName(int code) => code switch
    {
        1 => "<Left>", 2 => "<Right>", 3 => "<Home>", 4 => "<End>", 5 => "<Insert>", 6 => "<Delete>", 8 => "<Backspace>",
        13 => "<Enter>", 14 => "<Up>", 15 => "<Down>", 16 => "<PageUp>", 17 => "<PageDown>", 18 => "<Tab>", 19 => "<Escape>",
        32 => "<Space>", _ => ((char)code).ToString(),
    };
}

public sealed class ButtonRecord
{
    public bool Up, Over, Down, HitTest;
    public int CharacterId, Depth;
    public Matrix Matrix;
    public ColorTransform ColorTransform = ColorTransform.Identity;
}

public sealed class SwfButton
{
    public int Id;
    public List<ButtonRecord> Records = [];
    public List<EventActions> Actions = [];
    public SwfTag? Tag;

    public static SwfButton Parse(SwfTag tag, int version)
    {
        var r = new SwfReader(tag.Data) { Version = version };
        var b = new SwfButton { Id = r.U16(), Tag = tag };
        bool v2 = tag.Code == TagCodes.DefineButton2;
        int actionOffset = 0, actionBase = 0;
        if (v2)
        {
            r.U8();
            actionBase = r.Position;
            actionOffset = r.U16();
        }
        while (r.Remaining > 0)
        {
            int flags = r.U8();
            if (flags == 0) break;
            var rec = new ButtonRecord
            {
                HitTest = (flags & 8) != 0, Down = (flags & 4) != 0, Over = (flags & 2) != 0, Up = (flags & 1) != 0,
                CharacterId = r.U16(), Depth = r.U16(), Matrix = r.Matrix(),
            };
            if (v2)
            {
                rec.ColorTransform = r.CxForm(true);
                if ((flags & 0x10) != 0) PlaceObject.SkipFilters(r);
                if ((flags & 0x20) != 0) r.U8();
            }
            b.Records.Add(rec);
        }
        if (!v2)
        {
            int at = r.Position;
            b.Actions.Add(new EventActions { Header = "on(release)", Offset = at, Actions = r.Rest() });
            return b;
        }
        if (actionOffset == 0) return b;
        r.Position = actionBase + actionOffset;
        while (r.Remaining >= 4)
        {
            int start = r.Position;
            int size = r.U16();
            int cond = r.U8() << 8 | r.U8(); // bit fields: first byte = high-order flags
            int actionsLen = size == 0 ? r.Remaining : size - 4;
            int at = r.Position;
            var actions = r.Bytes(actionsLen);
            // A BUTTONCONDACTION's size includes its 4 header bytes; 0 = last in the list (left unchanged)
            b.Actions.Add(new EventActions
            {
                Header = ButtonCondition(cond), Actions = actions, Offset = at,
                SizeField = size == 0 ? -1 : start, SizeBytes = 2, SizeExtra = 4,
            });
            if (size == 0) break;
            r.Position = start + size;
        }
        return b;
    }

    static string ButtonCondition(int c)
    {
        var ev = new List<string>();
        // High byte: IdleToOverDown … IdleToOverUp; low byte: KeyPress (7 bits) + OverDownToIdle
        if ((c & 0x8000) != 0) ev.Add("dragOver");
        if ((c & 0x4000) != 0) ev.Add("releaseOutside");
        if ((c & 0x2000) != 0) ev.Add("dragOver");
        if ((c & 0x1000) != 0) ev.Add("dragOut");
        if ((c & 0x0800) != 0) ev.Add("release");
        if ((c & 0x0400) != 0) ev.Add("press");
        if ((c & 0x0200) != 0) ev.Add("rollOut");
        if ((c & 0x0100) != 0) ev.Add("rollOver");
        int key = c >> 1 & 0x7F;
        if (key != 0) ev.Add($"keyPress \"{PlaceObject.KeyName(key)}\"");
        if ((c & 1) != 0) ev.Add("dragOut");
        return $"on({string.Join(", ", ev.Distinct())})";
    }
}
