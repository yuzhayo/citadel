namespace Module.Proxy.Features.Sync;

internal sealed record ProxySource(string Name, Uri Url, string DefaultScheme);

internal static class ProxySourceCatalog
{
    public static IReadOnlyList<ProxySource> All { get; } =
    [
        Source("ProxyScrape SOCKS5", "https://api.proxyscrape.com/v3/free-proxy-list/get?request=displayproxies&protocol=socks5&timeout=5000&country=all&ssl=all&anonymity=all", "socks5"),
        Source("ProxyScrape HTTP", "https://api.proxyscrape.com/v3/free-proxy-list/get?request=displayproxies&protocol=http&timeout=5000&country=all&ssl=all&anonymity=all", "http"),
        Source("TheSpeedX SOCKS5", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt", "socks5"),
        Source("TheSpeedX SOCKS4", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks4.txt", "socks4"),
        Source("TheSpeedX HTTP", "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt", "http"),
        Source("monosans SOCKS5", "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt", "socks5"),
        Source("monosans HTTP", "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt", "http"),
        Source("ShiftyTR SOCKS5", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/socks5.txt", "socks5"),
        Source("ShiftyTR HTTP", "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/https.txt", "http"),
        Source("clarketm HTTP", "https://raw.githubusercontent.com/clarketm/proxy-list/master/proxy-list-raw.txt", "http"),
        Source("roosterkid HTTP", "https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt", "http"),
        Source("ProxyList+ HTTP", "https://raw.githubusercontent.com/sunny9577/proxy-scraper/master/generated/http_proxies.txt", "http"),
        Source("sandeepsingh SOCKS5", "https://raw.githubusercontent.com/saschapeschke/proxy-list/main/proxies/socks5.txt", "socks5"),
        Source("sandeepsingh HTTP", "https://raw.githubusercontent.com/saschapeschke/proxy-list/main/proxies/http.txt", "http"),
    ];

    private static ProxySource Source(string name, string url, string scheme) =>
        new(name, new Uri(url, UriKind.Absolute), scheme);
}
