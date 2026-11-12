namespace Urlog.Core;

/// <summary>Build-time constants shared by every component.</summary>
public static class CoreInfo
{
    /// <summary>ClickHouse server line this build is validated against. Keep in sync with compose.dev.yml.</summary>
    public const string ClickHouseLts = "26.8";
}
