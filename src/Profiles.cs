// ============================================================================
//  Profiles.cs — 战术档案：每个游戏的亲和性 / GPU 首选项 / 定时器策略
//  Rev82：档案库被守护线程（GameWatcher 巡检）与 UI 线程同时读写。旧实现把
//  List 直接公开出去且无锁——UI 侧 Remove 移位时，巡检线程正按旧 Count 索引，
//  越界异常会直接终结巡检线程，守护静默失效。这里照 Ledger 的口径收敛：
//  私有不可变快照 + 一把状态锁 + 读取免锁；落盘失败上报 Diag，绝不静默清空用户档案。
//
//  Rev86：状态锁不再包住文件 IO（旧实现 Save 持锁写盘，还在锁内调 Diag），
//  并给落盘加了快照版本号——两条线程同时「改一条 + 存一次盘」不会再互相覆盖。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Go
{
    public class GameProfile
    {
        private string _priorityMode;
        private string _ccdMode;

        public string Id { get; set; }
        public string Name { get; set; }
        public string Exe { get; set; }
        public bool PinCcd0 { get; set; }
        public bool HighGpu { get; set; }
        public bool Timer { get; set; }
        public string Created { get; set; }

        // Rev88 战报回写与高阶调优策略（向后兼容旧 profiles.json）
        public int Hits { get; set; }
        public string LastUsed { get; set; }
        public long TotalPlaySec { get; set; }
        public double LastP99Us { get; set; }
        public double LastMaxJitterUs { get; set; }
        public string LastVerdict { get; set; }

        public string PriorityMode
        {
            get { return string.IsNullOrEmpty(_priorityMode) ? "Keep" : _priorityMode; }
            set { _priorityMode = value; }
        }

        public string CcdMode
        {
            get
            {
                if (!string.IsNullOrEmpty(_ccdMode)) return _ccdMode;
                return PinCcd0 ? "Ccd0" : "Auto";
            }
            set { _ccdMode = value; }
        }

        public bool EcoBackground { get; set; }
    }

    public class ProfileStore
    {
        private readonly string _file;
        /// <summary>唯一的状态锁：只护内存快照与版本号，锁内不做 IO、不回调外部。</summary>
        private readonly object _writer = new object();
        /// <summary>文件锁：串行化「临时文件 + 替换」。锁内没有托管回调。</summary>
        private readonly object _fileLock = new object();
        /// <summary>不可变快照：写者整体替换引用，读者永远看到完整的一代。</summary>
        private GameProfile[] _items = new GameProfile[0];
        /// <summary>每次内容变化 +1。守护用它判断「档案变了要重建目标表」——
        /// 只看条数会漏掉「改了既有档案的 exe」（条数不变，行为必须变）。</summary>
        private volatile int _version;
        /// <summary>读取阶段失败过：内存列表不代表磁盘内容，禁止覆盖写（同 Ledger）。</summary>
        private volatile bool _unreadable;
        /// <summary>内存快照的版本号（只在 _writer 内自增）。</summary>
        private int _saveSeq;
        /// <summary>已落盘的版本号（只在 _fileLock 内推进）。</summary>
        private int _savedSeq;

        public static volatile string DirOverride;

        public ProfileStore()
        {
            string dir = DirOverride ?? Ledger.DirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameOptimizer");
            _file = Path.Combine(dir, "profiles.json");
            Load();
        }

        public ProfileStore(string customFilePath)
        {
            _file = customFilePath;
            Load();
        }

        public string FilePath { get { return _file; } }

        /// <summary>当前档案数（线程安全）。</summary>
        public int Count { get { return _items.Length; } }

        /// <summary>内容版本号（线程安全）：任何增删改都会变。</summary>
        public int Version { get { return _version; } }

        /// <summary>取一份快照供遍历/索引。调用方拿到的是独立数组，遍历期间增删不会越界。</summary>
        public GameProfile[] Snapshot()
        {
            GameProfile[] snap = _items;
            return snap.Length == 0 ? new GameProfile[0] : (GameProfile[])snap.Clone();
        }

        private void Load()
        {
            string txt;
            try
            {
                if (!File.Exists(_file)) return;
                txt = File.ReadAllText(_file, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 读不出来（被占用/IO 错）：从空列表起步，但禁止本次会话覆盖写，
                // 否则一次临时占用就能把用户真实档案冲掉。
                _unreadable = true;
                Diag.Fail("profiles.load", ex);
                return;
            }
            if (string.IsNullOrWhiteSpace(txt)) return;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                GameProfile[] arr = js.Deserialize<GameProfile[]>(txt);
                if (arr != null) _items = arr;
            }
            catch (Exception ex)
            {
                // 损坏：先留证再清空，绝不静默丢弃（同 Ledger/BenchHistory 的隔离口径）。
                Diag.Fail("profiles.parse", ex);
                Quarantine();
                _items = new GameProfile[0];
            }
        }

        /// <summary>损坏的档案原样存证。同一秒内两次损坏也各留一份（旧实现撞名就放弃）。</summary>
        private void Quarantine()
        {
            try
            {
                if (!File.Exists(_file)) return;
                string stem = _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dst = stem;
                for (int i = 2; i < 100 && File.Exists(dst); i++) dst = stem + "-" + i;
                File.Move(_file, dst);
            }
            catch (Exception mx) { Diag.Fail("profiles.quarantine", mx); }
        }

        /// <summary>原子落盘。返回 false 表示没写下去（调用方据此提示，绝不假装已保存）。</summary>
        public bool Save()
        {
            string json = null;
            int seq = 0;
            bool blocked = false;
            lock (_writer)
            {
                // 锁内只做一次 File.Exists（廉价元数据）与序列化，不写盘、不回调外部。
                if (_unreadable && File.Exists(_file)) blocked = true;
                else
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    json = js.Serialize(_items);
                    seq = _saveSeq;
                }
            }
            if (blocked)
            {
                Diag.Fail("profiles.save", new InvalidOperationException("档案读取失败，为保护现有数据本次拒绝覆盖写入"));
                return false;
            }

            Exception cleanupFail = null;
            bool ok = true;
            try
            {
                lock (_fileLock)
                {
                    if (seq < _savedSeq) return true;   // 更新的快照已经落盘，本次跳过
                    string dir = Path.GetDirectoryName(_file);
                    // 临时名带 Guid：AutoPilot.ApplyExtreme 与 UI 可能并发 Save，
                    // 写死 ".tmp" 会让两条路径撞同一个临时文件。
                    string tmp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(tmp, json, new UTF8Encoding(false));
                        if (File.Exists(_file)) File.Replace(tmp, _file, null);
                        else File.Move(tmp, _file);
                        _savedSeq = seq;
                    }
                    catch
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); }
                        catch (Exception cx) { cleanupFail = cx; }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("profiles.save", ex);
                ok = false;
            }
            if (cleanupFail != null) Diag.Fail("profiles.tmpclean", cleanupFail);
            return ok;
        }

        /// <summary>追加并落盘。返回落盘结果。</summary>
        public bool Add(GameProfile p)
        {
            if (p == null) return false;
            Append(p);
            return Save();
        }

        /// <summary>只追加不落盘（离屏演示 / 自检用，避免写用户真实档案）。</summary>
        public void AddNoSave(GameProfile p)
        {
            if (p == null) return;
            Append(p);
        }

        /// <summary>批量追加后单次落盘（批量建档不再每条写一次盘）。</summary>
        public bool AddRange(IEnumerable<GameProfile> ps)
        {
            if (ps == null) return false;
            lock (_writer)
            {
                List<GameProfile> next = new List<GameProfile>(_items);
                foreach (GameProfile p in ps) if (p != null) next.Add(p);
                _items = next.ToArray();
                _version++;
                _saveSeq++;
            }
            return Save();
        }

        public bool Remove(string id)
        {
            bool changed = false;
            lock (_writer)
            {
                GameProfile[] snap = _items;
                List<GameProfile> next = new List<GameProfile>(snap.Length);
                for (int i = 0; i < snap.Length; i++)
                {
                    if (snap[i].Id == id) { changed = true; continue; }
                    next.Add(snap[i]);
                }
                if (!changed) return true;
                _items = next.ToArray();
                _version++;
                _saveSeq++;
            }
            return Save();
        }

        /// <summary>按引用移除且不落盘（自检收尾用）。</summary>
        public void RemoveNoSave(GameProfile p)
        {
            if (p == null) return;
            lock (_writer)
            {
                GameProfile[] snap = _items;
                List<GameProfile> next = new List<GameProfile>(snap.Length);
                for (int i = 0; i < snap.Length; i++)
                {
                    if (object.ReferenceEquals(snap[i], p)) continue;
                    next.Add(snap[i]);
                }
                if (next.Count == snap.Length) return;
                _items = next.ToArray();
                _version++;
                _saveSeq++;
            }
        }

        public GameProfile FindByExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            GameProfile[] snap = _items;
            for (int i = 0; i < snap.Length; i++)
                if (string.Equals(snap[i].Exe, exe, StringComparison.OrdinalIgnoreCase)) return snap[i];

            string qName = Path.GetFileName(exe);
            if (string.IsNullOrEmpty(qName)) return null;
            bool qNoDir = !HasDirSep(exe);
            for (int i = 0; i < snap.Length; i++)
            {
                string sExe = snap[i].Exe;
                if (string.IsNullOrEmpty(sExe)) continue;
                if (!qNoDir && HasDirSep(sExe)) continue;
                if (string.Equals(Path.GetFileName(sExe), qName, StringComparison.OrdinalIgnoreCase))
                    return snap[i];
            }
            return null;
        }

        private static bool HasDirSep(string path)
        {
            return path.IndexOf('\\') >= 0 || path.IndexOf('/') >= 0;
        }

        public GameProfile FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GameProfile[] snap = _items;
            for (int i = 0; i < snap.Length; i++)
                if (string.Equals(snap[i].Id, id, StringComparison.OrdinalIgnoreCase)) return snap[i];
            return null;
        }

        /// <summary>线程安全地原子修改指定档案，并递增 _version/_saveSeq 后按需落盘。</summary>
        public bool UpdateProfile(string id, Action<GameProfile> mutator)
        {
            return UpdateProfile(id, mutator, true);
        }

        public bool UpdateProfile(string id, Action<GameProfile> mutator, bool persist)
        {
            if (string.IsNullOrEmpty(id) || mutator == null) return false;
            bool matched = false;
            lock (_writer)
            {
                GameProfile[] snap = _items;
                for (int i = 0; i < snap.Length; i++)
                {
                    if (!string.Equals(snap[i].Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                    mutator(snap[i]);
                    _version++;
                    _saveSeq++;
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
            return persist ? Save() : true;
        }

        public bool RecordSession(string profileId, string exe, Go.Daemon.SessionReport rep)
        {
            return RecordSession(profileId, exe, rep, true);
        }

        public bool RecordSession(string profileId, string exe, Go.Daemon.SessionReport rep, bool persist)
        {
            if (rep == null) return false;
            bool matched = false;
            lock (_writer)
            {
                GameProfile[] snap = _items;
                GameProfile target = null;
                if (!string.IsNullOrEmpty(profileId))
                {
                    for (int i = 0; i < snap.Length; i++)
                    {
                        if (string.Equals(snap[i].Id, profileId, StringComparison.OrdinalIgnoreCase))
                        {
                            target = snap[i];
                            break;
                        }
                    }
                }
                if (target == null && !string.IsNullOrEmpty(exe))
                {
                    for (int i = 0; i < snap.Length; i++)
                    {
                        if (string.Equals(snap[i].Exe, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            target = snap[i];
                            break;
                        }
                    }
                    if (target == null)
                    {
                        string qName = Path.GetFileName(exe);
                        bool qNoDir = !HasDirSep(exe);
                        for (int i = 0; i < snap.Length; i++)
                        {
                            string sExe = snap[i].Exe;
                            if (string.IsNullOrEmpty(sExe)) continue;
                            if (!qNoDir && HasDirSep(sExe)) continue;
                            if (string.Equals(Path.GetFileName(sExe), qName, StringComparison.OrdinalIgnoreCase))
                            {
                                target = snap[i];
                                break;
                            }
                        }
                    }
                }
                if (target != null)
                {
                    target.Hits++;
                    target.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    long sec = (long)Math.Max(0, Math.Round(rep.DurationSec));
                    target.TotalPlaySec += sec;
                    target.LastP99Us = rep.AvgP99Us;
                    target.LastMaxJitterUs = rep.MaxJitterUs;
                    target.LastVerdict = rep.Verdict ?? "";
                    _version++;
                    _saveSeq++;
                    matched = true;
                }
            }
            if (!matched) return false;
            return persist ? Save() : true;
        }

        private void Append(GameProfile p)
        {
            lock (_writer)
            {
                GameProfile[] snap = _items;
                GameProfile[] next = new GameProfile[snap.Length + 1];
                Array.Copy(snap, next, snap.Length);
                next[snap.Length] = p;
                _items = next;
                _version++;
                _saveSeq++;
            }
        }
    }
}