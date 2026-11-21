// ============================================================================
//  Diag.cs — 被吞掉的异常计数器（SPINE）
//
//  为什么要有它：
//  全项目有 153 处 catch { }。吞掉本身常常是对的——一个探针失败不该炸掉整页——
//  但「吞掉且不计数」是错的：用户看到的是「按钮点了没反应」，我们看到的是「自检全绿」。
//  这里给每一处吞掉的地方一个 tag：次数、最后一条消息、首次堆栈。
//  静默降级因此变成可读的读数，而不是一片沉默。
//
//  约束：
//   · 稳态零分配：同一个 tag 的重复上报只做 int 自增 + 字符串引用赋值，
//     不建对象、不拼接、不格式化；字典只在首次遇到新 tag 时写入。
//   · 线程安全：采样线程 / 守护线程 / UI 线程都会上报。
//   · 容量：上限 200 个 tag（含 __overflow__），超出后一律归入 __overflow__；
//     每个 tag 只保留一条堆栈（它遇到的第一条），所以内存不随次数增长。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Go
{
    public static class Diag
    {
        /// <summary>一个 tag 的累计状态。除首次上报外，这张表不再新增对象。</summary>
        private sealed class Cell
        {
            public string Tag;
            public int Count;
            public string LastType;   // 最后一次的异常类型名（RuntimeType.Name 已缓存，不分配）
            public string LastMsg;    // 最后一次的消息（复用异常自带的字符串）
            public string Stack;      // 首次堆栈：只留一条
            public string Head;       // 堆栈的头部几帧（Rows 用，解析一次后缓存）
        }

        private const int MaxTags = 200;
        private const string Overflow = "__overflow__";

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Cell> _cells =
            new Dictionary<string, Cell>(16, StringComparer.Ordinal);

        private static int _total;

        private static readonly Comparison<Cell> ByCount = CompareCells;

        /// <summary>累计上报次数（所有 tag 的总和）。</summary>
        public static int Total
        {
            get { lock (_lock) { return _total; } }
        }

        /// <summary>已经用了多少个 tag（含 __overflow__）。</summary>
        public static int Tags
        {
            get { lock (_lock) { return _cells.Count; } }
        }

        /// <summary>
        /// 记录一次被吞掉的异常：计数 +1，保留最后一条消息，保留首次堆栈。
        /// ex 为 null 时退化成 Hit（只计数）。
        /// </summary>
        public static void Fail(string tag, Exception ex)
        {
            if (ex == null) { Hit(tag); return; }
            lock (_lock)
            {
                Cell c = CellFor(tag);
                c.Count++;
                _total++;
                // 类型名与消息都是异常自带/已缓存的字符串，这里只搬引用，不拼接。
                c.LastType = ex.GetType().Name;
                c.LastMsg = ex.Message;
                if (c.Stack == null)
                {
                    try { c.Stack = ex.StackTrace; }
                    catch (Exception inner) { c.Stack = "栈不可用：" + inner.GetType().Name; }
                }
            }
        }

        /// <summary>纯计数：用于「走了降级路径」这类没有异常的读数。</summary>
        public static void Hit(string tag)
        {
            lock (_lock)
            {
                Cell c = CellFor(tag);
                c.Count++;
                _total++;
            }
        }

        /// <summary>某个 tag 的次数；不存在返回 0。</summary>
        public static int Count(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            lock (_lock)
            {
                Cell c;
                return _cells.TryGetValue(tag, out c) ? c.Count : 0;
            }
        }

        /// <summary>
        /// 供日志页展示：按次数降序，每个 tag 一行。
        /// 行格式：tag  ×次数  类型: 消息（下一行是首次堆栈的头几帧）。
        /// 这是诊断路径，允许分配；稳态上报路径不允许。
        /// </summary>
        public static List<string> Rows()
        {
            lock (_lock)
            {
                Cell[] arr = new Cell[_cells.Count];
                _cells.Values.CopyTo(arr, 0);
                Array.Sort(arr, ByCount);

                List<string> rows = new List<string>(arr.Length);
                StringBuilder sb = new StringBuilder(160);
                for (int i = 0; i < arr.Length; i++)
                {
                    Cell c = arr[i];
                    sb.Length = 0;
                    sb.Append(c.Tag).Append("  \u00D7").Append(c.Count);
                    if (c.LastType != null)
                    {
                        sb.Append("  ").Append(c.LastType);
                        if (c.LastMsg != null) sb.Append(": ").Append(c.LastMsg);
                    }
                    string head = HeadOf(c);
                    if (head != null) sb.Append('\n').Append(head);
                    rows.Add(sb.ToString());
                }
                return rows;
            }
        }

        private static string HeadOf(Cell c)
        {
            if (c.Head != null) return c.Head;
            if (c.Stack == null) return null;
            c.Head = Frames(c.Stack, 3);
            if (c.Head.Length == 0) { c.Head = null; return null; }
            return c.Head;
        }

        /// <summary>
        /// 取堆栈里前 n 条非空帧，压成缩进文本。
        /// 不按 "at " 前缀筛选：.NET Framework 的 StackTrace 是本地化的
        /// （中文系统上是"在 … 位置 …"），按英文关键字筛会得到空栈。
        /// </summary>
        private static string Frames(string stack, int n)
        {
            StringBuilder sb = new StringBuilder(120);
            int taken = 0;
            int i = 0;
            while (i < stack.Length && taken < n)
            {
                int j = stack.IndexOf('\n', i);
                if (j < 0) j = stack.Length;
                string line = stack.Substring(i, j - i).Trim();
                if (line.Length > 0)
                {
                    if (taken > 0) sb.Append('\n');
                    sb.Append("    ").Append(line);
                    taken++;
                }
                i = j + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 取（或新建）tag 的格子。上限 200 是"总格子数"——含 __overflow__ 自己，
        /// 所以常态 tag 最多 199 个，第 200 个位置留给溢出桶。这样无论来多少新 tag，
        /// 表都不会超过 200 项。
        /// </summary>
        private static Cell CellFor(string tag)
        {
            if (string.IsNullOrEmpty(tag)) tag = "?";
            Cell c;
            if (_cells.TryGetValue(tag, out c)) return c;
            if (_cells.Count >= MaxTags - 1)
            {
                if (_cells.TryGetValue(Overflow, out c)) return c;
                tag = Overflow;
            }
            c = new Cell();
            c.Tag = tag;
            _cells[tag] = c;
            return c;
        }

        private static int CompareCells(Cell a, Cell b)
        {
            if (a.Count != b.Count) return b.Count - a.Count;   // 次数多的在前
            return string.CompareOrdinal(a.Tag, b.Tag);
        }
    }
}
