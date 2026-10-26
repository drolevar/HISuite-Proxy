using System;

namespace HiSuite_Proxy
{
    public static class FirmwarePaths
    {
        // CDN redirects can change the authority and scheme. The package directory
        // path, including its catalog identifier/hash, selects the component.
        public static bool MatchesRequest(string requestUrl, string firmwareRootUrl)
        {
            Uri request, root;
            if (!TryHttpUri(requestUrl, out request) || !TryHttpUri(firmwareRootUrl, out root))
                return false;

            string directory = root.AbsolutePath.TrimEnd('/');
            if (directory.Length == 0) return false;

            // Include the separator so a sibling such as hash-other cannot match hash.
            // AbsolutePath excludes query parameters and fragments.
            return request.AbsolutePath.StartsWith(directory + "/", StringComparison.Ordinal);
        }

        private static bool TryHttpUri(string value, out Uri uri)
        {
            uri = null;
            return !String.IsNullOrWhiteSpace(value)
                && Uri.TryCreate(value, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.IsWellFormedOriginalString()
                && !String.IsNullOrEmpty(uri.Host);
        }
    }
}
