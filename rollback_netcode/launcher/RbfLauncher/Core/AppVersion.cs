using System.Reflection;

namespace RbfLauncher.Core
{
    /// <summary>The Frame Perfect version as players see it: "alpha_test_v1.0.0".
    ///
    /// One place sets it - FpChannel and Version in RbfLauncher.csproj - and
    /// everything else reads it from the assembly: the window titles, the log,
    /// and the zip name (montar-pacote.ps1 reads the same string off the exe).
    /// That is the point of it: two copies of the launcher that look alike can
    /// be told apart at a glance.</summary>
    public static class AppVersion
    {
        /// <summary>"alpha_test_v1.0.0".</summary>
        public static string Label { get; } =
            typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "v" + typeof(AppVersion).Assembly.GetName().Version;

        /// <summary>Login window: "Frame Perfect (alpha_test_v1.0.0)".</summary>
        public static string ShortTitle => "Frame Perfect (" + Label + ")";

        /// <summary>Main window: "Frame Perfect (alpha_test_v1.0.0) by CyborgRJ".</summary>
        public static string FullTitle => ShortTitle + " by CyborgRJ";
    }
}
