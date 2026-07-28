using System.Reflection;
using System.Runtime.CompilerServices;

namespace AxialSqlTools
{
    /// <summary>
    /// Build-time information embedded by the build process.
    /// The contents of this file are regenerated on every build by the
    /// GenerateBuildInfo target in AxialSqlTools.csproj.
    /// </summary>
    public static class BuildInfo
    {
        public const string BuildTime = "BUILD_TIME_PLACEHOLDER";

        public static string AssemblyVersion
        {
            get
            {
                return typeof(BuildInfo).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion ?? "unknown";
            }
        }
    }
}