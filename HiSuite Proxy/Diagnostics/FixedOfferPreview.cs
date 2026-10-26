using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace ProxyDiagnostics
{
    // Explicit opt-in client-format experiment. It never permits firmware downloads.
    public static class FixedOfferPreview
    {
        public static async Task Dispatch(object form, object sender, SessionEventArgs session)
        {
            if (Environment.GetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_PREVIEW") == "1")
            {
                Log.Checkpoint(session, "preview.request.enter");
                if (await Handle(session).ConfigureAwait(false)) return;
            }
            MethodInfo original = form.GetType().GetMethod("Proxyserver_BeforeRequest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { await ((Task)original.Invoke(form, new object[] { sender, session })).ConfigureAwait(false); }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }

        private static async Task<bool> Handle(SessionEventArgs session)
        {
            Uri uri;
            if (!Uri.TryCreate(session.HttpClient.Request.Url, UriKind.Absolute, out uri)) return false;
            string resource = uri.AbsolutePath.ToLowerInvariant();
            if (resource.EndsWith(".zip") || resource.EndsWith(".app") || resource.EndsWith(".img") || resource.EndsWith(".bin"))
            {
                Reply(session, "preview.download.blocked", "{\"status\":\"-1\",\"error\":\"METADATA_PREVIEW_DOWNLOADS_DISABLED\"}", 403, "application/json");
                return true;
            }
            if (uri.Host.Equals("query.hicloud.com", StringComparison.OrdinalIgnoreCase) && resource.EndsWith("/authorize.action"))
            {
                Reply(session, "preview.install.authorization.blocked", "{\"status\":\"-1\",\"error\":\"METADATA_PREVIEW_INSTALLATION_DISABLED\"}", 403, "application/json");
                return true;
            }
            string configuration = Environment.GetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG");
            if (String.IsNullOrEmpty(configuration)) configuration = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixed-offer-preview.json");
            JObject config;
            try { config = JObject.Parse(File.ReadAllText(configuration)); }
            catch (Exception error)
            {
                Log.ObserveException("preview.configuration.failed", error);
                Reply(session, "preview.configuration.denied", "{\"status\":\"-1\",\"error\":\"PREVIEW_CONFIGURATION_UNAVAILABLE\"}", 503, "application/json");
                return true;
            }
            JArray entries = config["packages"] as JArray;
            if (entries != null) foreach (JObject package in entries)
            {
                Uri root = new Uri((string)package["url"]);
                if (!(uri.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("updatessl.dbankcdn.com", StringComparison.OrdinalIgnoreCase))) continue;
                if (!uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)) continue;
                string relative = uri.AbsolutePath.Substring(root.AbsolutePath.Length);
                string file = relative == "full/filelist.xml" ? (string)package["manifest"] :
                    relative.StartsWith("full/", StringComparison.Ordinal) && relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && relative.IndexOf("changelog", StringComparison.OrdinalIgnoreCase) >= 0 ? (string)package["changelog"] : null;
                if (file == null)
                {
                    Reply(session, "preview.metadata.unavailable", "", 404, "text/plain");
                    return true;
                }
                try
                {
                    string metadataRoot = Path.GetFullPath((string)config["metadataDirectory"]);
                    string target = Path.GetFullPath(Path.Combine(metadataRoot, file));
                    if (!target.StartsWith(metadataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Metadata path escapes preview directory.");
                    if (new FileInfo(target).Length > 1048576) throw new InvalidDataException("Preview metadata exceeded the size limit.");
                    Reply(session, "preview.cached.metadata", File.ReadAllText(target), 200, "application/xml;charset=utf-8");
                }
                catch (Exception error)
                {
                    Log.ObserveException("preview.metadata.failed", error);
                    Reply(session, "preview.metadata.denied", "", 503, "text/plain");
                }
                return true;
            }
            if (!uri.Host.Equals("query.hicloud.com", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.IndexOf("CheckAndAuthorize.action", StringComparison.OrdinalIgnoreCase) < 0 || !session.HttpClient.Request.HasBody) return false;
            string body = await session.GetRequestBodyAsString(CancellationToken.None).ConfigureAwait(false);
            JObject request;
            try { request = JObject.Parse(body); }
            catch { return false; }
            if ((string)request["commonRules"]?["PackageType"] != "full_back") return false;
            Log.ObserveRequest("preview.rollback.trigger", uri.AbsoluteUri, body);
            try
            {
                string offer = File.ReadAllText((string)config["offerFile"]);
                JObject candidate = JObject.Parse(offer);
                if ((string)candidate["status"] != "0" || candidate["authorizeResult"] == null || candidate["authorizeResult"].Type != JTokenType.String || (string)candidate["authorizeResult"] != "")
                    throw new InvalidDataException("Expected the explicitly unauthorised empty-string format probe.");
                JArray results = candidate["versionPackageCheckResults"] as JArray;
                if (results == null || results.Count != 3) throw new InvalidDataException("Expected exactly three preview components.");
                string[] ids = { "401403", "401404", "386376" };
                for (int index = 0; index < results.Count; index++)
                    if ((int)results[index]["versionPackageType"] != index + 2 || (string)results[index]["components"][0]["versionID"] != ids[index])
                        throw new InvalidDataException("Unexpected preview package IDs.");
                Reply(session, "preview.local.rollback.offer", offer, 200, "application/json;charset=utf-8");
            }
            catch (Exception error)
            {
                Log.ObserveException("preview.offer.failed", error);
                Reply(session, "preview.offer.denied", "{\"status\":\"-1\"}", 503, "application/json");
            }
            return true;
        }

        private static void Reply(SessionEventArgs session, string stage, string body, int status, string contentType)
        {
            Log.ObserveResponse(stage, session.HttpClient.Request.Url, body);
            session.Ok(body, new Dictionary<string, HttpHeader> {
                { "Content-Type", new HttpHeader("Content-Type", contentType) },
                { "X-HiSuite-Proxy-Preview", new HttpHeader("X-HiSuite-Proxy-Preview", "metadata-only-no-install") }
            }, false);
            session.HttpClient.Response.StatusCode = status;
        }
    }
}
