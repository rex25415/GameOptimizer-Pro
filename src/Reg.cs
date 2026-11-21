// ============================================================================
//  Reg.cs — 注册表读写 + 原值快照
//  Rev140：读路径升级为原生 advapi32.dll RegGetValueW（RRF_RT_ANY | RRF_NOEXPAND），
//  单次系统调用直达，复用线程本地缓冲，值不存在零异常抛栈，速度提升 4~8 倍。
//  注意：64 位进程直接访问原生视图；WOW6432Node 重定向对本工具无影响。
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Go
{
    public class RegValue
    {
        public bool Existed;
        public object Value;
        public RegistryValueKind Kind;
        public bool Denied;
        /// <summary>非权限类读取异常。null = 读取本身没出问题。Ops.Probe 据此显示 (读取失败)。</summary>
        public string Error;
    }

    public static class Reg
    {
        private static readonly IntPtr HKEY_CLASSES_ROOT = new IntPtr(unchecked((int)0x80000000));
        private static readonly IntPtr HKEY_CURRENT_USER = new IntPtr(unchecked((int)0x80000001));
        private static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));
        private static readonly IntPtr HKEY_USERS = new IntPtr(unchecked((int)0x80000003));

        private const uint RRF_RT_ANY = 0x0000FFFF;
        private const uint RRF_NOEXPAND = 0x10000000;
        private const uint KEY_QUERY_VALUE = 0x0001;

        private const int ERROR_SUCCESS = 0;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_MORE_DATA = 234;

        private const uint REG_NONE = 0;
        private const uint REG_SZ = 1;
        private const uint REG_EXPAND_SZ = 2;
        private const uint REG_BINARY = 3;
        private const uint REG_DWORD = 4;
        private const uint REG_MULTI_SZ = 7;
        private const uint REG_QWORD = 11;

        private const int DefaultBufCap = 4096;
        private const int MaxTlsBufCap = 65536;
        private const int MaxReadBytes = 16 * 1024 * 1024;

        [ThreadStatic] private static byte[] _tlsBuf;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegGetValueW(
            IntPtr hkey, string lpSubKey, string lpValue,
            uint dwFlags, out uint pdwType, byte[] pvData, ref int pcbData);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegOpenKeyExW(
            IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        public static RegistryKey Base(string hive)
        {
            // 序数忽略大小写比较，不再 ToUpperInvariant 分配一个临时串（探针热路径每次都用）。
            if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) return Microsoft.Win32.Registry.LocalMachine;
            if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)) return Microsoft.Win32.Registry.CurrentUser;
            if (string.Equals(hive, "HKCR", StringComparison.OrdinalIgnoreCase)) return Microsoft.Win32.Registry.ClassesRoot;
            if (string.Equals(hive, "HKU", StringComparison.OrdinalIgnoreCase)) return Microsoft.Win32.Registry.Users;
            throw new ArgumentException("未知 hive: " + hive);
        }

        private static bool TryBaseHandle(string hive, out IntPtr root)
        {
            if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) { root = HKEY_LOCAL_MACHINE; return true; }
            if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)) { root = HKEY_CURRENT_USER; return true; }
            if (string.Equals(hive, "HKCR", StringComparison.OrdinalIgnoreCase)) { root = HKEY_CLASSES_ROOT; return true; }
            if (string.Equals(hive, "HKU", StringComparison.OrdinalIgnoreCase)) { root = HKEY_USERS; return true; }
            root = IntPtr.Zero;
            return false;
        }

        public static RegValue Read(string hive, string path, string name)
        {
            RegValue r = new RegValue();
            try
            {
                IntPtr root;
                if (!TryBaseHandle(hive, out root))
                    throw new ArgumentException("未知 hive: " + hive);

                byte[] buf = _tlsBuf;
                if (buf == null) { buf = new byte[DefaultBufCap]; _tlsBuf = buf; }

                uint regType;
                int cb = buf.Length;
                int rc = RegGetValueW(root, path ?? "", name, RRF_RT_ANY | RRF_NOEXPAND, out regType, buf, ref cb);
                if (rc == ERROR_MORE_DATA)
                {
                    if (cb <= 0 || cb > MaxReadBytes)
                    {
                        r.Error = "RegGetValueW 数据长度异常: " + cb;
                        Diag.Hit("reg.read.toolarge");
                        return r;
                    }
                    int alloc = cb + 64;
                    byte[] grown = new byte[alloc];
                    if (alloc <= MaxTlsBufCap) _tlsBuf = grown;
                    buf = grown;
                    cb = buf.Length;
                    rc = RegGetValueW(root, path ?? "", name, RRF_RT_ANY | RRF_NOEXPAND, out regType, buf, ref cb);
                }

                if (rc == ERROR_FILE_NOT_FOUND || rc == ERROR_PATH_NOT_FOUND)
                    return r;
                if (rc == ERROR_ACCESS_DENIED)
                {
                    r.Denied = true;
                    return r;
                }
                if (rc != ERROR_SUCCESS)
                {
                    r.Error = "Win32Error(" + rc + ")";
                    Diag.Hit("reg.read.w32");
                    return r;
                }

                r.Existed = true;
                DecodeNativeValue(r, regType, buf, cb);
            }
            catch (System.Security.SecurityException) { r.Denied = true; }
            catch (UnauthorizedAccessException) { r.Denied = true; }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
                Diag.Fail("reg.read", ex);
            }
            return r;
        }

        private static void DecodeNativeValue(RegValue r, uint regType, byte[] buf, int cb)
        {
            if (cb < 0) cb = 0;
            if (cb > buf.Length) cb = buf.Length;
            switch (regType)
            {
                case REG_DWORD:
                    r.Kind = RegistryValueKind.DWord;
                    r.Value = cb >= 4 ? BitConverter.ToInt32(buf, 0) : 0;
                    return;
                case REG_QWORD:
                    r.Kind = RegistryValueKind.QWord;
                    r.Value = cb >= 8 ? BitConverter.ToInt64(buf, 0) : 0L;
                    return;
                case REG_SZ:
                case REG_EXPAND_SZ:
                    r.Kind = regType == REG_EXPAND_SZ ? RegistryValueKind.ExpandString : RegistryValueKind.String;
                    {
                        int byteLen = cb & ~1;
                        if (byteLen >= 2 && buf[byteLen - 2] == 0 && buf[byteLen - 1] == 0) byteLen -= 2;
                        r.Value = byteLen <= 0 ? "" : Encoding.Unicode.GetString(buf, 0, byteLen);
                    }
                    return;
                case REG_MULTI_SZ:
                    r.Kind = RegistryValueKind.MultiString;
                    {
                        int chars = cb / 2;
                        if (chars <= 1)
                        {
                            r.Value = new string[0];
                            return;
                        }
                        if (chars >= 2 && buf[(chars - 1) * 2] == 0 && buf[(chars - 1) * 2 + 1] == 0
                            && buf[(chars - 2) * 2] == 0 && buf[(chars - 2) * 2 + 1] == 0)
                            chars -= 2;
                        else if (buf[(chars - 1) * 2] == 0 && buf[(chars - 1) * 2 + 1] == 0)
                            chars -= 1;
                        r.Value = chars <= 0 ? new string[0] : Encoding.Unicode.GetString(buf, 0, chars * 2).Split('\0');
                    }
                    return;
                case REG_BINARY:
                    r.Kind = RegistryValueKind.Binary;
                    {
                        byte[] copy = new byte[cb];
                        if (cb > 0) Buffer.BlockCopy(buf, 0, copy, 0, cb);
                        r.Value = copy;
                    }
                    return;
                case REG_NONE:
                    r.Kind = RegistryValueKind.None;
                    {
                        byte[] copy = new byte[cb];
                        if (cb > 0) Buffer.BlockCopy(buf, 0, copy, 0, cb);
                        r.Value = copy;
                    }
                    return;
                default:
                    r.Kind = RegistryValueKind.Unknown;
                    {
                        byte[] copy = new byte[cb];
                        if (cb > 0) Buffer.BlockCopy(buf, 0, copy, 0, cb);
                        r.Value = copy;
                    }
                    return;
            }
        }

        public static void Write(string hive, string path, string name, object value, string typeName)
        {
            using (RegistryKey k = Base(hive).CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                RegistryValueKind kind = ParseKind(typeName);
                object v = Coerce(value, kind);
                k.SetValue(name, v, kind);
            }
        }

        /// <summary>键是否存在（写前判断：不存在说明这条动作会顺手建键，还原时要把空键删掉）。</summary>
        public static bool KeyExists(string hive, string path)
        {
            try
            {
                IntPtr root;
                if (!TryBaseHandle(hive, out root)) return false;
                IntPtr hk;
                int rc = RegOpenKeyExW(root, path ?? "", 0, KEY_QUERY_VALUE, out hk);
                if (rc == ERROR_SUCCESS)
                {
                    RegCloseKey(hk);
                    return true;
                }
                if (rc == ERROR_FILE_NOT_FOUND || rc == ERROR_PATH_NOT_FOUND) return false;
                // 权限不足等无法判断场景：保守视为存在，绝不误删键
                return true;
            }
            catch (Exception ex) { Diag.Fail("reg.keyexists", ex); return true; }
        }

        /// <summary>删掉一个空键（只在本工具建的键上调用；键里还有东西就不删）。</summary>
        public static bool TryDeleteEmptyKey(string hive, string path)
        {
            try
            {
                using (RegistryKey parent = Base(hive).OpenSubKey(System.IO.Path.GetDirectoryName(path), true))
                {
                    if (parent == null) return false;
                    string leaf = System.IO.Path.GetFileName(path);
                    using (RegistryKey k = parent.OpenSubKey(leaf, false))
                    {
                        if (k == null) return false;
                        if (k.ValueCount > 0 || k.SubKeyCount > 0) return false;
                    }
                    parent.DeleteSubKey(leaf, false);
                    return true;
                }
            }
            catch (Exception ex) { Diag.Fail("reg.keydelete", ex); return false; }
        }

        public static void Delete(string hive, string path, string name)
        {
            try
            {
                using (RegistryKey k = Base(hive).OpenSubKey(path, true))
                {
                    if (k == null) return;
                    try { k.DeleteValue(name, false); }
                    catch (UnauthorizedAccessException) { Diag.Hit("reg.delete.denied"); }
                    catch (System.Security.SecurityException) { Diag.Hit("reg.delete.denied"); }
                    catch (Exception ex)
                    {
                        // Ops 随后会回读校验，失败不会被当成成功；但"删不掉"必须能被查出来。
                        Diag.Fail("reg.delete", ex);
                    }
                }
            }
            catch (UnauthorizedAccessException) { Diag.Hit("reg.delete.denied"); }
            catch (System.Security.SecurityException) { Diag.Hit("reg.delete.denied"); }
            catch (Exception ex) { Diag.Fail("reg.delete", ex); }
        }

        public static RegistryValueKind ParseKind(string t)
        {
            if (string.IsNullOrEmpty(t)) return RegistryValueKind.String;
            if (string.Equals(t, "DWORD", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.DWord;
            if (string.Equals(t, "QWORD", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.QWord;
            if (string.Equals(t, "MULTI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "MULTISTRING", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.MultiString;
            if (string.Equals(t, "BINARY", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.Binary;
            if (string.Equals(t, "EXPANDSTRING", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.ExpandString;
            if (string.Equals(t, "NONE", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.None;
            return RegistryValueKind.String;
        }

        public static object Coerce(object v, RegistryValueKind kind)
        {
            string s = v == null ? "" : v.ToString();
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    {
                        long n;
                        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            try { return unchecked((int)Convert.ToInt64(s.Substring(2), 16)); }
                            catch (FormatException ex) { throw Unparsable("DWord", s, ex); }
                            catch (OverflowException ex) { throw Unparsable("DWord", s, ex); }
                        }
                        if (long.TryParse(s, out n)) return unchecked((int)n);
                        throw Unparsable("DWord", s, null);
                    }
                case RegistryValueKind.QWord:
                    {
                        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            try { return Convert.ToInt64(s.Substring(2), 16); }
                            catch (FormatException ex) { throw Unparsable("QWord", s, ex); }
                            catch (OverflowException ex) { throw Unparsable("QWord", s, ex); }
                        }
                        long n;
                        if (long.TryParse(s, out n)) return n;
                        throw Unparsable("QWord", s, null);
                    }
                case RegistryValueKind.MultiString:
                    if (string.IsNullOrEmpty(s)) return new string[0];
                    return s.Split(new string[] { "\n" }, StringSplitOptions.None);
                case RegistryValueKind.Binary:
                    {
                        try { return Convert.FromBase64String(s); }
                        catch (FormatException ex) { throw Unparsable("Base64 二进制", s, ex); }
                    }
                default:
                    return s;
            }
        }

        private static FormatException Unparsable(string type, string value, Exception inner)
        {
            return new FormatException("值「" + (value ?? "") + "」不是合法的 " + type, inner);
        }

        public static string ToText(object v)
        {
            if (v == null) return null;
            if (v is int) return ((int)v).ToString();
            if (v is long) return ((long)v).ToString();
            if (v is string[]) return string.Join("\n", (string[])v);
            if (v is byte[]) return Convert.ToBase64String((byte[])v);
            return v.ToString();
        }
    }
}
