// ============================================================================
//  Network.Tcp.cs — TCP 连接与协议栈参数（Network 分片）
//
//  两件事放在一个分片里的理由：它们都只在"点开某张适配器"之后才需要读，
//  而且都来自系统接口，不属于本页 1 Hz 采样器。以前参数表独立成一张
//  （Network.TcpTable.cs），和动作行里的"现值"重复；现在只有这一个落点。
//
//  未设置 = 该键不存在，协议栈按内置默认值运行：不猜默认值，也不写成 0。
//  读失败（Denied / Error）归"不可用"，与"未设置"分开写——两者不是一回事。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Network : Page
    {
        private const string TcpKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        private const int MaxConnRows = 8;

        /// <summary>一条审计参数：期望值来自动作目录（Op.Truth），不在这里另抄一份。</summary>
        private sealed class AuditRow
        {
            public string Hive, Path, Name, Expect, Note;
            public TextBlock Cur;
            public int Tier = -1;
        }

        private sealed class ConnRow
        {
            public string Local, Remote, State;
            public int Port;
        }

        private sealed class ConnResult
        {
            public readonly List<ConnRow> Rows = new List<ConnRow>();
            public int All;
            public bool Failed;
            public string Why;
        }

        private readonly List<AuditRow> _audit = new List<AuditRow>();
        private TextBlock _auditMeta;
        private StackPanel _pidTcpHost;
        private TextBlock _pidTcpMeta;
        private volatile bool _pidTcpBusy;
        private string _pidTcpSig;

        // ---- 参数目录（构建期一次）-----------------------------------------
        private void BuildAuditRows(Core c)
        {
            _audit.Clear();
            AddAction(c, "nagle", "关闭 Nagle 攒批；对已建立连接需重连生效");
            AddAction(c, "net-throttle", "MMCSS 网络节流；4294967295 = 关闭");
            AddAction(c, "dns-cache", "DNS 缓存上限（秒）；只影响重复解析");
            AddAction(c, "p2p", "更新 P2P 上传模式；0 = 关闭");

            AddRaw("Tcp1323Opts", "窗口缩放与时间戳（位掩码）");
            AddRaw("DefaultTTL", "出站报文默认 TTL");
            AddRaw("SackOpts", "选择性确认（SACK）");
            AddRaw("MaxUserPort", "临时端口上限");
            AddRaw("TcpTimedWaitDelay", "TIME_WAIT 保持秒数");
            AddRaw("EnableDCA", "网卡直写 CPU 缓存（DCA）");
        }

        private void AddAction(Core c, string id, string note)
        {
            ActionDef a = c.Engine.Find(id);
            if (a == null || a.Ops == null || a.Ops.Length == 0) return;
            Op op = a.Ops[0];
            AuditRow r = new AuditRow();
            r.Hive = op.Hive;
            r.Path = op.Path;
            r.Name = op.Name;
            r.Expect = op.Truth;
            r.Note = note;
            _audit.Add(r);
        }

        private void AddRaw(string name, string note)
        {
            AuditRow r = new AuditRow();
            r.Hive = "HKLM";
            r.Path = TcpKey;
            r.Name = name;
            r.Expect = null;
            r.Note = note;
            _audit.Add(r);
        }

        // ---- 展开内容：协议栈参数表 ----------------------------------------
        /// <summary>参数块：一行一个键（当前 / 期望 / 说明），只在展开时构建与读取。</summary>
        private StackPanel BuildAuditBlock(AdapterRow ownerRow)
        {
            StackPanel box = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock t = Ui.Label("协议栈参数 / 全机共用", T.AccentDim);
            head.Children.Add(t);
            TextBlock meta = Ui.Mono("—", T.FsNano, T.Mute);
            meta.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(meta, 1);
            head.Children.Add(meta);
            _auditMeta = meta;
            if (ownerRow != null) ownerRow.AuditMeta = meta;
            box.Children.Add(head);
            box.Children.Add(Gap(8));

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i <= _audit.Count; i++)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AuditHead(g, "参数", 0);
            AuditHead(g, "当前", 1);
            AuditHead(g, "期望", 2);
            AuditHead(g, "说明", 3);
            g.Children.Add(Hair(0, T.Line));

            List<AuditRow> rowList = new List<AuditRow>(_audit.Count);
            for (int i = 0; i < _audit.Count; i++)
            {
                AuditRow tmpl = _audit[i];
                AuditRow r = new AuditRow();
                r.Hive = tmpl.Hive;
                r.Path = tmpl.Path;
                r.Name = tmpl.Name;
                r.Expect = tmpl.Expect;
                r.Note = tmpl.Note;
                int row = i + 1;
                TextBlock nm = Cell(Ui.Mono(r.Name, T.FsNano, T.Text), 0, row);
                g.Children.Add(nm);
                r.Cur = Cell(Ui.Mono("—", T.FsNano, T.Dim), 1, row);
                r.Tier = -1;      // 新树里的 TextBlock 是默认色：旧档位必须作废，否则颜色不重画
                g.Children.Add(r.Cur);
                g.Children.Add(Cell(Ui.Mono(r.Expect == null ? "—" : r.Expect, T.FsNano,
                    r.Expect == null ? T.Mute : T.Dim), 2, row));
                g.Children.Add(Cell(Ui.Tx(r.Note, T.FsMicro, T.Mute, T.FCn), 3, row));
                if (i < _audit.Count - 1) g.Children.Add(Hair(row, T.LineSoft));
                rowList.Add(r);
            }
            if (ownerRow != null) ownerRow.AuditRows = rowList;
            box.Children.Add(g);
            return box;
        }

        private static void AuditHead(Grid g, string text, int col)
        {
            TextBlock t = Ui.Label(text, T.Mute);
            t.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        private static TextBlock Cell(TextBlock t, int col, int row)
        {
            t.Margin = new Thickness(0, 6, 8, 6);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(t, col);
            Grid.SetRow(t, row);
            return t;
        }

        private static Border Hair(int row, uint col)
        {
            Border b = new Border();
            b.Height = 1;
            b.Background = T.S(col);
            b.SnapsToDevicePixels = true;
            b.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(b, row);
            Grid.SetColumnSpan(b, 4);
            return b;
        }

        // ---- 参数读取：一次开键读完，行级判定在 ReadAudit 里 -----------------
        private void RefreshTable()
        {
            AdapterRow openRow = _open;
            List<AuditRow> rows = openRow != null && openRow.AuditRows != null ? openRow.AuditRows : _audit;
            TextBlock meta = openRow != null && openRow.AuditMeta != null ? openRow.AuditMeta : _auditMeta;
            if (meta == null) return;      // 没有摊开的表就不读注册表
            int present = 0, matched = 0, raw = 0, targets = 0, denied = 0;
            bool keyDenied;
            RegistryKey key = OpenTcpKey(out keyDenied);
            try
            {
                for (int i = 0; i < rows.Count; i++)
                    ReadAudit(rows[i], key, keyDenied, ref present, ref matched, ref raw, ref targets, ref denied);
            }
            finally { if (key != null) key.Dispose(); }

            // 两组数字分开写：只读键"设没设"和动作目标"到不到位"是两个问题，不共用一个分母。
            meta.Text = "原始键 " + present + "/" + raw + " 已设 · 目标 " + matched + "/" + targets + " 到位"
                            + (denied > 0 ? " · " + denied + " 项不可读" : "");
        }

        private static RegistryKey OpenTcpKey(out bool denied)
        {
            denied = false;
            try { return Microsoft.Win32.Registry.LocalMachine.OpenSubKey(TcpKey, false); }
            catch (System.Security.SecurityException) { denied = true; }
            catch (UnauthorizedAccessException) { denied = true; }
            catch (Exception ex) { Diag.Fail("tcp.open", ex); }
            return null;
        }

        private static void ReadAudit(AuditRow r, RegistryKey key, bool keyDenied,
                                      ref int present, ref int matched, ref int raw, ref int targets, ref int denied)
        {
            bool d;
            string cur;
            if (r.Expect == null)
            {
                raw++;
                cur = ReadRaw(r.Name, key, out d);
                d = d || keyDenied;                  // 键打不开 = 整列不可用，不能写成"未设置"
            }
            else
            {
                targets++;
                cur = ReadTarget(r, out d);
            }
            if (d) { denied++; PaintAudit(r, "不可用", T.Bad); return; }

            bool has = cur != null;
            if (has && cur.Length == 0) cur = "\"\"";
            if (r.Expect == null)
            {
                PaintAudit(r, has ? cur : "未设置", has ? T.Text : T.Mute);
                if (has) present++;
                return;
            }
            bool ok = has && Same(cur, r.Expect);
            PaintAudit(r, has ? cur : "未设置", ok ? T.Dim : T.Warn);
            if (ok) matched++;
        }

        private static string ReadRaw(string name, RegistryKey key, out bool denied)
        {
            denied = false;
            if (key == null) return null;
            try
            {
                object o = key.GetValue(name, null);
                return o == null ? null : o.ToString();
            }
            catch (System.Security.SecurityException) { denied = true; return null; }
            catch (UnauthorizedAccessException) { denied = true; return null; }
            catch (Exception) { Diag.Hit("tcp.value"); return null; }
        }

        private static string ReadTarget(AuditRow r, out bool denied)
        {
            RegValue rv = Reg.Read(r.Hive, r.Path, r.Name);
            // 读失败也归"不可用"，不谎报"未设置"
            denied = rv.Denied || rv.Error != null;
            return rv.Existed ? Reg.ToText(rv.Value) : null;
        }

        private static void PaintAudit(AuditRow r, string text, uint tint)
        {
            if (r.Cur == null) return;
            if (r.Cur.Text != text) r.Cur.Text = text;
            int t = unchecked((int)tint);
            if (t == r.Tier) return;
            r.Tier = t;
            r.Cur.Foreground = T.S(tint);
        }

        /// <summary>数值型不能按字符串比：DWord 的 -1 与 4294967295 是同一个值。</summary>
        private static bool Same(string cur, string want)
        {
            if (cur == null || want == null) return false;
            if (string.Equals(cur, want, StringComparison.OrdinalIgnoreCase)) return true;
            long a, b;
            if (Ops.TryNumber(cur, "DWord", out a) && Ops.TryNumber(want, "DWord", out b)) return a == b;
            return false;
        }

        // ---- TCP 连接：按本地地址归属到这张网卡 -----------------------------
        /// <summary>
        /// 读全机 TCP 连接表（GetActiveTcpConnections → GetExtendedTcpTable），
        /// 只留下本地地址等于本适配器 IPv4 的那些。监听在 0.0.0.0 的套接字不归属单张网卡。
        /// 这个调用会走一次系统表，必须在后台线程上跑。
        /// </summary>
        private static ConnResult ReadConns(string ipv4)
        {
            ConnResult res = new ConnResult();
            try
            {
                TcpConnectionInformation[] all = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                res.All = all.Length;
                for (int i = 0; i < all.Length; i++)
                {
                    ConnRow row = MakeConn(all[i], ipv4);
                    if (row != null) res.Rows.Add(row);
                }
                res.Rows.Sort(CompareConn);
            }
            catch (Exception ex)
            {
                res.Failed = true;
                res.Why = ex.Message;
                Diag.Fail("net.tcp.conns", ex);
            }
            return res;
        }

        private static ConnRow MakeConn(TcpConnectionInformation c, string ipv4)
        {
            if (c == null || c.LocalEndPoint == null || string.IsNullOrEmpty(ipv4)) return null;
            if (c.LocalEndPoint.Address.ToString() != ipv4) return null;
            ConnRow r = new ConnRow();
            r.Port = c.LocalEndPoint.Port;
            r.Local = c.LocalEndPoint.Port.ToString();
            r.Remote = c.RemoteEndPoint == null ? "—" : c.RemoteEndPoint.ToString();
            r.State = c.State.ToString().ToUpperInvariant();
            return r;
        }

        /// <summary>已建立的排前面（那才是"正在通信"的），其余按本地端口。</summary>
        private static int CompareConn(ConnRow a, ConnRow b)
        {
            int sa = a.State == "ESTABLISHED" ? 0 : 1;
            int sb = b.State == "ESTABLISHED" ? 0 : 1;
            if (sa != sb) return sa - sb;
            return a.Port.CompareTo(b.Port);
        }

        // ---- 按进程活跃 TCP 排行（GetExtendedTcpTable）--------------------
        private const int MaxPidTcpRows = 8;

        private UIElement BuildPidTcpPanel(Core c)
        {
            StackPanel body = new StackPanel();
            _pidTcpMeta = Ui.Mono("SCAN \u2014", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("活跃 TCP 进程", "ACTIVE TCP BY PID · TOP " + MaxPidTcpRows, _pidTcpMeta));
            body.Children.Add(Gap(12));

            body.Children.Add(BuildPidTcpHead());
            body.Children.Add(Ui.Rule(T.LineSoft, 0));

            _pidTcpHost = new StackPanel();
            _pidTcpHost.Children.Add(Ui.Muted("正在读取内核 TCP 连接表\u2026", T.FsSmall));
            body.Children.Add(_pidTcpHost);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock foot = Ui.Tx("数据来自 Win32 GetExtendedTcpTable 按 PID 实时聚合（仅统计非零远端连接）；" +
                                   "可迅速定位正在高并发外联或占用带宽的进程，点上方适配器行可查看对应网卡协议栈参数。",
                T.FsMicro, T.Mute, T.FCn);
            foot.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(foot);

            return Ui.Panel(body, T.PadPanel);
        }

        private static Grid BuildPidTcpHead()
        {
            Grid g = NewPidTcpGrid();
            g.Height = 22;

            TextBlock nm = Ui.Label("进程名", T.Mute);
            nm.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(nm);

            TextBlock pid = Ui.Label("PID", T.Mute);
            pid.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pid, 1);
            g.Children.Add(pid);

            TextBlock est = Ui.Label("已建连", T.Mute);
            est.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(est, 2);
            g.Children.Add(est);

            TextBlock rem = Ui.Label("样本远端 IP:端口", T.Mute);
            rem.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(rem, 3);
            g.Children.Add(rem);
            return g;
        }

        private static Grid NewPidTcpGrid()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            return g;
        }

        private void RefreshPidTcp()
        {
            if (_pidTcpHost == null || _pidTcpBusy) return;
            _pidTcpBusy = true;
            int gen = _netGen;

            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Thread t = new Thread(delegate ()
            {
                List<PidTcpSummary> list = null;
                try { list = NetTrace.ReadActiveTcpByPid(); }
                catch (Exception ex)
                {
                    Diag.Fail("net.pidtcp.read", ex);
                    list = new List<PidTcpSummary>();
                }
                try
                {
                    ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                    {
                        if (gen != _netGen) return;
                        _pidTcpBusy = false;
                        ApplyPidTcp(list);
                    });
                }
                catch (Exception ex)
                {
                    _pidTcpBusy = false;
                    Diag.Fail("net.pidtcp.post", ex);
                }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.pidtcp";
            t.Start();
        }

        private void ApplyPidTcp(List<PidTcpSummary> list)
        {
            if (_pidTcpHost == null) return;
            if (list == null) list = new List<PidTcpSummary>();

            int count = list.Count < MaxPidTcpRows ? list.Count : MaxPidTcpRows;
            string sig = list.Count.ToString();
            for (int i = 0; i < count; i++)
            {
                PidTcpSummary p = list[i];
                sig += "|" + p.Pid + ":" + (p.ProcessName ?? "") + ":" + p.Established + ":" + p.SynWait + ":" + p.TimeWait + ":" + p.ActiveCount + ":" + (p.TopRemote ?? "");
            }

            if (_pidTcpMeta != null)
            {
                int totalEst = 0;
                for (int i = 0; i < list.Count; i++) totalEst += list[i].Established;
                _pidTcpMeta.Text = list.Count == 0
                    ? "0 ACTIVE PID"
                    : list.Count + " 进程 · " + totalEst + " ESTABLISHED";
            }

            if (sig == _pidTcpSig) return;
            _pidTcpSig = sig;
            _pidTcpHost.Children.Clear();

            if (count == 0)
            {
                _pidTcpHost.Children.Add(Ui.Empty(Glyph.Network, "当前无活跃 TCP 外联进程",
                    "GetExtendedTcpTable 未检测到处于 Established 或活跃握手状态的 IPv4 连接。", T.Mute, null));
                return;
            }

            for (int i = 0; i < count; i++)
                _pidTcpHost.Children.Add(BuildPidTcpRow(list[i], i == 0));
        }

        private static UIElement BuildPidTcpRow(PidTcpSummary p, bool lead)
        {
            Grid row = NewPidTcpGrid();
            row.Height = 36;

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Margin = new Thickness(0, 0, 10, 0);

            string pname = string.IsNullOrEmpty(p.ProcessName) ? ("PID " + p.Pid) : p.ProcessName;
            TextBlock nm = Ui.Tx(pname, T.FsSmall, T.Hi, T.FCn);
            nm.FontWeight = FontWeights.SemiBold;
            nm.TextTrimming = TextTrimming.CharacterEllipsis;
            nm.TextWrapping = TextWrapping.NoWrap;
            left.Children.Add(nm);
            if (p.SynWait > 0)
            {
                Border synTag = Ui.Tag("SYN " + p.SynWait, T.Warn);
                synTag.Margin = new Thickness(8, 0, 0, 0);
                left.Children.Add(synTag);
            }
            row.Children.Add(left);

            TextBlock pid = Ui.Mono(p.Pid.ToString(), T.FsNano, T.Mute);
            pid.HorizontalAlignment = HorizontalAlignment.Right;
            pid.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pid, 1);
            row.Children.Add(pid);

            string estText = p.Established + (p.ActiveCount > p.Established ? " / " + p.ActiveCount : "");
            TextBlock est = Ui.Mono(estText, T.FsSmall, p.Established > 0 ? T.Accent : T.Dim);
            est.FontWeight = FontWeights.SemiBold;
            est.HorizontalAlignment = HorizontalAlignment.Right;
            est.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(est, 2);
            row.Children.Add(est);

            string remText = string.IsNullOrEmpty(p.TopRemote) ? "\u2014" : p.TopRemote;
            TextBlock rem = Ui.Mono(remText, T.FsNano, T.Dim);
            rem.HorizontalAlignment = HorizontalAlignment.Right;
            rem.VerticalAlignment = VerticalAlignment.Center;
            rem.TextTrimming = TextTrimming.CharacterEllipsis;
            rem.TextWrapping = TextWrapping.NoWrap;
            rem.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(rem, 3);
            row.Children.Add(rem);

            Border shell = Ui.RowShell(row, lead ? T.Accent : T.LineSoft, 4);
            shell.Background = Brushes.Transparent;
            shell.MouseEnter += delegate { shell.Background = T.S(T.LineFaint); };
            shell.MouseLeave += delegate { shell.Background = Brushes.Transparent; };
            Tip.Attach(shell, pname + " (PID " + p.Pid + ")\n" +
                              "Established: " + p.Established + " · 活跃握手: " + p.SynWait +
                              " · TimeWait: " + p.TimeWait + " · Listen: " + p.Listen +
                              "\n最近样本远端：" + remText);
            return shell;
        }
    }
}