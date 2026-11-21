// ============================================================================
//  Brand.cs — 品牌母版（唯一真源 / Single Source of Truth）
//
//  这个文件同时被两处编译：
//    1) 主程序（GameOptimizer.exe）—— 侧栏品牌位、启动闪屏
//    2) 图标生成器（tools\IconGen.exe）—— 多尺寸 .ico
//  因此界面里的矢量标记与外部图标文件不可能再出现造型漂移。
//
//  几何坐标系：24×24 网格，Y 向下。
//  改动这里 = 同时改变界面与图标，这是刻意的。
// ============================================================================
namespace Go
{
    public static class Brand
    {
        /// <summary>主标记：顶点三角（描边几何，24 网格）。</summary>
        public const string MarkPath = "M2.5 21 L12 3 L21.5 21 Z";

        /// <summary>内层人字（次级信息层，24 网格）。</summary>
        public const string MarkInnerPath = "M7.6 19.4 L12 11.8 L16.4 19.4";

        /// <summary>主标记描边宽度（相对 24 网格）。</summary>
        public const double MarkStroke = 1.5;

        /// <summary>内层人字描边宽度（相对 24 网格）。</summary>
        public const double MarkInnerStroke = 1.2;

        /// <summary>母版配色（与 Theme 令牌同值，独立声明以避免图标生成器依赖 WPF 主题）。</summary>
        public const uint Accent = 0xFF5FD9FF;
        public const uint AccentDim = 0xFF2A8FB8;

        /// <summary>图标底板：与侧栏同色的深空黑曜石渐变。</summary>
        public const uint PlateTop = 0xFF17253D;
        public const uint PlateBottom = 0xFF070C17;
        public const uint PlateEdge = 0x565FD9FF;

        /// <summary>产品名与副标（图标 ALT 文本、关于信息共用）。</summary>
        public const string ProductName = "GameOptimizer";
        public const string ProductSub = "VANGUARD CONSOLE";

        /// <summary>当前修订号与年月（导轨脚注 / 启动卡 / 程序集版本共用，唯一真源）。</summary>
        public const int Rev = 142;
        public const string RevStamp = "2026.11";
    }
}