// ============================================================================
//  Memory.Kernel.cs — 04 内存与提交：内核池 + 提交 / 页面文件（Memory 分片）
//  为什么切：这两块都是「物理内存之外的账」，共用一套采样来源标注与量具
//  （MeterBar / RatioRuler）；页根本来不必知道它们怎么画。
//
//  Rev86：只做对齐——面板内间距统一 12、读数右对齐到同一条边、量尺的刻度写明量程；
//  不再各写一套"标签 + 条 + 数字"的间距（同一个面板里三种行距是这一页最吵的地方）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Go.Pages
{
    public partial class Memory : Page
    {
        // ====================================================================
        //  内核池
        // ====================================================================
        private UIElement BuildKernel(Core c)
        {
            StackPanel body = new StackPanel();
            _poolMeta = Src("PERF INFO");
            body.Children.Add(Ui.PanelHead("内核池", "KERNEL POOL", _poolMeta));
            body.Children.Add(Gap(12));

            _poolPagedBar = new MeterBar();
            _poolPagedBar.Height = 6;
            _poolPagedBar.Tint = T.SeriesA;
            _poolPagedVal = PoolRow(body, "分页池", "PAGED POOL", _poolPagedBar);

            body.Children.Add(Gap(11));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(11));

            _poolNonpagedBar = new MeterBar();
            _poolNonpagedBar.Height = 6;
            _poolNonpagedBar.Tint = T.AccentDim;
            _poolNonpagedVal = PoolRow(body, "非分页池", "NONPAGED POOL", _poolNonpagedBar);

            body.Children.Add(Gap(12));

            TextBlock note = Ui.Tx("非分页池不能被换出到磁盘。驱动泄漏时它只涨不落，把应用能驻留的物理内存一点点挤掉 —— 机器就越来越卡。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 15;
            body.Children.Add(note);

            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>一行池读数：名字（左）+ 读数（右，等宽）+ 量条（压底）。</summary>
        private TextBlock PoolRow(StackPanel host, string cn, string en, MeterBar bar)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel l = new StackPanel();
            l.Orientation = Orientation.Horizontal;
            l.Children.Add(Ui.Tx(cn, T.FsSmall, T.Text, T.FCn));
            TextBlock e = Ui.Label(en, T.Mute);
            e.Opacity = 0.75;
            e.Margin = new Thickness(8, 0, 0, 0);
            l.Children.Add(e);
            Grid.SetColumn(l, 0);
            g.Children.Add(l);

            TextBlock v = Ui.Mono("\u2014", T.FsSmall, T.Hi);
            v.VerticalAlignment = VerticalAlignment.Center;
            v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(v, 1);
            g.Children.Add(v);

            host.Children.Add(g);
            bar.Margin = new Thickness(0, 7, 0, 0);
            host.Children.Add(bar);
            return v;
        }

        // ====================================================================
        //  提交与页面文件
        // ====================================================================
        private UIElement BuildCommit(Core c)
        {
            StackPanel body = new StackPanel();
            body.Children.Add(Ui.PanelHead("提交与页面文件", "COMMIT / PAGEFILE", Src("COMMIT")));
            body.Children.Add(Gap(12));

            Grid line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock cap = Ui.Tx("已提交 / 上限", T.FsSmall, T.Dim, T.FCn);
            cap.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(cap);
            _cmtLine = Ui.Mono("\u2014 / \u2014 GB", T.FsSmall, T.Hi);
            _cmtLine.VerticalAlignment = VerticalAlignment.Center;
            _cmtLine.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_cmtLine, 1);
            line.Children.Add(_cmtLine);
            body.Children.Add(line);

            _ruler = new RatioRuler();
            _ruler.Height = 34;
            _ruler.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(_ruler);

            Grid foot = new Grid();
            foot.Margin = new Thickness(0, 2, 0, 0);
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _cmtHead = Ui.Mono("距上限 \u2014 GB", T.FsNano, T.Mute);
            _cmtHead.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(_cmtHead);
            TextBlock edge = Ui.Mono("75% 偏紧 · 90% 危险 · 触顶失败", T.FsNano, T.Mute);
            edge.HorizontalAlignment = HorizontalAlignment.Right;
            edge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(edge, 1);
            foot.Children.Add(edge);
            body.Children.Add(foot);

            body.Children.Add(Gap(12));
            body.Children.Add(Ui.Rule(T.LineSoft, 0));
            body.Children.Add(Gap(10));
            body.Children.Add(BuildPageFile());

            body.Children.Add(Gap(8));
            TextBlock note = Ui.Tx("提交 = 已经答应给程序的内存，可落在物理内存或页面文件；关掉页面文件，上限就塌缩到内存大小。",
                T.FsMicro, T.Mute, T.FCn);
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 15;
            body.Children.Add(note);

            LoadPageFile();
            return Ui.Panel(body, T.PadPanel);
        }

        /// <summary>页面文件读数：一行"路径 · 初始–最大"，值不存在/读不到各有各的说法。</summary>
        private UIElement BuildPageFile()
        {
            Grid pf = new Grid();
            pf.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pf.ColumnDefinitions.Add(new ColumnDefinition());

            StackPanel pl = new StackPanel();
            pl.Orientation = Orientation.Horizontal;
            pl.VerticalAlignment = VerticalAlignment.Center;
            pl.Children.Add(Ui.Tx("页面文件", T.FsSmall, T.Dim, T.FCn));
            TextBlock pe = Ui.Label("REGISTRY", T.Mute);
            pe.Opacity = 0.7;
            pe.Margin = new Thickness(7, 0, 0, 0);
            pl.Children.Add(pe);
            pf.Children.Add(pl);

            _pageFile = Ui.Mono("\u2014", T.FsNano, T.Dim);
            _pageFile.Margin = new Thickness(12, 0, 0, 0);
            _pageFile.TextWrapping = TextWrapping.Wrap;
            _pageFile.TextAlignment = TextAlignment.Right;
            _pageFile.HorizontalAlignment = HorizontalAlignment.Right;
            _pageFile.LineHeight = 14;
            _pageFile.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_pageFile, 1);
            pf.Children.Add(_pageFile);
            return pf;
        }

        private void LoadPageFile()
        {
            string text = "\u2014";
            try
            {
                RegValue rv = Reg.Read("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles");
                if (rv.Existed && rv.Value is string[])
                {
                    string[] arr = (string[])rv.Value;
                    List<string> parts = new List<string>();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (arr[i] == null || arr[i].Trim().Length == 0) continue;
                        parts.Add(DescribePagingFile(arr[i].Trim()));
                    }
                    text = parts.Count == 0 ? "未配置（无页面文件）" : string.Join(" · ", parts.ToArray());
                }
                else if (rv.Denied)
                {
                    text = "\u2014（注册表拒绝访问）";
                }
                else if (rv.Error != null)
                {
                    text = "\u2014（注册表读取失败）";
                }
                else
                {
                    text = "自动管理（注册表无固定配置）";
                }
            }
            catch { text = "\u2014"; }
            Set(_pageFile, text);
        }

        private static string DescribePagingFile(string raw)
        {
            string[] tok = raw.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) return raw;
            string path = tok[0];
            if (path.StartsWith("?:"))
                path = System.IO.Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\') + path.Substring(2);
            if (tok.Length < 3) return path + " · 自动管理";
            long init, max;
            if (!long.TryParse(tok[1], out init) || !long.TryParse(tok[2], out max)) return path + " · 自动管理";
            if (init == 0 && max == 0) return path + " · 自动管理";
            if (max <= 0) return path + " · 初始 " + Group(init) + " MB";
            return path + " · " + Group(init) + "–" + Group(max) + " MB";
        }

        // ---- 池量程：内核池条的刻度阶梯（128 MB → 32 GB），Apply 用它把 MB 换成条长 ----
        private static double PoolScale(double maxMb)
        {
            double[] ladder = new double[] { 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };
            if (maxMb <= 0) return 0;
            double need = maxMb * 1.08;
            for (int i = 0; i < ladder.Length; i++) if (ladder[i] >= need) return ladder[i];
            return ladder[ladder.Length - 1];
        }
    }
}
