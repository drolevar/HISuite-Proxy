using System;
using HiSuite_Proxy;

internal static class FirmwarePathTests
{
    private static int passed, failed;
    // Three real modern CDN path shapes for a base/CUST/preload routing matrix.
    // These are string fixtures only: no requests, firmware downloads, or installation eligibility checks.
    private static readonly string[] Roots =
    {
        "http://update.dbankcdn.com/download/data/pub_13/HWHOTA_hotaMigrate_900_9/73/v3/38886962e86740678df2885e5fd8a541/",
        "http://update.dbankcdn.com/download/data/pub_13/HWHOTA_hotaMigrate_900_9/a0/v3/51a0cd12010744779075e7e89e90e960/",
        "http://update.dbankcdn.com/download/data/pub_13/HWHOTA_hotaMigrate_900_9/76/v3/92a1959e850f4748bc37cff9013d6796/"
    };

    public static int Main()
    {
        for (int request = 0; request < Roots.Length; request++)
            for (int component = 0; component < Roots.Length; component++)
                Check("rollback component matrix " + request + "/" + component,
                    Roots[request].Replace("http:", "https:") + "full/filelist.xml", Roots[component], request == component);

        string root = Roots[0];
        string manifest = root + "full/filelist.xml";
        Check("redirect to another CDN", manifest.Replace("update.dbankcdn.com", "cdn.example.test"), root, true);
        Check("scheme and authority change", manifest.Replace("http://update.dbankcdn.com", "https://mirror.example.test:8443"), root, true);
        Check("catalog root without final slash", manifest, root.TrimEnd('/'), true);
        Check("archive within package directory", root + "full/update_full_base.zip", root, true);
        Check("manifest query is outside the path", manifest + "?signature=x%2Fy&download=1", root, true);
        Check("configured root query is outside the path", manifest, root + "?catalog=401403", true);
        Check("path remains case sensitive", manifest.Replace("HWHOTA", "hwhota"), root, false);
        Check("legacy TDS redirect", "https://download-c.hicloud.com/TDS/data/files/p3/s15/G1234/g123/v401403/f1/full/filelist.xml",
            "http://update.hicloud.com/TDS/data/files/p3/s15/G1234/g123/v401403/f1/", true);
        Check("legacy TDS component collision", "https://download-c.hicloud.com/TDS/data/files/p3/s15/G1234/g123/v401404/f1/full/filelist.xml",
            "http://update.hicloud.com/TDS/data/files/p3/s15/G1234/g123/v401403/f1/", false);
        Check("package directory prefix collision", root.TrimEnd('/') + "-other/full/filelist.xml", root, false);
        Check("root name is not a filename prefix", root.TrimEnd('/') + ".zip", root, false);
        Check("root text only in request query", "https://update.dbankcdn.com/redirect?next=" + manifest, root, false);
        Check("request fragment is outside the path", "https://update.dbankcdn.com/other#" + root, root, false);

        string[] invalid =
        {
            null, "", " ", "not a URL", "/download/data/package/", "//cdn.example.test/package/",
            "https://", "https://bad host/package/", "https://cdn.example.test/%GG/", "https:\\cdn.example.test\\package\\",
            "ftp://cdn.example.test/package/", "file:///C:/package/", "data:text/plain,package", "http://cdn.example.test", "https://cdn.example.test/"
        };
        for (int i = 0; i < invalid.Length; i++)
        {
            Check("empty/malformed/root-only request " + i, invalid[i], root, false);
            Check("empty/malformed/root-only component " + i, manifest, invalid[i], false);
        }
        Check("host-only root with path in its query", manifest, "https://update.dbankcdn.com/?root=/download/data/package/", false);
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, string request, string root, bool expected)
    {
        try
        {
            bool actual = FirmwarePaths.MatchesRequest(request, root);
            if (actual != expected) throw new Exception("Expected " + expected + ", got " + actual + ".");
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + ": " + error.Message); }
    }
}
