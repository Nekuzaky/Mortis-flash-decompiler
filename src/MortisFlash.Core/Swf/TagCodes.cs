namespace MortisFlash.Swf;

public static class TagCodes
{
    public const int End = 0, ShowFrame = 1, DefineShape = 2, PlaceObject = 4, RemoveObject = 5, DefineBits = 6,
        DefineButton = 7, JPEGTables = 8, SetBackgroundColor = 9, DefineFont = 10, DefineText = 11, DoAction = 12,
        DefineFontInfo = 13, DefineSound = 14, StartSound = 15, DefineButtonSound = 17, SoundStreamHead = 18,
        SoundStreamBlock = 19, DefineBitsLossless = 20, DefineBitsJPEG2 = 21, DefineShape2 = 22,
        DefineButtonCxform = 23, Protect = 24, PlaceObject2 = 26, RemoveObject2 = 28, DefineShape3 = 32,
        DefineText2 = 33, DefineButton2 = 34, DefineBitsJPEG3 = 35, DefineBitsLossless2 = 36, DefineEditText = 37,
        DefineSprite = 39, ProductInfo = 41, FrameLabel = 43, SoundStreamHead2 = 45, DefineMorphShape = 46,
        DefineFont2 = 48, ExportAssets = 56, ImportAssets = 57, EnableDebugger = 58, DoInitAction = 59,
        DefineVideoStream = 60, VideoFrame = 61, DefineFontInfo2 = 62, DebugID = 63, EnableDebugger2 = 64,
        ScriptLimits = 65, SetTabIndex = 66, FileAttributes = 69, PlaceObject3 = 70, ImportAssets2 = 71,
        DoABC = 72, DefineFontAlignZones = 73, CSMTextSettings = 74, DefineFont3 = 75, SymbolClass = 76,
        Metadata = 77, DefineScalingGrid = 78, DoABC2 = 82, DefineShape4 = 83, DefineMorphShape2 = 84,
        DefineSceneAndFrameLabelData = 86, DefineBinaryData = 87, DefineFontName = 88, StartSound2 = 89,
        DefineBitsJPEG4 = 90, DefineFont4 = 91, EnableTelemetry = 93, PlaceObject4 = 94;

    static readonly Dictionary<int, string> Names = new()
    {
        [0] = "End", [1] = "ShowFrame", [2] = "DefineShape", [4] = "PlaceObject", [5] = "RemoveObject",
        [6] = "DefineBits", [7] = "DefineButton", [8] = "JPEGTables", [9] = "SetBackgroundColor",
        [10] = "DefineFont", [11] = "DefineText", [12] = "DoAction", [13] = "DefineFontInfo",
        [14] = "DefineSound", [15] = "StartSound", [17] = "DefineButtonSound", [18] = "SoundStreamHead",
        [19] = "SoundStreamBlock", [20] = "DefineBitsLossless", [21] = "DefineBitsJPEG2", [22] = "DefineShape2",
        [23] = "DefineButtonCxform", [24] = "Protect", [26] = "PlaceObject2", [28] = "RemoveObject2",
        [32] = "DefineShape3", [33] = "DefineText2", [34] = "DefineButton2", [35] = "DefineBitsJPEG3",
        [36] = "DefineBitsLossless2", [37] = "DefineEditText", [39] = "DefineSprite", [41] = "ProductInfo",
        [43] = "FrameLabel", [45] = "SoundStreamHead2", [46] = "DefineMorphShape", [48] = "DefineFont2",
        [56] = "ExportAssets", [57] = "ImportAssets", [58] = "EnableDebugger", [59] = "DoInitAction",
        [60] = "DefineVideoStream", [61] = "VideoFrame", [62] = "DefineFontInfo2", [63] = "DebugID",
        [64] = "EnableDebugger2", [65] = "ScriptLimits", [66] = "SetTabIndex", [69] = "FileAttributes",
        [70] = "PlaceObject3", [71] = "ImportAssets2", [72] = "DoABC", [73] = "DefineFontAlignZones",
        [74] = "CSMTextSettings", [75] = "DefineFont3", [76] = "SymbolClass", [77] = "Metadata",
        [78] = "DefineScalingGrid", [82] = "DoABC2", [83] = "DefineShape4", [84] = "DefineMorphShape2",
        [86] = "DefineSceneAndFrameLabelData", [87] = "DefineBinaryData", [88] = "DefineFontName",
        [89] = "StartSound2", [90] = "DefineBitsJPEG4", [91] = "DefineFont4", [93] = "EnableTelemetry",
        [94] = "PlaceObject4",
    };

    public static string Name(int code) => Names.TryGetValue(code, out var n) ? n : $"Unknown_{code}";

    public static bool DefinesCharacter(int code) => code is DefineShape or DefineShape2 or DefineShape3 or DefineShape4
        or DefineBits or DefineBitsJPEG2 or DefineBitsJPEG3 or DefineBitsJPEG4 or DefineBitsLossless or DefineBitsLossless2
        or DefineButton or DefineButton2 or DefineFont or DefineFont2 or DefineFont3 or DefineFont4
        or DefineText or DefineText2 or DefineEditText or DefineSound or DefineSprite
        or DefineMorphShape or DefineMorphShape2 or DefineVideoStream or DefineBinaryData;

    public static bool IsShape(int code) => code is DefineShape or DefineShape2 or DefineShape3 or DefineShape4;
    public static bool IsImage(int code) => code is DefineBits or DefineBitsJPEG2 or DefineBitsJPEG3 or DefineBitsJPEG4
        or DefineBitsLossless or DefineBitsLossless2;
    public static bool IsFont(int code) => code is DefineFont or DefineFont2 or DefineFont3 or DefineFont4;
    public static bool IsText(int code) => code is DefineText or DefineText2 or DefineEditText;
    public static bool IsMorph(int code) => code is DefineMorphShape or DefineMorphShape2;
}
