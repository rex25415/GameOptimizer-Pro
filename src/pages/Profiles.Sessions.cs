// ============================================================================
//  Profiles.Sessions.cs — 历史战报回放（Profiles 分片）
//  合并当前会话 LastSession 与磁盘持久化 SessionHistory.LoadNewestFirst()，
//  让用户在切换或重启后仍能点选回放最近几局的抖动 P99、卡顿脉冲与后台抢占排行。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Go.Pages
{
    public partial class Profiles : Page
    {
        private int _selectedSessionIdx;
        private Go.Daemon.SessionReport _dismissedLive;
        private Go.Daemon.SessionReport _synthLiveRef;
        private string _synthStartedAt = "";

        private sealed class ReplayEntry
        {
            public Go.Daemon.SessionHistoryItem Item;
            public Go.Daemon.SessionReport Live;
        }

        private void ShowLastSession()
        {
            if (_reportHost == null) return;
            _reportHost.Children.Clear();

            List<ReplayEntry> entries = CollectReplayEntries();
            if (entries.Count == 0)
            {
                _reportHost.Visibility = Visibility.Collapsed;
                return;
            }
            _reportHost.Visibility = Visibility.Visible;

            if (_selectedSessionIdx < 0 || _selectedSessionIdx >= entries.Count)
                _selectedSessionIdx = 0;

            ReplayEntry cur = entries[_selectedSessionIdx];
            Go.Daemon.SessionHistoryItem r = cur.Item;
            bool jitterOk = r.JitterSamples > 0;
            bool calm = jitterOk && r.StutterWindows == 0;
            uint tint = !jitterOk ? T.Mute : (calm ? T.Ok : T.Warn);

            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleLeft = new StackPanel();
            titleLeft.Orientation = Orientation.Horizontal;
            titleLeft.VerticalAlignment = VerticalAlignment.Center;
            titleLeft.Children.Add(Ui.Label("POST-MORTEM \u00B7 \u5386\u53F2\u6218\u62A5\u56DE\u653E", tint));
            titleLeft.Children.Add(Ui.Mono("  \u00B7  " + entries.Count + " \u5C40\u8BB0\u5F55", T.FsNano, T.Mute));
            head.Children.Add(titleLeft);

            Btn clear = new Btn("\u6E05\u7A7A\u6218\u62A5", BtnKind.Quiet);
            clear.Height = T.BtnHSm;
            clear.Compact = true;
            clear.Click += delegate
            {
                Go.Daemon.SessionHistory.Clear();
                if (C != null && C.Daemon != null) _dismissedLive = C.Daemon.LastSession;
                _selectedSessionIdx = 0;
                ShowLastSession();
                if (C != null && C.Shell != null) C.Shell.Toast("\u5DF2\u6E05\u7A7A\u5386\u53F2\u6218\u62A5\u8BB0\u5F55", true);
            };
            Grid.SetColumn(clear, 1);
            head.Children.Add(clear);
            body.Children.Add(head);

            if (entries.Count > 1)
            {
                int tabCount = Math.Min(6, entries.Count);
                Ui.SegTabItem[] tabs = new Ui.SegTabItem[tabCount];
                for (int i = 0; i < tabCount; i++)
                {
                    Go.Daemon.SessionHistoryItem it = entries[i].Item;
                    string nm = ShortName(it.ProfileName, 10);
                    string tm = string.IsNullOrEmpty(it.StartedTime) ? ("#" + (i + 1)) : it.StartedTime;
                    tabs[i] = new Ui.SegTabItem(i.ToString(), nm + " " + tm,
                        (it.StartedAt ?? "") + " \u00B7 " + (it.DurationText ?? ""));
                }
                Ui.SegTabs sel = new Ui.SegTabs(tabs, Math.Min(_selectedSessionIdx, tabCount - 1));
                sel.Margin = new Thickness(0, 9, 0, 2);
                sel.Changed += delegate
                {
                    _selectedSessionIdx = sel.SelectedIndex;
                    ShowLastSession();
                };
                body.Children.Add(sel);
            }

            string when = string.IsNullOrEmpty(r.StartedAt) ? r.StartedTime : r.StartedAt;
            string dur = string.IsNullOrEmpty(r.DurationText) ? FormatDurationShort((long)r.DurationSec) : r.DurationText;
            TextBlock sub = Ui.Mono(r.ProfileName + "  \u00B7  " + when + " \u8D77  \u00B7  \u65F6\u957F " + dur, T.FsNano, T.Mute);
            sub.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(sub);

            string verdictText = string.IsNullOrEmpty(r.Verdict) ? "\u672C\u5C40\u672A\u89C1\u660E\u663E\u6296\u52A8\u5F02\u5E38" : r.Verdict;
            TextBlock verdict = Ui.Tx(verdictText, T.FsBody, !jitterOk ? T.Mute : (calm ? T.Hi : T.Warn), T.FCn);
            verdict.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(verdict);

            string eco = r.EcoThrottledCount > 0 ? ("  \u00B7  \u540E\u53F0\u964D\u6548 " + r.EcoThrottledCount + " \u4E2A") : "";
            string detail = jitterOk
                ? ("P99 " + r.AvgP99Us.ToString("0") + " \u00B5s  \u00B7  \u5CF0\u503C " + r.MaxJitterUs.ToString("0")
                   + " \u00B5s  \u00B7  \u5361\u987F\u8109\u51B2 " + r.StutterWindows + " \u6B21  \u00B7  "
                   + r.JitterSamples + "/" + r.Samples + " \u62CD\u91C7\u5230\u6296\u52A8" + eco)
                : ("\u540E\u53F0\u5360\u7528 " + r.Samples + " \u6B21\u91C7\u6837  \u00B7  \u6296\u52A8\u672A\u91C7\u6837\uFF08\u7A97\u53E3\u6536\u8D77\u65F6\u63A2\u9488\u505C\u91C7\uFF09" + eco);
            TextBlock dt = Ui.Mono(detail, T.FsNano, T.Dim);
            dt.Margin = new Thickness(0, 5, 0, 0);
            body.Children.Add(dt);

            AppendCulprits(body, cur);

            Border box = Ui.RowShell(body, tint, 13);
            box.BorderThickness = new Thickness(0, 0, 0, 1);
            box.BorderBrush = T.S(T.LineSoft);
            _reportHost.Children.Add(box);
        }

        private List<ReplayEntry> CollectReplayEntries()
        {
            List<ReplayEntry> list = new List<ReplayEntry>();
            List<Go.Daemon.SessionHistoryItem> hist = null;
            try { hist = Go.Daemon.SessionHistory.LoadNewestFirst(); }
            catch (Exception ex) { Diag.Fail("profiles.sesshist", ex); }

            Go.Daemon.DaemonHost d = C == null ? null : C.Daemon;
            Go.Daemon.SessionReport live = d == null ? null : d.LastSession;
            if (live == _dismissedLive) live = null;

            if (live != null)
            {
                bool matched = hist != null && hist.Count > 0
                    && string.Equals(hist[0].ProfileName, live.ProfileName, StringComparison.Ordinal)
                    && string.Equals(hist[0].StartedTime, live.Started, StringComparison.Ordinal);
                if (!matched)
                {
                    GameProfile gp = FindProfileByName(live.ProfileName, d);
                    string pid = gp != null && gp.Id != null ? gp.Id : "";
                    string exe = gp != null && gp.Exe != null ? gp.Exe : "";
                    int ecoCount = (d != null && d.Pilot != null) ? d.Pilot.EcoThrottledCount : 0;
                    if (!object.ReferenceEquals(_synthLiveRef, live))
                    {
                        _synthLiveRef = live;
                        string day = (gp != null && !string.IsNullOrEmpty(gp.LastUsed) && gp.LastUsed.Length >= 10)
                            ? gp.LastUsed.Substring(0, 10)
                            : DateTime.Now.ToString("yyyy-MM-dd");
                        _synthStartedAt = (!string.IsNullOrEmpty(live.Started) && live.Started.Length == 5)
                            ? (day + " " + live.Started + ":00")
                            : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    Go.Daemon.SessionHistoryItem synth = Go.Daemon.SessionHistory.FromReport(live, pid, exe, ecoCount);
                    synth.StartedAt = _synthStartedAt;
                    list.Add(new ReplayEntry { Item = synth, Live = live });
                }
            }

            if (hist != null)
            {
                for (int i = 0; i < hist.Count; i++)
                {
                    Go.Daemon.SessionHistoryItem it = hist[i];
                    if (it == null) continue;
                    Go.Daemon.SessionReport matchedLive = (i == 0 && live != null
                        && string.Equals(it.ProfileName, live.ProfileName, StringComparison.Ordinal)
                        && string.Equals(it.StartedTime, live.Started, StringComparison.Ordinal))
                        ? live : null;
                    list.Add(new ReplayEntry { Item = it, Live = matchedLive });
                }
            }
            return list;
        }

        private static void AppendCulprits(StackPanel body, ReplayEntry cur)
        {
            List<Go.Daemon.Culprit> rows = new List<Go.Daemon.Culprit>();
            if (cur.Live != null && cur.Live.Top != null && cur.Live.Top.Count > 0)
            {
                int show = Math.Min(3, cur.Live.Top.Count);
                for (int i = 0; i < show; i++) rows.Add(cur.Live.Top[i]);
            }
            else if (!string.IsNullOrEmpty(cur.Item.TopCulprit))
            {
                rows.Add(new Go.Daemon.Culprit { Name = cur.Item.TopCulprit, CpuPercent = cur.Item.TopCulpritPct });
            }
            if (rows.Count == 0) return;

            Border rule = Ui.Rule(T.LineSoft, 0);
            rule.Margin = new Thickness(0, 11, 0, 9);
            body.Children.Add(rule);
            body.Children.Add(Ui.Label("\u540C\u671F\u540E\u53F0 CPU \u62A2\u5360\u6392\u884C", T.Mute));
            body.Children.Add(Gap(7));

            for (int i = 0; i < rows.Count; i++)
            {
                Go.Daemon.Culprit cu = rows[i];
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColVal) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColTag) });
                row.Margin = new Thickness(0, 2, 0, 2);

                TextBlock nm = Ui.Mono(cu.Name, T.FsNano, T.Text);
                nm.TextTrimming = TextTrimming.CharacterEllipsis;
                nm.TextWrapping = TextWrapping.NoWrap;
                row.Children.Add(nm);

                SegBar bar = new SegBar(Math.Min(1, cu.CpuPercent / 40.0));
                bar.Segments = 18;
                bar.Height = 5;
                bar.VerticalAlignment = VerticalAlignment.Center;
                bar.Tint = cu.CpuPercent > 15 ? T.Warn : T.AccentDim;
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                TextBlock pct = Ui.Mono(cu.CpuPercent.ToString("0.0") + "%", T.FsNano, T.Dim);
                pct.HorizontalAlignment = HorizontalAlignment.Right;
                pct.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pct, 2);
                row.Children.Add(pct);
                body.Children.Add(row);
            }
            TextBlock note = Ui.Mono("\u767E\u5206\u6BD4\u4E3A\u300C\u5360\u5355\u6838\u300D\uFF0C\u5373\u8BE5\u8FDB\u7A0B\u5728\u672C\u5C40\u5E73\u5747\u5403\u6389\u7684\u5355\u6838\u7B97\u529B", T.FsNano, T.Mute);
            note.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(note);
        }

        private GameProfile FindProfileByName(string name, Go.Daemon.DaemonHost d)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (d != null && d.Watcher != null && d.Watcher.Active != null
                && string.Equals(d.Watcher.Active.Name, name, StringComparison.OrdinalIgnoreCase))
                return d.Watcher.Active;
            if (C == null || Store == null) return null;
            GameProfile[] items = Store.Snapshot();
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null && string.Equals(items[i].Name, name, StringComparison.Ordinal)) return items[i];
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null && string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase)) return items[i];
            return null;
        }

        private static string ShortName(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "\u672A\u547D\u540D";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026";
        }
    }
}
