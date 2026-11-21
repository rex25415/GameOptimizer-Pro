// ============================================================================
//  SampleHealth.cs — 采样降级的可解释账簿
//
//  为什么需要它：
//   旧采样层失败时只有两种表态——「返回 0」或「保留旧值」，外加一条 Diag 计数。
//   界面于是看到一个看起来正常的数字（CPU 0% / 内存冻住），诊断面板只多了一个
//   没人看得懂的 tag。读者回答不了三个最基本的问题：哪一层坏了？为什么坏？
//   什么时候会再试？这里把这三件事一起记下来。
//
//  零分配约定：
//   失败路径只写 int（计数 / 状态码 / 阶段 / 下次重试时刻），不建对象、不拼接。
//   十六进制状态码、"2.0s 后重试"这类格式化只发生在诊断读取的那一刻。
//
//  并发：
//   每一格只由它的所有者线程写（采样线程 / 抖动线程 / UI 线程各写各的格子），
//   读取方是 UI / 诊断线程。字段都是 int，读写天然原子；跨字段组合可能差一拍，
//   诊断不需要更强的一致性。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Go
{
    /// <summary>一条采样通路（cpu / mem / gpu.util …）的健康状态。</summary>
    public sealed class SampleHealth
    {
        // 失败发生在哪一步。诊断面板据此区分「打不开」和「读到了但口径不对」。
        public const int StageOpen = 1;
        public const int StageQuery = 2;
        public const int StageParse = 3;
        public const int StageException = 4;
        public const int StageAlloc = 5;

        private readonly string _layer;
        private int _failures;
        private int _code;
        private int _stage;
        private int _down;
        private int _nextRetry;   // Environment.TickCount 时刻；0 = 现在就可以重试

        internal SampleHealth(string layer)
        {
            _layer = layer;
        }

        /// <summary>稳定标识，同时也是 Diag 的 tag（诊断面板按它归类）。</summary>
        public string Layer { get { return _layer; } }
        /// <summary>累计失败次数。</summary>
        public int Failures { get { return _failures; } }
        /// <summary>最近一次失败的原始状态码（NTSTATUS / PDH status / Win32 error），0 = 无码。</summary>
        public int Code { get { return _code; } }
        /// <summary>最近一次失败发生在哪一步，见 Stage* 常量。</summary>
        public int Stage { get { return _stage; } }
        /// <summary>最近一次尝试是否失败。恢复后为 false，计数保留供追溯。</summary>
        public bool Down { get { return _down != 0; } }

        /// <summary>距离下次允许重试还有多少毫秒；0 = 现在就可以重试。</summary>
        public int RetryInMs
        {
            get
            {
                int at = _nextRetry;
                if (at == 0) return 0;
                // TickCount 是 int 毫秒，49 天回绕。回绕后差值是"负数"，按有符号差判断仍然成立。
                int d = unchecked(at - Environment.TickCount);
                return d > 0 ? d : 0;
            }
        }

        internal void Fail(int code, int stage, int retryMs)
        {
            _failures++;
            _code = code;
            _stage = stage;
            _down = 1;
            _nextRetry = retryMs > 0 ? unchecked(Environment.TickCount + retryMs) : 0;
        }

        internal void Ok()
        {
            _down = 0;
            _nextRetry = 0;
        }

        public string StageName
        {
            get
            {
                switch (_stage)
                {
                    case StageOpen: return "open";
                    case StageQuery: return "query";
                    case StageParse: return "parse";
                    case StageException: return "exception";
                    case StageAlloc: return "alloc";
                    default: return "-";
                }
            }
        }

        public string CodeText
        {
            get { return _code == 0 ? "—" : "0x" + _code.ToString("X8"); }
        }

        /// <summary>一行可读描述。诊断路径专用，允许分配。</summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append(_layer);
            if (_down == 0)
            {
                sb.Append("  ok");
                if (_failures > 0) sb.Append("  （已恢复，累计失败 ").Append(_failures).Append(" 次）");
                return sb.ToString();
            }
            sb.Append("  DOWN  stage=").Append(StageName).Append("  code=").Append(CodeText);
            sb.Append("  ×").Append(_failures);
            int retry = RetryInMs;
            if (retry > 0) sb.Append("  retry in ").Append((retry / 1000.0).ToString("0.0")).Append('s');
            else sb.Append("  retry now");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一组采样通路的账簿。构造期把格子一次分配好，之后只改格子里面的 int。
    /// </summary>
    public sealed class SampleHealthBoard
    {
        private readonly SampleHealth[] _cells;

        public SampleHealthBoard(string[] layers)
        {
            int n = layers == null ? 0 : layers.Length;
            _cells = new SampleHealth[n];
            for (int i = 0; i < n; i++) _cells[i] = new SampleHealth(layers[i]);
        }

        public int Count { get { return _cells.Length; } }

        public SampleHealth At(int i) { return _cells[i]; }

        /// <summary>当前处于降级状态的层数。</summary>
        public int DownCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _cells.Length; i++) if (_cells[i].Down) n++;
                return n;
            }
        }

        /// <summary>一行摘要：诊断面板页脚直接显示它（0 层时也是完整的一句话）。</summary>
        public string Summary()
        {
            int down = DownCount;
            if (down == 0)
                return "采样层 " + _cells.Length + "/" + _cells.Length + " 就绪（0 层降级）";

            StringBuilder sb = new StringBuilder(192);
            sb.Append("采样降级 ").Append(down).Append('/').Append(_cells.Length).Append("：");
            bool first = true;
            for (int i = 0; i < _cells.Length; i++)
            {
                SampleHealth c = _cells[i];
                if (!c.Down) continue;
                if (!first) sb.Append(" · ");
                first = false;
                sb.Append(c.Layer).Append(" ×").Append(c.Failures);
                sb.Append(" code=").Append(c.CodeText);
                sb.Append(" stage=").Append(c.StageName);
                int retry = c.RetryInMs;
                if (retry > 0) sb.Append("（").Append((retry / 1000.0).ToString("0.0")).Append("s 后重试）");
                else sb.Append("（可立即重试）");
            }
            return sb.ToString();
        }

        /// <summary>每层一行，供诊断面板展开（允许分配）。</summary>
        public List<string> Rows()
        {
            List<string> rows = new List<string>(_cells.Length);
            for (int i = 0; i < _cells.Length; i++) rows.Add(_cells[i].Describe());
            return rows;
        }
    }
}
