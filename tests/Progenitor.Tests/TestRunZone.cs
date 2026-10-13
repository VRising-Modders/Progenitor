using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Progenitor.Tests;

/// <summary>
/// Runs the whole suite as if the machine were in another time zone or culture, so a test that silently depends on the
/// machine's local zone or culture fails on the developer's machine instead of only on CI (a runner in UTC, en-US).
/// <list type="bullet">
/// <item><c>PROGENITOR_TEST_TIME_ZONE</c>: a time zone id (<c>UTC</c>, <c>Tokyo Standard Time</c>,
/// <c>Line Islands Standard Time</c>) that <see cref="TimeZoneInfo.Local"/>, <see cref="DateTime.Now"/> and
/// <see cref="DateTime.ToLocalTime"/> use for this test process.</item>
/// <item><c>PROGENITOR_TEST_CULTURE</c>: a culture name (<c>de-DE</c>, <c>ja-JP</c>) for every test thread.</item>
/// </list>
/// Neither changes a system setting. Unset (the default), the suite runs in the machine's own zone and culture.
/// </summary>
internal static class TestRunZone
{
    internal const string ZoneVariable = "PROGENITOR_TEST_TIME_ZONE";
    internal const string CultureVariable = "PROGENITOR_TEST_CULTURE";

    [ModuleInitializer]
    internal static void Apply()
    {
        string? zone = Environment.GetEnvironmentVariable(ZoneVariable);
        if (!string.IsNullOrWhiteSpace(zone)) OverrideLocalZone(TimeZoneInfo.FindSystemTimeZoneById(zone.Trim()));

        string? culture = Environment.GetEnvironmentVariable(CultureVariable);
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var c = CultureInfo.GetCultureInfo(culture.Trim());
            CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c;
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = c;
        }
    }

    /// <summary>
    /// Replaces the runtime's cached local zone (the field <see cref="TimeZoneInfo.Local"/> reads). The runtime has no public
    /// setter; a missing field fails loudly rather than run the suite in the machine's zone while claiming another.
    /// </summary>
    private static void OverrideLocalZone(TimeZoneInfo zone)
    {
        TimeZoneInfo.ClearCachedData();
        const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var cacheField = typeof(TimeZoneInfo).GetField("s_cachedData", Static)
                         ?? throw new InvalidOperationException(ZoneVariable + ": this runtime has no TimeZoneInfo.s_cachedData.");
        object cache = cacheField.GetValue(null)
                       ?? throw new InvalidOperationException(ZoneVariable + ": TimeZoneInfo.s_cachedData is null.");
        var localField = cache.GetType().GetField("_localTimeZone", Instance)
                         ?? throw new InvalidOperationException(ZoneVariable + ": this runtime has no CachedData._localTimeZone.");
        localField.SetValue(cache, zone);
        if (TimeZoneInfo.Local.Id != zone.Id)
            throw new InvalidOperationException(ZoneVariable + ": TimeZoneInfo.Local is still " + TimeZoneInfo.Local.Id + ".");
    }
}
