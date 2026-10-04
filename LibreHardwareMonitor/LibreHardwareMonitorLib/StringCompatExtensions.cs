// 兼容补丁：net472 / netstandard2.0 的 System.String 没有
// Contains(string, StringComparison) 重载（该重载仅存在于 .NET Core 2.1+）。
// 这里补一个行为等价的扩展方法，使 LibreHardwareMonitor 源码能在旧目标框架下编译，
// 不影响 net8.0 / net9.0（那些框架已有原生重载，实例方法优先绑定）。
#if NETFRAMEWORK || NETSTANDARD2_0
using System;

namespace LibreHardwareMonitor
{
    internal static class StringCompatExtensions
    {
        public static bool Contains(this string source, string value, StringComparison comparison)
        {
            return source.IndexOf(value, comparison) >= 0;
        }
    }
}
#endif
