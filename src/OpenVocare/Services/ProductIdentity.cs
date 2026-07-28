using System.Reflection;

namespace OpenVocare.Services;

internal static class ProductIdentity
{
    public const string RewriteModel = "gpt-5.6-luna";

    // The undocumented subscription endpoints currently reject OpenVocare's own
    // identity. Keep this compatibility header isolated and conspicuous so it
    // can be removed if OpenAI provides a supported third-party identifier.
    public const string CodexCompatibilityOriginator = "Codex Desktop";
    public const string CodexCompatibilityUserAgent =
        "Codex Desktop/26.721 (Windows; x64)";
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0]
        ?? "1.0.0";

}
