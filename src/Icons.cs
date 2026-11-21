// ============================================================================
//  Icons.cs — 24×24 网格线性图标（1.4px 描边，圆头，几何化）
// ============================================================================
namespace Go
{
    public static class Glyph
    {
        public const string Overview = "M12 2.5 V6.5 M12 17.5 V21.5 M2.5 12 H6.5 M17.5 12 H21.5 " +
                                      "M16.5 12 A4.5 4.5 0 1 1 7.5 12 A4.5 4.5 0 1 1 16.5 12";
        public const string Tune = "M3 7 H21 M3 12 H21 M3 17 H21 M8.5 5 V9 M15.5 10 V14 M10.5 15 V19";
        public const string Cpu = "M8 8 H16 V16 H8 Z M8 4.5 V8 M12 4.5 V8 M16 4.5 V8 M8 16 V19.5 " +
                                  "M12 16 V19.5 M16 16 V19.5 M4.5 8 H8 M4.5 12 H8 M4.5 16 H8 " +
                                  "M16 8 H19.5 M16 12 H19.5 M16 16 H19.5";
        public const string Memory = "M2.5 8 H21.5 V16 H2.5 Z M6.5 16 V19 M10.5 16 V19 M14.5 16 V19 M18.5 16 V19 " +
                                     "M5.5 11 H18.5";
        public const string Graphics = "M3 5 H21 V16 H3 Z M9 20 H15 M12 16 V20 M7 9 H11 M7 12 H9";
        public const string Network = "M12 3 C15.5 6.5 15.5 17.5 12 21 C8.5 17.5 8.5 6.5 12 3 Z " +
                                      "M3.2 12 H20.8 M4.6 7.4 H19.4 M4.6 16.6 H19.4";
        public const string Processes = "M3.5 5.5 H6 V8 H3.5 Z M3.5 13 H6 V15.5 H3.5 Z " +
                                        "M8.5 6.7 H20.5 M8.5 14.2 H17.5 M3.5 19.5 H6 V22 H3.5 Z " +
                                        "M8.5 20.7 H19";
        public const string Profiles = "M12 3 L21 7.8 L12 12.6 L3 7.8 Z M3 12.6 L12 17.4 L21 12.6";
        public const string Benchmark = "M2.5 12 H6.5 L9 6 L13 18 L15.5 12 H21.5";
        public const string Restore = "M8 4.5 L4 8.5 L8 12.5 M4 8.5 H14.5 A6 6 0 0 1 14.5 20.5 H8.5";
        public const string Log = "M6 3.5 H18 V20.5 H6 Z M9 8.5 H15 M9 12 H15 M9 15.5 H12.5";
        public const string Shield = "M12 3 L20 6 V12 C20 16.5 16.5 19.5 12 21 C7.5 19.5 4 16.5 4 12 V6 Z";
        public const string Bolt = "M13 2.5 L5 13.5 H11 L10 21.5 L19 10.5 H12.5 Z";
        public const string Search = "M15.5 15.5 L20.5 20.5 M17.2 10.6 A6.6 6.6 0 1 1 4 10.6 A6.6 6.6 0 1 1 17.2 10.6";
        public const string Chevron = "M9 5 L16 12 L9 19";
        public const string Check = "M4.5 12.5 L9.5 17.5 L19.5 6.5";
        public const string Warn = "M12 3.5 L21.5 20 H2.5 Z M12 9.5 V14 M12 16.8 V17.2";
        public const string Refresh = "M20 12 A8 8 0 1 1 17.6 6.2 M20 3.5 V8 H15.5";
        public const string Download = "M12 3.5 V15.5 M7 10.5 L12 15.5 L17 10.5 M4 20 H20";
        public const string Play = "M7 4.5 L19 12 L7 19.5 Z";
        public const string Target = "M12 3 A9 9 0 1 1 11.99 3 M12 7.5 A4.5 4.5 0 1 1 11.99 7.5 M12 11.5 V12.5";
        public const string Theme = "M12 3 C12 3 5.5 10.5 5.5 15 A6.5 6.5 0 0 0 18.5 15 C18.5 10.5 12 3 12 3 Z M9 15.5 A3 3 0 0 0 12 18.5";
        public const string Sparkle = "M12 3 L13.8 9.2 L20 11 L13.8 12.8 L12 19 L10.2 12.8 L4 11 L10.2 9.2 Z";
        public const string Activity = "M3 13 H7 L10 5.5 L14 18.5 L17 11 H21";
        public const string Filter = "M3.5 5 H20.5 L14 12.5 V19 L10 20.5 V12.5 Z";
        public const string Power = "M12 3.5 V11 M7.5 6.3 A7.5 7.5 0 1 0 16.5 6.3";
        public const string History = "M4.5 7.5 V3.5 M4.5 7.5 H8.5 M5.2 7 A8 8 0 1 1 4.5 14 M12 7.5 V12.5 L15.5 14.5";
        public const string Lock = "M6.5 11 H17.5 V20 H6.5 Z M8.5 11 V7.5 A3.5 3.5 0 0 1 15.5 7.5 V11 M12 14.5 V16.5";
        public const string Gpu = "M4.5 7.5 H19.5 V16.5 H4.5 Z M2.5 5.5 V18.5 M10 12 A2 2 0 1 1 9.99 12 M15.5 10 V14 M8.5 16.5 V19 M12.5 16.5 V19";
        public const string Link = "M10 14 L14 10 M8.5 15.5 L6.8 17.2 A3.2 3.2 0 0 1 2.3 12.7 L5.5 9.5 A3.2 3.2 0 0 1 10 9.5 M15.5 8.5 L17.2 6.8 A3.2 3.2 0 0 1 21.7 11.3 L18.5 14.5 A3.2 3.2 0 0 1 14 14.5";
        public const string Pin = "M12 14 V21 M7.5 4.5 H16.5 L15 10 L17.5 14 H6.5 L9 10 Z";
        public const string Sliders = "M4 6 H20 M4 12 H20 M4 18 H20 M9 4 V8 M15 10 V14 M7 16 V20";
    }
}
