using System;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>LOCALTS1: the dev Thunderstore stand-in is loopback only, dev only, and the only place http is accepted.</summary>
public sealed class ThunderstoreMirrorTests : IDisposable
{
    public void Dispose() => ThunderstoreMirror.TestOverride = null;

    [Fact]
    public void Unset_uses_thunderstore_and_keeps_https_only()
    {
        ThunderstoreMirror.TestOverride = ("", true);
        Assert.False(ThunderstoreMirror.Active);
        Assert.Equal(CatalogFetcher.DefaultIndexUrl, CatalogFetchOptions.Default.IndexUrl);
        Assert.False(CatalogFetcher.TryCreateHttpsUri("http://127.0.0.1:27450/c/v-rising/api/v1/package/", out _, out string error));
        Assert.Equal(InstallReasons.FetchNotHttps, error);
        Assert.Equal(ThunderstoreAdapter.IconCdnRoot, ThunderstoreMirror.IconRoot);
    }

    [Fact]
    public void A_loopback_url_in_a_dev_tree_redirects_the_index_downloads_icons_and_documents()
    {
        ThunderstoreMirror.TestOverride = ("http://127.0.0.1:27450/", true);
        Assert.True(ThunderstoreMirror.Active);
        Assert.Equal("http://127.0.0.1:27450/c/v-rising/api/v1/package/", CatalogFetchOptions.Default.IndexUrl);
        Assert.True(CatalogFetcher.TryCreateHttpsUri("http://127.0.0.1:27450/package/download/A/B/1.0.0/", out _, out _));
        // Another http host, or the same host on another port, is still refused.
        Assert.False(CatalogFetcher.TryCreateHttpsUri("http://127.0.0.1:9999/x", out _, out _));
        Assert.False(CatalogFetcher.TryCreateHttpsUri("http://example.com/x", out _, out _));
        Assert.True(JoinCatalogInstaller.IsThunderstoreUrl("http://127.0.0.1:27450/package/download/A/B/1.0.0/"));
        Assert.True(JoinCatalogInstaller.IsThunderstoreUrl("https://gcdn.thunderstore.io/live/x.zip"));
        Assert.Equal("http://127.0.0.1:27450/icons/", ThunderstoreMirror.IconRoot);
        Assert.True(ThunderstoreAdapter.TryBuildDocumentUrl("A", "B", "1.0.0", "readme", out string url, out _));
        Assert.Equal("http://127.0.0.1:27450/api/experimental/package/A/B/1.0.0/readme/", url);
    }

    [Fact]
    public void A_non_loopback_url_or_no_dev_signal_leaves_it_off()
    {
        ThunderstoreMirror.TestOverride = ("http://192.168.1.20:27450", true);
        Assert.False(ThunderstoreMirror.Active);
        Assert.Contains("loopback", ThunderstoreMirror.Note);
        ThunderstoreMirror.TestOverride = ("http://localhost:27450", false);
        Assert.False(ThunderstoreMirror.Active);
        Assert.Contains("not a developer tree", ThunderstoreMirror.Note);
        ThunderstoreMirror.TestOverride = ("ftp://127.0.0.1:27450", true);
        Assert.False(ThunderstoreMirror.Active);
    }

    [Fact]
    public void The_servers_base_url_accepts_loopback_http_only_in_a_dev_tree()
    {
        var settings = new RemoteInstallSettings { ThunderstoreBaseUrl = "http://127.0.0.1:27450" };
        ThunderstoreMirror.TestOverride = ("", false);
        Assert.False(settings.BaseUrlValid);
        ThunderstoreMirror.TestOverride = ("", true);
        Assert.True(settings.BaseUrlValid);
        Assert.Equal("http://127.0.0.1:27450/c/v-rising/api/v1/package/", settings.IndexUrl);
        Assert.True(settings.IsThunderstoreUrl("http://127.0.0.1:27450/package/download/A/B/1.0.0/"));
        Assert.False(settings.IsThunderstoreUrl("http://127.0.0.1:1/package/download/A/B/1.0.0/"));
        Assert.False(new RemoteInstallSettings { ThunderstoreBaseUrl = "http://10.0.0.5:27450" }.BaseUrlValid);
    }
}
