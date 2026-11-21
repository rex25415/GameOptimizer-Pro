// ============================================================================
//  Graphics.Pipeline.cs — 呈现管线实时判定 HAGS/MPO/FSE（Graphics 分片）
//  来源：Graphics.cs（1204 行单体）按职责拆分，partial 共享字段，行为零变化。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Go.Pages
{
    public partial class Graphics : Page
    {
        // ---- 呈现管线 ------------------------------------------------------
        private UIElement BuildPipeline(Core c)
        {
            StackPanel body = new StackPanel();
            _pipeMeta = Ui.Mono("—", T.FsNano, T.Mute);
            body.Children.Add(Ui.PanelHead("呈现管线", "PRESENTATION PATH · LIVE REGISTRY", _pipeMeta));
            body.Children.Add(Gap(12));

            // 列数交给 Reflow 按自身宽度决定：Compact/Regular 竖排，Wide/Ultra 三列。
            Reflow flow = new Reflow(T.Gap, 1, 1, 1, 3);

            PipeRef hags, mpo, fse;
            Border c0 = PipeCard("HAGS", "硬件加速 GPU 调度",
                "HwSchMode：1 = 关闭，2 = 开启。HAGS 让 GPU 自主调度命令提交与显存管理，降低 CPU 侧提交开销；切换后需重启。", out hags);
            Border c1 = PipeCard("MPO", "多平面叠加",
                "OverlayTestMode：存在任意值即禁用 MPO，删除该值并重启 DWM 才恢复。禁用 MPO 会增加合成拷贝、带宽与功耗，多为第三方脚本残留。", out mpo);
            Border c2 = PipeCard("FSE", "独立翻转全屏",
                "GameDVR_FSEBehaviorMode=2 表示强制独立翻转全屏，与 HonorUserFSEBehaviorMode、DXGIHonorFSEWindowsCompatible 共同生效；个别老游戏可能黑屏。", out fse);

            flow.AddRange(c0, c1, c2);

            _hags = hags;
            _mpo = mpo;
            _fse = fse;
            body.Children.Add(flow);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));

            TextBlock foot = Ui.Tx("三个键由本页每 5 秒复读一次；在动作区切换后立即复判，不依赖开关自身的视觉状态。", T.FsMicro, T.Mute, T.FCn);
            foot.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(foot);

            return Ui.Panel(body, 18);
        }

        private Border PipeCard(string keyLabel, string title, string tip, out PipeRef r)
        {
            r = new PipeRef();

            StackPanel sp = new StackPanel();

            StackPanel top = new StackPanel();
            top.Orientation = Orientation.Horizontal;

            r.Dot = new Ellipse();
            r.Dot.Width = 6;
            r.Dot.Height = 6;
            r.Dot.Fill = T.S(T.Mute);
            r.Dot.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(r.Dot);

            TextBlock k = Ui.Label(keyLabel, T.Mute);
            k.Margin = new Thickness(7, 0, 0, 0);
            top.Children.Add(k);
            sp.Children.Add(top);

            TextBlock t = Ui.Tx(title, T.FsBody, T.Hi, T.FCn);
            t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(0, 9, 0, 0);
            sp.Children.Add(t);

            r.State = Ui.Tx("—", T.FsTitle, T.Dim, T.FCn);
            r.State.FontWeight = FontWeights.SemiBold;
            r.State.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(r.State);

            r.Raw = Ui.Mono("—", T.FsNano, T.Dim);
            r.Raw.Margin = new Thickness(0, 6, 0, 0);
            r.Raw.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(r.Raw);

            r.Desc = Ui.Tx("—", T.FsMicro, T.Mute, T.FCn);
            r.Desc.Margin = new Thickness(0, 9, 0, 0);
            r.Desc.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(r.Desc);

            // 不写 MinHeight：Reflow 的一行会按最高的一张卡等高，三张自然齐平；
            // 写死 148 会在文字只有一行的那两张底下留 40px 空白。
            Border card = Ui.Inset(sp, T.PadInset);
            Tip.Attach(card, tip);
            return card;
        }

        private void RefreshPipeline()
        {
            if (_hags == null) return;
            ApplyPipe(_hags, ReadHags());
            ApplyPipe(_mpo, ReadMpo());
            ApplyPipe(_fse, ReadFse());
            // 5 秒一次：三个键都是 IO，用户不改设置时它每秒读三遍纯属浪费（见 OnEnter 的节拍说明）。
            if (_pipeMeta != null) _pipeMeta.Text = "5S POLL · " + DateTime.Now.ToString("HH:mm:ss");
        }

        private void ApplyPipe(PipeRef r, PipeState p)
        {
            r.Dot.Fill = T.S(p.Ok ? T.Ok : T.Warn);
            r.State.Text = p.State;
            r.State.Foreground = T.S(p.Ok ? T.Ok : T.Warn);
            r.Raw.Text = p.Raw;
            r.Desc.Text = p.Desc;
        }

        private static PipeState ReadHags()
        {
            PipeState p = new PipeState();
            RegValue v = Reg.Read("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");

            if (v.Denied)
            {
                p.Ok = false;
                p.State = "读取被拒绝";
                p.Raw = "HwSchMode = —";
                p.Desc = "需要管理员权限才能读取 GraphicsDrivers 键；动作区的写入同样受权限限制。";
                return p;
            }
            if (v.Error != null)
            {
                p.Ok = false;
                p.State = "读取失败";
                p.Raw = "HwSchMode = —";
                p.Desc = "注册表读取出错（非权限）：" + v.Error;
                return p;
            }

            int mode = TryReadInt(v, "gfx.pipe.hags");

            p.Ok = mode == 2;
            if (mode == 2)
            {
                p.State = "已启用";
                p.Desc = "命令提交与显存管理交给 GPU 自主调度，CPU 侧提交开销更低。";
            }
            else if (mode == 1)
            {
                p.State = "已关闭";
                p.Desc = "走 CPU 提交路径；可在本页动作中开启，重启后生效。";
            }
            else if (v.Existed)
            {
                p.State = "值异常";
                p.Desc = "HwSchMode 不是 1 或 2，驱动可能忽略该设置；建议重新写入。";
            }
            else
            {
                p.State = "未设置";
                p.Desc = "系统按默认值处理；开启后需重启，老驱动与录屏软件组合可能有兼容问题。";
            }
            p.Raw = "HwSchMode = " + ValueText(v);
            return p;
        }

        private static PipeState ReadMpo()
        {
            PipeState p = new PipeState();
            RegValue v = Reg.Read("HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");

            if (v.Denied)
            {
                p.Ok = false;
                p.State = "读取被拒绝";
                p.Raw = "OverlayTestMode = —";
                p.Desc = "需要管理员权限才能读取 Dwm 键；当前无法确认 MPO 是否被禁用。";
                return p;
            }
            if (v.Error != null)
            {
                // 读失败时 Existed=false，若继续往下走 p.Ok=!Existed 会谎报"MPO 已启用"。
                p.Ok = false;
                p.State = "读取失败";
                p.Raw = "OverlayTestMode = —";
                p.Desc = "注册表读取出错（非权限）：" + v.Error;
                return p;
            }

            p.Ok = !v.Existed;
            if (v.Existed)
            {
                p.State = "已被禁用";
                p.Raw = "OverlayTestMode = " + ValueText(v);
                p.Desc = "DWM 退回额外合成拷贝，带宽与功耗上升；多为第三方脚本残留，可在动作区还原。";
            }
            else
            {
                p.State = "已启用";
                p.Raw = "OverlayTestMode = —";
                p.Desc = "该值不存在即 MPO 启用：多平面叠加参与合成，减少一次全屏拷贝。";
            }
            return p;
        }

        private static PipeState ReadFse()
        {
            PipeState p = new PipeState();
            RegValue v = Reg.Read("HKCU", @"System\GameConfigStore", "GameDVR_FSEBehaviorMode");

            if (v.Denied)
            {
                p.Ok = false;
                p.State = "读取被拒绝";
                p.Raw = "GameDVR_FSEBehaviorMode = —";
                p.Desc = "当前用户没有读取 GameConfigStore 的权限；无法确认独立翻转状态。";
                return p;
            }
            if (v.Error != null)
            {
                p.Ok = false;
                p.State = "读取失败";
                p.Raw = "GameDVR_FSEBehaviorMode = —";
                p.Desc = "注册表读取出错（非权限）：" + v.Error;
                return p;
            }

            int mode = TryReadInt(v, "gfx.pipe.hags");

            p.Ok = mode == 2;
            if (mode == 2)
            {
                p.State = "已强制";
                p.Raw = "GameDVR_FSEBehaviorMode = " + ValueText(v);
                p.Desc = "绕过全屏优化兼容层，走独立 flip 呈现；个别老游戏可能黑屏或无法切窗。";
            }
            else
            {
                p.State = "未强制";
                p.Raw = "GameDVR_FSEBehaviorMode = " + ValueText(v);
                p.Desc = "由系统「全屏优化」接管，兼容性优先，呈现延迟略高。";
            }
            return p;
        }

        /// <summary>注册表值的展示文本；值不存在或为空时统一显示破折号。</summary>
        private static string ValueText(RegValue v)
        {
            if (v == null || !v.Existed || v.Value == null) return "—";
            string s = Reg.ToText(v.Value);
            return string.IsNullOrEmpty(s) ? "—" : s;
        }

        private static int TryReadInt(RegValue v, string diagTag)
        {
            if (v == null || !v.Existed || v.Value == null) return -1;
            try { return Convert.ToInt32(v.Value); }
            catch (Exception ex) { Diag.Fail(diagTag, ex); return -1; }
        }

    }
}
