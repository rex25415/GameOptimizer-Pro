// ============================================================================
//  Network.Sample.cs — 采样：1 Hz 字节差分 + 1 Hz ICMP（Network 分片）
//
//  本文件只做 IO 与算术：读网卡计数器、算差分、找网关、发一次 echo。
//  结果交给读数带（Network.Band.cs）与图表（Network.Chart.cs）去画——
//  采样里不出现 TextBlock、不拼展示用字符串，渲染永远不在后台线程发生。
//
//  ICMP 为什么值得一条常驻线程：单次 Send 最长阻塞 300 ms。放进 UI 线程，
//  一次超时就是一次可感知的停顿；放进每拍新建的线程，就是每秒一次线程创建。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Network : Page
    {
        /// <summary>一张网卡在本窗口里的实测流量；只给渲染用，不跨线程保存。</summary>
        private sealed class AdapterTraffic
        {
            public string Id;
            public long Bytes;
            public double Rx, Tx;
        }

        private sealed class NetAccount
        {
            public string Id;
            public NetworkInterface Ni;
            public long Rx, Tx;
        }

        private readonly List<NetAccount> _accounts = new List<NetAccount>();
        private DispatcherTimer _netTimer;
        private Dispatcher _ui;
        private DateTime _lastAt = DateTime.MinValue;
        private bool _measured;
        private double _rxMbps, _txMbps;

        // ICMP 工作线程自己的字段；UI 只读。
        private volatile bool _pingRun;
        private volatile bool _dualProbing;
        private Thread _pingThread;
        private string _gwIp, _gwNic;

        private void StartSampler()
        {
            _ui = Dispatcher.CurrentDispatcher;
            if (C != null && C.HeadlessRender) return;
            if (_netTimer == null)
            {
                _netTimer = new DispatcherTimer();
                _netTimer.Tick += delegate { SampleThroughput(); };
            }
            _netTimer.Stop();
            // 首个窗口故意短：进页面后 0.12 s 就给出第一个真实读数，面板头如实写窗口长度。
            _netTimer.Interval = TimeSpan.FromMilliseconds(120);
            _netTimer.Start();
            StartPing();
            TriggerDualProbe(1, 110);
        }

        private void TriggerDualProbe(int samples, int timeoutMs)
        {
            if (C != null && C.HeadlessRender) return;
            if (_dualProbing) return;
            _dualProbing = true;
            int gen = _netGen;
            Dispatcher ui = _ui != null ? _ui : Dispatcher.CurrentDispatcher;
            NetTrace.ProbeDualPathAsync(samples, timeoutMs, delegate (DualPathLatencyReport rep)
            {
                try
                {
                    ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                    {
                        if (gen != _netGen) return;
                        _dualProbing = false;
                        ApplyDualPathReport(rep);
                    });
                }
                catch (Exception ex)
                {
                    _dualProbing = false;
                    Diag.Fail("net.dualprobe.post", ex);
                }
            });
        }

        private void StopSampler()
        {
            if (_netTimer != null) _netTimer.Stop();
            // 只放开关，不 Join：一次 ICMP 最长 300 ms，UI 线程不能等它。
            _pingRun = false;
        }

        /// <summary>重取基线：接口集合变化（重新枚举 / 进页面）后必须重新起算差分。</summary>
        private void PrimeThroughput()
        {
            _accounts.Clear();
            for (int i = 0; i < _adapterInfos.Count; i++)
            {
                AdapterInfo a = _adapterInfos[i];
                NetAccount acc = new NetAccount();
                acc.Id = a.Id;
                acc.Ni = a.Ni;
                ReadStats(a.Ni, out acc.Rx, out acc.Tx);
                _accounts.Add(acc);
            }
            _lastAt = DateTime.Now;
            _measured = false;
            _rxMbps = 0;
            _txMbps = 0;
            UpdateHead();
        }

        // ---- 1 Hz 吞吐窗口 ---------------------------------------------------
        private void SampleThroughput()
        {
            if (_accounts.Count == 0) return;
            DateTime now = DateTime.Now;
            if (_lastAt == DateTime.MinValue) { _lastAt = now; return; }

            double dt = (now - _lastAt).TotalSeconds;
            if (dt < 0.02) return;   // 窗口太短（同一毫秒的重复触发）不出数，等下一拍

            long sumRx = 0, sumTx = 0;
            List<AdapterTraffic> traffic = new List<AdapterTraffic>(_accounts.Count);
            int okReads = 0;
            for (int i = 0; i < _accounts.Count; i++)
                okReads += ReadAccount(_accounts[i], dt, traffic, ref sumRx, ref sumTx);

            _lastAt = now;
            _rxMbps = sumRx * 8.0 / dt / 1000000.0;
            _txMbps = sumTx * 8.0 / dt / 1000000.0;
            // 一个都没读成功就不是"测得 0 Mbps"，而是"读不到"：界面必须显示 —。
            _measured = okReads > 0;
            // 首个窗口过后回到 1 Hz：60 个样本 = 60 s。窗口长度本身由面板头读出来。
            if (_netTimer != null && _netTimer.Interval.TotalMilliseconds < 900)
                _netTimer.Interval = TimeSpan.FromSeconds(1);

            ApplyTraffic(traffic);
            if (!_measured) { UpdateHead(); return; }

            SetBandWindow(dt);
            PushThroughput(_rxMbps, _txMbps);
            if (_chart != null) _chart.Push(_rxMbps, _txMbps);
            PaintChartMeta();
            // 一个点画不出线：等第二个样本再撤掉"首个窗口"的说明，别留一块没有解释的空网格。
            if (_chartHint != null && _chart != null && _chart.Count >= 2
                && _chartHint.Visibility != Visibility.Collapsed)
                _chartHint.Visibility = Visibility.Collapsed;
            UpdateHead();
        }

        /// <summary>读一张网卡的计数并算差分。返回 1 = 本拍有效，0 = 基线重建或读失败。</summary>
        private static int ReadAccount(NetAccount a, double dt, List<AdapterTraffic> into,
                                       ref long sumRx, ref long sumTx)
        {
            long rx, tx;
            if (!ReadStats(a.Ni, out rx, out tx))
            {
                // 读失败时基线必须作废：否则失败期间漏掉的字节会在下一次成功时
                // 被一个很短的窗口除，报出一个虚高的速率。
                a.Rx = -1; a.Tx = -1;
                return 0;
            }
            if (a.Rx < 0) { a.Rx = rx; a.Tx = tx; return 0; }   // 重新建立基线，本拍不出数

            long drx = rx - a.Rx;
            long dtx = tx - a.Tx;
            if (drx < 0) drx = 0;
            if (dtx < 0) dtx = 0;
            a.Rx = rx; a.Tx = tx;
            sumRx += drx; sumTx += dtx;

            AdapterTraffic t = new AdapterTraffic();
            t.Id = a.Id;
            t.Bytes = drx + dtx;
            t.Rx = drx * 8.0 / dt / 1000000.0;
            t.Tx = dtx * 8.0 / dt / 1000000.0;
            into.Add(t);
            return 1;
        }

        private static bool ReadStats(NetworkInterface ni, out long rx, out long tx)
        {
            rx = 0; tx = 0;
            if (ni == null) return false;
            try
            {
                IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                rx = st.BytesReceived;
                tx = st.BytesSent;
                return true;
            }
            catch (Exception) { Diag.Hit("net.stats"); return false; }   // 接口被禁用/拔出：本拍不出数
        }

        // ---- 1 Hz ICMP ------------------------------------------------------
        private void StartPing()
        {
            // 先开开关再判活：离开页面后立刻回来时，旧线程可能还没退出，
            // 它看到开关重新打开就会继续跑；如果反过来先判活，这里会直接返回，之后再也没有线程。
            _pingRun = true;
            if (_pingThread != null && _pingThread.IsAlive) return;
            Thread t = new Thread(PingLoop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "go.ping";
            _pingThread = t;
            t.Start();
        }

        private void PingLoop()
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    IPAddress addr = null;
                    int beat = 0;
                    while (_pingRun)
                    {
                        if (addr == null || (beat % 60) == 0) addr = ResolveGw();   // 每分钟重找一次网关
                        if (addr == null)
                        {
                            PostNoGateway();
                            SleepWhileRunning(5000);   // 没有网关：5 s 再看一次，不空转
                            beat++;
                            continue;
                        }
                        int rtt = PingGw(ping, addr);
                        if (!_pingRun) break;
                        PostPing(rtt);
                        beat++;
                        SleepWhileRunning(1000);
                    }
                }
            }
            catch (Exception ex) { Diag.Fail("net.ping.loop", ex); }
        }

        private void SleepWhileRunning(int ms)
        {
            for (int i = 0; i < ms / 100; i++)
            {
                if (!_pingRun) return;
                Thread.Sleep(100);
            }
        }

        /// <summary>一次 ICMP echo。返回毫秒；没有回包返回 -1（不估算、不平滑）。</summary>
        private static int PingGw(Ping ping, IPAddress addr)
        {
            try
            {
                PingReply r = ping.Send(addr, 300);
                if (r != null && r.Status == IPStatus.Success) return (int)r.RoundtripTime;
            }
            catch (Exception) { Diag.Hit("net.ping"); }   // 目标不可达 / 被拒绝：本拍没有读数
            return -1;
        }

        /// <summary>解析默认网关：物理链路优先，同址去重。失败返回 null，不抛。</summary>
        private IPAddress ResolveGw()
        {
            List<string[]> list = FindGateways();
            _gwIp = list.Count == 0 ? null : list[0][1];
            _gwNic = list.Count == 0 ? null : list[0][0];
            IPAddress addr;
            return _gwIp != null && IPAddress.TryParse(_gwIp, out addr) ? addr : null;
        }

        private static List<string[]> FindGateways()
        {
            List<string[]> pref = new List<string[]>();
            List<string[]> rest = new List<string[]>();
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < all.Length; i++)
                {
                    try { AddGateway(all[i], pref, rest); }
                    catch (Exception) { Diag.Hit("net.gateway.nic"); }
                }
            }
            catch (Exception ex) { Diag.Fail("net.gateway", ex); }
            pref.AddRange(rest);
            return Dedup(pref);
        }

        private static void AddGateway(NetworkInterface ni, List<string[]> pref, List<string[]> rest)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) return;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return;

            string gw = null;
            foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
            {
                if (g.Address == null || g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string s = g.Address.ToString();
                if (s == "0.0.0.0") continue;
                gw = s;
                break;
            }
            if (gw == null) return;

            string vk;
            bool isVirtual = NetTrace.IsVirtualTunNic(ni, out vk);
            bool physical = !isVirtual && (
                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            (physical ? pref : rest).Add(new string[] { ni.Name, gw });
        }

        /// <summary>同一网关可能被两张卡同时报上来；按地址去重。</summary>
        private static List<string[]> Dedup(List<string[]> list)
        {
            List<string[]> uniq = new List<string[]>();
            for (int i = 0; i < list.Count; i++)
            {
                bool dup = false;
                for (int k = 0; k < uniq.Count; k++)
                    if (uniq[k][1] == list[i][1]) { dup = true; break; }
                if (!dup) uniq.Add(list[i]);
            }
            return uniq;
        }

        // ---- 回投 UI --------------------------------------------------------
        private void PostPing(int ms)
        {
            Dispatcher ui = _ui;
            if (ui == null) return;
            try { ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate { PushPing(ms); }); }
            catch (Exception ex) { Diag.Fail("net.ping.post", ex); }
        }

        private void PostNoGateway()
        {
            Dispatcher ui = _ui;
            if (ui == null) return;
            try { ui.BeginInvoke(DispatcherPriority.Background, (Action)delegate { OnNoGateway(); }); }
            catch (Exception ex) { Diag.Fail("net.ping.post", ex); }
        }
    }
}