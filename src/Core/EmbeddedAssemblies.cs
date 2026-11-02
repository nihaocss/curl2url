using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Curl2Url
{
    // 把 MySqlConnector 和它的托管依赖直接嵌进 exe，继续保持“下载一个 exe 就能跑”。
    // 注意：这个类不能在字段/方法签名里出现 MySqlConnector 类型，否则 Main 的 JIT
    // 可能在 AssemblyResolve 注册之前就去加载程序集。
    internal static class EmbeddedAssemblies
    {
        private static readonly Dictionary<string, string> Resources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "MySqlConnector", "MySqlConnector.dll" },
                { "System.Memory", "System.Memory.dll" },
                { "System.Buffers", "System.Buffers.dll" },
                { "System.Threading.Tasks.Extensions", "System.Threading.Tasks.Extensions.dll" },
                { "System.Runtime.CompilerServices.Unsafe", "System.Runtime.CompilerServices.Unsafe.dll" },
                { "System.Numerics.Vectors", "System.Numerics.Vectors.dll" }
            };

        private static readonly Dictionary<string, Assembly> Loaded =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string simpleName;
            try
            {
                simpleName = new AssemblyName(args.Name).Name;
            }
            catch (Exception)
            {
                return null;
            }

            string resourceName;
            if (string.IsNullOrEmpty(simpleName) || !Resources.TryGetValue(simpleName, out resourceName))
            {
                return null;
            }

            lock (Gate)
            {
                Assembly cached;
                if (Loaded.TryGetValue(simpleName, out cached)) return cached;

                try
                {
                    using (Stream stream = typeof(EmbeddedAssemblies).Assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null) return null;
                        byte[] buffer = new byte[stream.Length];
                        int offset = 0;
                        while (offset < buffer.Length)
                        {
                            int read = stream.Read(buffer, offset, buffer.Length - offset);
                            if (read <= 0) break;
                            offset += read;
                        }
                        if (offset != buffer.Length) return null;

                        Assembly assembly = Assembly.Load(buffer);
                        Loaded[simpleName] = assembly;
                        return assembly;
                    }
                }
                catch (Exception)
                {
                    // 资源损坏时返回 null，让 CLR 给出正常的缺程序集错误
                    return null;
                }
            }
        }
    }
}
