using System;
using System.Reflection;

namespace Dd2Autobattler
{
    /// <summary>Build stamp (git commit, dirty flag) embedded by the csproj at build time.</summary>
    internal static class BuildInfo
    {
        private static bool _read;
        private static string _commit;
        private static bool _dirty;

        public static string GitCommit
        {
            get { Read(); return _commit; }
        }

        public static bool GitDirty
        {
            get { Read(); return _dirty; }
        }

        private static void Read()
        {
            if (_read)
                return;
            _read = true;
            try
            {
                foreach (var attr in typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                {
                    var meta = attr as AssemblyMetadataAttribute;
                    if (meta == null)
                        continue;
                    if (meta.Key == "GitCommit")
                        _commit = string.IsNullOrEmpty(meta.Value) ? null : meta.Value.Trim();
                    else if (meta.Key == "GitDirty")
                        _dirty = string.Equals(meta.Value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // metadata is optional
            }
        }
    }
}
