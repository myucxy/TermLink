using System;

namespace TermLink.UpdateCore
{
    public static class WindowsUpdateVersion
    {
        public static long GetBuild(Version version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (version.Revision >= 0) return version.Revision;
            return version.Build >= 0 ? version.Build : 0;
        }

        public static bool TryGetBuild(string version, out long build)
        {
            if (Version.TryParse(version, out var parsed))
            {
                build = GetBuild(parsed);
                return true;
            }
            build = 0;
            return false;
        }
    }
}
