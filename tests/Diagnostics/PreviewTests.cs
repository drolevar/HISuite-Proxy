using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProxyDiagnostics;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

public sealed class PreviewFallbackForm
{
    public int Calls;
    public string LastBody;
    public const string ReplyBody = "{\"fixture\":\"original-handler\",\"unchanged\":true}";

    public async Task Proxyserver_BeforeRequest(object sender, SessionEventArgs session)
    {
        LastBody = session.HttpClient.Request.HasBody
            ? await session.GetRequestBodyAsString(CancellationToken.None).ConfigureAwait(false) : "";
        Interlocked.Increment(ref Calls);
        session.Ok(ReplyBody, new Dictionary<string, HttpHeader> {
            { "Content-Type", new HttpHeader("Content-Type", "application/json") },
            { "X-Fixture-Fallback", new HttpHeader("X-Fixture-Fallback", "original") }
        }, false);
        session.HttpClient.Response.StatusCode = 202;
    }
}

internal static class PreviewTests
{
    private sealed class Response
    {
        public int Status;
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JArray Cases = new JArray();
    private static readonly List<string> ProxyErrors = new List<string>();
    private static PreviewFallbackForm form;
    private static int port;
    private static int interceptedRequests;
    private static string configFile;
    private static string runDirectory;
    private static JObject candidate;
    private static JObject config;
    private static JObject expectedOffer;
    private static Func<object, SessionEventArgs, Task> dispatch;
    private static object actualForm;
    private static MethodInfo actualWrapper;
    private const string QueryUrl = "http://query.hicloud.com/sp_ard_common/v2/CheckAndAuthorize.action";
    private const string PreviewHeader = "metadata-only-no-install";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: PreviewTests.exe <test-output-directory>");
            runDirectory = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(runDirectory);
            Environment.SetEnvironmentVariable("HISUITE_PROXY_LOG_DIR", Path.Combine(runDirectory, "logs"));
            Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_PREVIEW", "1");
            configFile = Path.Combine(runDirectory, "fixed-offer-preview.json");
            Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG", configFile);
            string metadataDirectory = Path.Combine(runDirectory, "metadata");
            Directory.CreateDirectory(metadataDirectory);
            CreateSyntheticFixtures(metadataDirectory);
            string offerFile = Path.Combine(runDirectory, "synthetic-empty-authorization-offer.json");
            File.WriteAllText(offerFile, expectedOffer.ToString());
            JArray packages = new JArray();
            foreach (JObject component in candidate["components"])
                packages.Add(new JObject {
                    ["url"] = (string)component["folderUrl"],
                    ["manifest"] = Path.GetFileName((string)component["manifest"]["path"]),
                    ["changelog"] = "synthetic-changelog.xml"
                });
            config = new JObject {
                ["metadataDirectory"] = metadataDirectory,
                ["offerFile"] = offerFile,
                ["packages"] = packages
            };
            SaveConfig();
            form = new PreviewFallbackForm();
            dispatch = (sender, session) => FixedOfferPreview.Dispatch(form, sender, session);
            // HTTP only; disable certificate trust and persistence. No system proxy changes or fixed listening port.
            using (ProxyServer proxy = new ProxyServer(false, false, false))
            {
                proxy.CertificateManager.SaveFakeCertificates = false;
                proxy.CertificateManager.OverwritePfxFile = false;
                proxy.CertificateManager.PfxFilePath = Path.Combine(runDirectory, "unused-fixture-root.pfx");
                proxy.ConnectionTimeOutSeconds = 5;
                proxy.ConnectTimeOutSeconds = 3;
                proxy.ExceptionFunc = error => { lock (ProxyErrors) ProxyErrors.Add(error.ToString()); };
                TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                Assert(port != 7777, "Fixture must not use port 7777.");
                ExplicitProxyEndPoint endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port, false);
                proxy.BeforeRequest += async (sender, session) =>
                {
                    Interlocked.Increment(ref interceptedRequests);
                    try { await dispatch(sender, session).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        // Always terminate locally even if the code under test throws: never allow upstream DNS/network.
                        lock (ProxyErrors) ProxyErrors.Add("Dispatch: " + error);
                        session.Ok("fixture-dispatch-exception", new Dictionary<string, HttpHeader>(), false);
                        session.HttpClient.Response.StatusCode = 500;
                    }
                };
                proxy.AddEndPoint(endpoint);
                proxy.Start();
                try { RunCases(); }
                finally { proxy.Stop(); }
            }
            int failed = 0;
            foreach (JObject result in Cases) if (!(bool)result["passed"]) failed++;
            bool passed = failed == 0 && ProxyErrors.Count == 0;
            JObject report = new JObject {
                ["checkedUtc"] = DateTime.UtcNow.ToString("o"),
                ["scope"] = "Built Proxy and its Titanium dependency; synthetic metadata and offers; client sockets connect only to IPv4 loopback. Every request is answered by preview or fixture fallback; no TLS, DNS, upstream, phone, live apps, system proxy or certificate trust changes.",
                ["proxyAssembly"] = typeof(FixedOfferPreview).Assembly.FullName,
                ["titaniumAssembly"] = typeof(ProxyServer).Assembly.FullName,
                ["proxyPort"] = port,
                ["interceptedRequests"] = interceptedRequests,
                ["fallbackRequests"] = form.Calls,
                ["passed"] = Cases.Count - failed,
                ["failed"] = failed,
                ["overallPassed"] = passed,
                ["cases"] = Cases,
                ["proxyErrors"] = new JArray(ProxyErrors)
            };
            File.WriteAllText(Path.Combine(runDirectory, "report.json"), report.ToString());
            Console.WriteLine("RESULT " + (Cases.Count - failed) + " passed, " + failed + " failed; " + ProxyErrors.Count + " proxy errors; " + interceptedRequests + " requests; proxy port " + port);
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (runDirectory != null) File.WriteAllText(Path.Combine(runDirectory, "fatal-error.txt"), error.ToString());
            return 2;
        }
    }

    private static void RunCases()
    {
        Run("ordinary-full-fallback-preserves-request", () => {
            string body = RequestBody("full", true);
            int before = form.Calls;
            Response response = Send(QueryUrl, body);
            AssertFallback(response, body, before);
        });
        Run("rollback-compact-three-component-preview", () => { int before = form.Calls; AssertOffer(Send(QueryUrl, RequestBody("full_back", false)), before); });
        Run("rollback-pretty-three-component-preview", () => { int before = form.Calls; AssertOffer(Send(QueryUrl, RequestBody("full_back", true)), before); });
        foreach (JObject component in candidate["components"])
        {
            JObject current = component;
            Run("cached-manifest-" + (string)current["role"], () => {
                int before = form.Calls;
                Response response = Send(Http((string)current["manifestUrl"]) + "?fixture=metadata", null);
                AssertPreview(response, 200, before);
                string file = Path.Combine((string)config["metadataDirectory"], Path.GetFileName((string)current["manifest"]["path"]));
                Assert(response.Body == File.ReadAllText(file), "Cached manifest bytes/text must match the source.");
                Assert(response.Headers["Content-Type"].StartsWith("application/xml", StringComparison.OrdinalIgnoreCase), "Manifest content type.");
            });
        }
        Run("zip-blocked-selected-cdn-query-string", () => AssertBlocked(Http((string)candidate["components"][0]["packageUrl"]) + "?fixture=download", "METADATA_PREVIEW_DOWNLOADS_DISABLED"));
        Run("zip-blocked-unrelated-host-query-string", () => AssertBlocked("http://fixture.invalid/firmware/test.ZIP?name=anything", "METADATA_PREVIEW_DOWNLOADS_DISABLED"));
        Run("zip-blocked-query-hicloud-host", () => AssertBlocked("http://query.hicloud.com/download/test.zip?fixture=1", "METADATA_PREVIEW_DOWNLOADS_DISABLED"));
        Run("installation-authorization-blocked", () => AssertBlocked("http://query.hicloud.com/sp_ard_common/v2/authorize.action?fixture=1", "METADATA_PREVIEW_INSTALLATION_DISABLED"));
        Run("missing-config-fails-closed", () => {
            Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG", Path.Combine(runDirectory, "does-not-exist.json"));
            try {
                int before = form.Calls;
                Response response = Send(QueryUrl, RequestBody("full_back", false));
                AssertPreview(response, 503, before);
                Assert((string)JObject.Parse(response.Body)["error"] == "PREVIEW_CONFIGURATION_UNAVAILABLE", "Missing configuration reason.");
            } finally { Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG", configFile); }
        });
        Run("preview-disabled-original-fallback", () => {
            Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_PREVIEW", "0");
            Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG", Path.Combine(runDirectory, "does-not-exist.json"));
            try {
                int before = form.Calls;
                string body = RequestBody("full_back", true);
                AssertFallback(Send(QueryUrl, body), body, before);
            } finally {
                Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_PREVIEW", "1");
                Environment.SetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_CONFIG", configFile);
            }
        });
        Run("unknown-selected-metadata-404", () => {
            int before = form.Calls;
            AssertPreview(Send(Http((string)candidate["components"][0]["fullFolderUrl"]) + "fixture-not-cached.xml", null), 404, before);
        });
        Run("nonempty-authorization-offer-denied", () => WithAlteredOffer(offer => offer["authorizeResult"] = "fixture-not-authorized", 503));
        Run("mixed-component-offer-denied", () => WithAlteredOffer(offer => offer["versionPackageCheckResults"][1]["components"][0]["versionID"] = "999999", 503));
        Run("missing-offer-file-denied", () => {
            string original = (string)config["offerFile"];
            config["offerFile"] = Path.Combine(runDirectory, "missing-offer.json");
            SaveConfig();
            try { int before = form.Calls; AssertPreview(Send(QueryUrl, RequestBody("full_back", false)), 503, before); }
            finally { config["offerFile"] = original; SaveConfig(); }
        });
        Run("actual-source-wrapper-preview-on-synthetic-offer", () => {
            LoadActualWrapper();
            dispatch = ActualDispatch;
            try { int before = form.Calls; AssertOffer(Send(QueryUrl, RequestBody("full_back", false)), before); }
            finally { dispatch = (sender, session) => FixedOfferPreview.Dispatch(form, sender, session); }
        });
    }

    private static void LoadActualWrapper()
    {
        if (actualWrapper != null) return;
        Assembly assembly = typeof(FixedOfferPreview).Assembly;
        Type formType = assembly.GetType("HiSuite_Proxy.Form1", true);
        actualWrapper = formType.GetMethod("DiagnosticsBeforeRequest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(actualWrapper != null, "Source event wrapper must exist in the built Proxy.");
        // Deliberately bypass all Form constructors: never create a UI, original proxy, settings or 7777 listener.
        actualForm = FormatterServices.GetUninitializedObject(formType);
        GC.SuppressFinalize(actualForm);
    }

    private static void CreateSyntheticFixtures(string metadataDirectory)
    {
        string[] roles = { "base", "cust", "preload" };
        // The preview guard intentionally requires these IDs. Everything else is synthetic test data.
        string[] ids = { "401403", "401404", "386376" };
        JArray components = new JArray(), groups = new JArray();
        for (int index = 0; index < roles.Length; index++)
        {
            string role = roles[index], root = "https://packages.fixture.invalid/preview/" + role + "/" + ids[index] + "/";
            string manifest = "synthetic-" + role + ".xml";
            File.WriteAllText(Path.Combine(metadataDirectory, manifest),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><root><component><name>FIXTURE-" + role +
                "</name></component><vendorInfo name=\"" + role + "\" subpath=\"\"/><fixture>metadata-only</fixture></root>");
            components.Add(new JObject {
                ["role"] = role, ["folderUrl"] = root, ["fullFolderUrl"] = root + "full/",
                ["manifestUrl"] = root + "full/filelist.xml", ["packageUrl"] = root + "full/synthetic-" + role + ".zip",
                ["manifest"] = new JObject { ["path"] = manifest }
            });
            groups.Add(new JObject {
                ["status"] = "0", ["versionPackageType"] = index + 2,
                ["components"] = new JArray(new JObject {
                    ["versionID"] = ids[index], ["name"] = "Synthetic " + role,
                    ["version"] = "FIXTURE-" + role + " 9.1.0.1", ["url"] = root,
                    ["pointVersion"] = "0", ["versionType"] = "1", ["ruleAttr"] = ""
                })
            });
        }
        candidate = new JObject { ["components"] = components };
        expectedOffer = new JObject { ["status"] = "0", ["authorizeResult"] = "", ["versionPackageCheckResults"] = groups };
        File.WriteAllText(Path.Combine(metadataDirectory, "synthetic-changelog.xml"),
            "<?xml version=\"1.0\"?><root><fixture>synthetic metadata only; no firmware payload</fixture></root>");
    }

    private static Task ActualDispatch(object sender, SessionEventArgs session)
    {
        return (Task)actualWrapper.Invoke(actualForm, new object[] { sender, session });
    }

    private static void WithAlteredOffer(Action<JObject> mutate, int status)
    {
        JObject changed = (JObject)expectedOffer.DeepClone();
        mutate(changed);
        string file = Path.Combine(runDirectory, "invalid-offer.json");
        File.WriteAllText(file, changed.ToString());
        string original = (string)config["offerFile"];
        config["offerFile"] = file;
        SaveConfig();
        try { int before = form.Calls; AssertPreview(Send(QueryUrl, RequestBody("full_back", false)), status, before); }
        finally { config["offerFile"] = original; SaveConfig(); }
    }

    private static string RequestBody(string packageType, bool pretty)
    {
        JObject body = new JObject {
            ["commonRules"] = new JObject { ["PackageType"] = packageType, ["DeviceName"] = "FIXTURE-MODEL" },
            ["versionPackageRules"] = new JArray()
        };
        return body.ToString(pretty ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None);
    }

    private static void AssertOffer(Response response, int fallbackBefore)
    {
        AssertPreview(response, 200, fallbackBefore);
        JObject body = JObject.Parse(response.Body);
        Assert(JToken.DeepEquals(expectedOffer, body), "Preview response must exactly match the generated synthetic offer.");
        Assert(body["authorizeResult"].Type == JTokenType.String && (string)body["authorizeResult"] == "", "Authorization must remain explicitly empty.");
        string[] ids = { "401403", "401404", "386376" };
        JArray groups = (JArray)body["versionPackageCheckResults"];
        Assert(groups.Count == 3, "Exactly three components.");
        for (int index = 0; index < ids.Length; index++)
        {
            JObject item = (JObject)groups[index]["components"][0];
            Assert((string)item["versionID"] == ids[index], "Component ID " + index);
            Assert((string)item["url"] == (string)candidate["components"][index]["folderUrl"], "Component root URL " + index);
        }
    }

    private static void AssertBlocked(string url, string reason)
    {
        int before = form.Calls;
        Response response = Send(url, "{}");
        AssertPreview(response, 403, before);
        Assert((string)JObject.Parse(response.Body)["error"] == reason, "Blocked request reason.");
    }

    private static void AssertPreview(Response response, int status, int fallbackBefore)
    {
        Assert(response.Status == status, "HTTP status: expected " + status + ", got " + response.Status + "; " + response.Body);
        Assert(response.Headers.ContainsKey("X-HiSuite-Proxy-Preview") && response.Headers["X-HiSuite-Proxy-Preview"] == PreviewHeader, "Preview marker header.");
        Assert(form.Calls == fallbackBefore, "Preview must terminate before the fallback handler.");
    }

    private static void AssertFallback(Response response, string body, int before)
    {
        Assert(response.Status == 202 && response.Body == PreviewFallbackForm.ReplyBody, "Original handler response unchanged.");
        Assert(!response.Headers.ContainsKey("X-HiSuite-Proxy-Preview"), "Fallback must not claim preview response.");
        Assert(response.Headers["X-Fixture-Fallback"] == "original", "Original response headers unchanged.");
        Assert(form.Calls == before + 1 && form.LastBody == body, "Original handler receives the exact request body once.");
    }

    private static void Run(string name, Action test)
    {
        JObject result = new JObject { ["name"] = name };
        try { test(); result["passed"] = true; Console.WriteLine("PASS " + name); }
        catch (Exception error) { result["passed"] = false; result["error"] = error.ToString(); Console.WriteLine("FAIL " + name + ": " + error.Message); }
        Cases.Add(result);
    }

    private static void SaveConfig() { File.WriteAllText(configFile, config.ToString()); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Http(string url) { UriBuilder builder = new UriBuilder(url) { Scheme = "http", Port = -1 }; return builder.Uri.AbsoluteUri; }

    // Raw absolute-form HTTP is deliberate: only the loopback IP is connected, and destination names never enter a DNS API.
    private static Response Send(string url, string body)
    {
        Uri uri = new Uri(url);
        byte[] content = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
        string header = (body == null ? "GET" : "POST") + " " + uri.AbsoluteUri + " HTTP/1.1\r\nHost: " + uri.Authority +
            "\r\nConnection: close\r\nContent-Type: application/json;charset=utf-8\r\nContent-Length: " + content.Length + "\r\n\r\n";
        using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
        {
            IAsyncResult connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
            using (connect.AsyncWaitHandle) Assert(connect.AsyncWaitHandle.WaitOne(5000), "Loopback proxy connection timed out.");
            client.EndConnect(connect);
            client.ReceiveTimeout = 6000;
            client.SendTimeout = 6000;
            using (NetworkStream stream = client.GetStream())
            {
                byte[] preamble = Encoding.ASCII.GetBytes(header);
                stream.Write(preamble, 0, preamble.Length);
                if (content.Length != 0) stream.Write(content, 0, content.Length);
                Response response = new Response();
                string statusLine = Line(stream);
                response.Status = Int32.Parse(statusLine.Split(' ')[1]);
                string line;
                while ((line = Line(stream)).Length != 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) response.Headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                }
                using (MemoryStream bytes = new MemoryStream())
                {
                    string transfer, length;
                    if (response.Headers.TryGetValue("Transfer-Encoding", out transfer) && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        while (true)
                        {
                            int count = Convert.ToInt32(Line(stream).Split(';')[0], 16);
                            if (count == 0) { while (Line(stream).Length != 0) { } break; }
                            CopyExactly(stream, bytes, count);
                            Assert(Line(stream).Length == 0, "Chunk terminator.");
                        }
                    }
                    else if (response.Headers.TryGetValue("Content-Length", out length)) CopyExactly(stream, bytes, Int32.Parse(length));
                    else
                    {
                        byte[] buffer = new byte[4096]; int count;
                        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { bytes.Write(buffer, 0, count); Assert(bytes.Length <= 2097152, "Response bound."); }
                    }
                    response.Body = Encoding.UTF8.GetString(bytes.ToArray());
                }
                return response;
            }
        }
    }

    private static string Line(Stream stream)
    {
        using (MemoryStream bytes = new MemoryStream())
        {
            int current;
            while ((current = stream.ReadByte()) >= 0)
            {
                if (current == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.WriteByte((byte)current);
                Assert(bytes.Length <= 16384, "HTTP header line bound.");
            }
            throw new EndOfStreamException("Truncated HTTP headers.");
        }
    }

    private static void CopyExactly(Stream source, Stream target, int length)
    {
        Assert(length >= 0 && target.Length + length <= 2097152, "Response length bound.");
        byte[] buffer = new byte[4096];
        while (length != 0)
        {
            int count = source.Read(buffer, 0, Math.Min(buffer.Length, length));
            if (count == 0) throw new EndOfStreamException("Truncated response body.");
            target.Write(buffer, 0, count); length -= count;
        }
    }
}
